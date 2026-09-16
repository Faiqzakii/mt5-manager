using System.Diagnostics;
using System.Runtime.InteropServices;
using Mt5Manager.Application.Abstractions;
using Mt5Manager.Domain.Models;

namespace Mt5Manager.Infrastructure.Processes;

public interface IAlgoTradingLease : IDisposable
{
    bool TryMinimize();
}

public enum AlgoTradingInputFailure
{
    None,
    ForegroundActivation,
    WindowRestore,
    ThreadAttachment,
    InputInjection
}

public interface IAlgoTradingInput
{
    AlgoTradingInputFailure TryAcquire(nint window, out IAlgoTradingLease? lease);
}

internal interface IAlgoTradingNativeApi
{
    nint GetForegroundWindow();
    uint GetWindowThreadProcessId(nint window);
    uint GetCurrentThreadId();
    bool PostMessage(nint window, int message, nint wParam, nint lParam);
    bool AttachThreadInput(uint threadId, uint attachThreadId, bool attach);
    bool SetForegroundWindow(nint window);
    bool SendInput(WindowsTerminalAlgoTradingController.NativeInput[] inputs);
}

public sealed class WindowsTerminalAlgoTradingController : ITerminalAlgoTradingController
{
    private const int WmSyscommand = 0x0112;
    private const nint ScRestore = 0xF120;
    private const nint ScMinimize = 0xF020;
    private const uint KeyEvent = 0x0001;
    private const uint KeyEventUp = 0x0002;
    private const ushort VkControl = 0x11;
    private const ushort VkE = 0x45;

    private readonly ITerminalRuntimeInspector inspector;
    private readonly ITerminalProcessController process;
    private readonly Func<int, IReadOnlyList<nint>> windowFinder;
    private readonly IAlgoTradingInput input;
    private readonly Func<DateTimeOffset> clock;
    private readonly TimeSpan pollInterval;
    private readonly TimeSpan timeout;
    private readonly SemaphoreSlim gate = new(1, 1);

    public WindowsTerminalAlgoTradingController(
        ITerminalRuntimeInspector inspector,
        ITerminalProcessController process,
        Func<int, IReadOnlyList<nint>>? windowFinder = null,
        IAlgoTradingInput? input = null,
        Func<DateTimeOffset>? clock = null,
        TimeSpan? pollInterval = null,
        TimeSpan? timeout = null)
    {
        this.inspector = inspector;
        this.process = process;
        this.windowFinder = windowFinder ?? EnumerateTopLevelWindows;
        this.input = input ?? new Win32AlgoTradingInput();
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        this.pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(250);
        this.timeout = timeout ?? TimeSpan.FromSeconds(5);
    }

    public async Task<AlgoTradingControlResult> SetAsync(TerminalRegistration terminal, bool enable, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        var desired = enable ? AlgoTradingState.Enabled : AlgoTradingState.Disabled;
        await gate.WaitAsync(cancellationToken);
        try
        {
            var current = await inspector.ReadAsync(terminal, cancellationToken);
            if (current is null) return new(false, "The MT5 Manager bridge snapshot is unavailable.", null);
            if (current.GlobalAlgoTrading == AlgoTradingState.Unknown) return new(false, "The current Algo Trading state is unknown.", current);
            if (current.GlobalAlgoTrading == desired) return new(true, enable ? "Algo Trading is already enabled." : "Algo Trading is already disabled.", current);

            var state = await process.GetStateAsync(terminal, cancellationToken);
            if (state.State != TerminalState.Running || state.ProcessId is null)
                return new(false, "The terminal must be running before Algo Trading can be changed.", current);

            var windows = windowFinder(state.ProcessId.Value);
            if (windows.Count != 1) return new(false, windows.Count == 0 ? "No matching MetaTrader 5 window was found." : "Several matching MetaTrader 5 windows were found.", current);

            var inputFailure = input.TryAcquire(windows[0], out var lease);
            if (inputFailure != AlgoTradingInputFailure.None || lease is null)
                return new(false, InputFailureMessage(inputFailure), current);

            using (lease)
            {
                var deadline = clock().Add(timeout);
                while (true)
                {
                    var observed = await inspector.ReadAsync(terminal, cancellationToken);
                    if (observed?.GlobalAlgoTrading == desired)
                    {
                        lease.TryMinimize();
                        return new(true, enable ? "Algo Trading was enabled." : "Algo Trading was disabled.", observed);
                    }
                    if (clock() >= deadline) return new(false, enable ? "Algo Trading did not become enabled." : "Algo Trading did not become disabled.", observed ?? current);
                    await Task.Delay(pollInterval, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) { return new(false, exception.Message, null); }
        finally { gate.Release(); }
    }

    private static string InputFailureMessage(AlgoTradingInputFailure failure) => failure switch
    {
        AlgoTradingInputFailure.ForegroundActivation => "The MetaTrader 5 window could not be brought to the foreground.",
        AlgoTradingInputFailure.WindowRestore => "The MetaTrader 5 window could not be restored.",
        AlgoTradingInputFailure.ThreadAttachment => "Input could not be attached to the MetaTrader 5 window.",
        AlgoTradingInputFailure.InputInjection => "The Ctrl+E shortcut could not be injected into the MetaTrader 5 window.",
        _ => "The Ctrl+E shortcut could not be delivered to the terminal window."
    };

    public static IReadOnlyList<uint> RequiredInputAttachments(uint currentThread, uint targetThread, uint foregroundThread)
    {
        var threads = new List<uint>(2);
        if (targetThread != currentThread) threads.Add(targetThread);
        if (foregroundThread != 0 && foregroundThread != currentThread && foregroundThread != targetThread) threads.Add(foregroundThread);
        return threads;
    }

    private static IReadOnlyList<nint> EnumerateTopLevelWindows(int processId)
    {
        var windows = new List<nint>();
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var candidateProcessId);
            if (candidateProcessId == processId && IsWindow(window) && IsWindowVisible(window)) windows.Add(window);
            return true;
        }, nint.Zero);
        return windows;
    }

    internal sealed class Win32AlgoTradingInput : IAlgoTradingInput
    {
        private const int ForegroundAttempts = 3;
        private static readonly TimeSpan ForegroundRetryDelay = TimeSpan.FromMilliseconds(25);
        private readonly IAlgoTradingNativeApi native;
        private readonly Action<TimeSpan> delay;

        public Win32AlgoTradingInput() : this(new Win32NativeApi(), Thread.Sleep) { }

        internal Win32AlgoTradingInput(IAlgoTradingNativeApi native, Action<TimeSpan>? delay = null)
        {
            this.native = native;
            this.delay = delay ?? Thread.Sleep;
        }

        public AlgoTradingInputFailure TryAcquire(nint window, out IAlgoTradingLease? lease)
        {
            lease = null;
            var foreground = native.GetForegroundWindow();
            if (!native.PostMessage(window, WmSyscommand, ScRestore, 0)) return AlgoTradingInputFailure.WindowRestore;

            var targetThread = native.GetWindowThreadProcessId(window);
            var currentThread = native.GetCurrentThreadId();
            var foregroundThread = foreground == nint.Zero ? 0 : native.GetWindowThreadProcessId(foreground);
            var attachedThreads = new List<uint>(2);
            foreach (var thread in RequiredInputAttachments(currentThread, targetThread, foregroundThread))
            {
                if (native.AttachThreadInput(currentThread, thread, true))
                {
                    attachedThreads.Add(thread);
                    continue;
                }

                CleanupFailure(foreground, currentThread, attachedThreads);
                return AlgoTradingInputFailure.ThreadAttachment;
            }

            for (var attempt = 0; attempt < ForegroundAttempts && native.GetForegroundWindow() != window; attempt++)
            {
                native.SetForegroundWindow(window);
                if (native.GetForegroundWindow() != window && attempt + 1 < ForegroundAttempts) delay(ForegroundRetryDelay);
            }

            if (native.GetForegroundWindow() != window)
            {
                CleanupFailure(foreground, currentThread, attachedThreads);
                return AlgoTradingInputFailure.ForegroundActivation;
            }

            if (!native.SendInput([KeyDown(VkControl), KeyDown(VkE), KeyUp(VkE), KeyUp(VkControl)]))
            {
                CleanupFailure(foreground, currentThread, attachedThreads);
                return AlgoTradingInputFailure.InputInjection;
            }

            lease = new ForegroundLease(native, window, foreground, currentThread, attachedThreads);
            return AlgoTradingInputFailure.None;
        }

        private void CleanupFailure(nint foreground, uint currentThread, IReadOnlyList<uint> attachedThreads)
        {
            DetachInputThreads(currentThread, attachedThreads);
            RestoreForeground(foreground);
        }

        private void DetachInputThreads(uint currentThread, IReadOnlyList<uint> attachedThreads)
        {
            for (var index = attachedThreads.Count - 1; index >= 0; index--)
                native.AttachThreadInput(currentThread, attachedThreads[index], false);
        }

        private void RestoreForeground(nint foreground)
        {
            if (foreground != nint.Zero) native.SetForegroundWindow(foreground);
        }

        private sealed class ForegroundLease(IAlgoTradingNativeApi native, nint window, nint foreground, uint currentThread, IReadOnlyList<uint> attachedThreads) : IAlgoTradingLease
        {
            private bool disposed;

            public bool TryMinimize() => !disposed && native.PostMessage(window, WmSyscommand, ScMinimize, 0);

            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                for (var index = attachedThreads.Count - 1; index >= 0; index--)
                    native.AttachThreadInput(currentThread, attachedThreads[index], false);
                if (foreground != nint.Zero) native.SetForegroundWindow(foreground);
            }
        }

        private static NativeInput KeyDown(ushort virtualKey) => new() { Type = KeyEvent, Keyboard = new NativeKeyboardInput { VirtualKey = virtualKey, Flags = 0 } };
        private static NativeInput KeyUp(ushort virtualKey) => new() { Type = KeyEvent, Keyboard = new NativeKeyboardInput { VirtualKey = virtualKey, Flags = KeyEventUp } };
    }

    private sealed class Win32NativeApi : IAlgoTradingNativeApi
    {
        public nint GetForegroundWindow() => WindowsTerminalAlgoTradingController.GetForegroundWindow();
        public uint GetWindowThreadProcessId(nint window) => WindowsTerminalAlgoTradingController.GetWindowThreadProcessId(window, 0);
        public uint GetCurrentThreadId() => WindowsTerminalAlgoTradingController.GetCurrentThreadId();
        public bool PostMessage(nint window, int message, nint wParam, nint lParam) => WindowsTerminalAlgoTradingController.PostMessage(window, message, wParam, lParam);
        public bool AttachThreadInput(uint threadId, uint attachThreadId, bool attach) => WindowsTerminalAlgoTradingController.AttachThreadInput(threadId, attachThreadId, attach);
        public bool SetForegroundWindow(nint window) => WindowsTerminalAlgoTradingController.SetForegroundWindow(window);
        public bool SendInput(NativeInput[] inputs) => WindowsTerminalAlgoTradingController.SendInput(inputs);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NativeInput
    {
        public uint Type;
        public NativeKeyboardInput Keyboard;
        private readonly nuint unionPadding1;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NativeKeyboardInput
    {
        public ushort VirtualKey;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    private delegate bool EnumWindowsProc(nint window, nint parameter);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(nint window, out int processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(nint window, nint processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsWindow(nint window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(nint window, int message, nint wParam, nint lParam);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AttachThreadInput(uint threadId, uint attachThreadId, bool attach);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, NativeInput[] inputs, int size);

    private static bool SendInput(NativeInput[] inputs) => SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>()) == inputs.Length;
}
