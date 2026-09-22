using System.Runtime.InteropServices;
using Mt5Manager.Application.Abstractions;
using Mt5Manager.Domain.Models;

namespace Mt5Manager.Infrastructure.Processes;

/// <summary>
/// Delivers a MetaTrader 5 command id to the terminal frame window.
/// </summary>
public interface IAlgoTradingCommandSender
{
    bool Send(nint window, int command);
}

public sealed class WindowsTerminalAlgoTradingController : ITerminalAlgoTradingController
{
    /// <summary>
    /// MetaTrader 5 command id behind the "Algo Trading" toolbar button. Posting it as a
    /// <c>WM_COMMAND</c> to the terminal frame window toggles global Algo Trading without
    /// requiring the window to own the foreground.
    /// </summary>
    internal const int AlgoTradingCommandId = 32851;

    private const int WmCommand = 0x0111;
    private const uint GwOwner = 4;

    private readonly ITerminalRuntimeInspector inspector;
    private readonly ITerminalProcessController process;
    private readonly Func<int, IReadOnlyList<nint>> windowFinder;
    private readonly IAlgoTradingCommandSender commands;
    private readonly Func<nint, nint> ownerFinder;
    private readonly Func<DateTimeOffset> clock;
    private readonly TimeSpan pollInterval;
    private readonly TimeSpan timeout;
    private readonly SemaphoreSlim gate = new(1, 1);

    public WindowsTerminalAlgoTradingController(
        ITerminalRuntimeInspector inspector,
        ITerminalProcessController process,
        Func<int, IReadOnlyList<nint>>? windowFinder = null,
        IAlgoTradingCommandSender? commands = null,
        Func<DateTimeOffset>? clock = null,
        TimeSpan? pollInterval = null,
        TimeSpan? timeout = null,
        Func<nint, nint>? ownerFinder = null)
    {
        this.inspector = inspector;
        this.process = process;
        this.windowFinder = windowFinder ?? EnumerateTopLevelWindows;
        this.commands = commands ?? new Win32AlgoTradingCommandSender();
        this.ownerFinder = ownerFinder ?? GetWindowOwner;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        this.pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(250);
        this.timeout = timeout ?? TimeSpan.FromSeconds(10);
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

            var commandTarget = nint.Zero;
            var matchingFrameCount = 0;
            foreach (var window in windowFinder(state.ProcessId.Value))
            {
                if (ownerFinder(window) != nint.Zero) continue;
                commandTarget = window;
                if (++matchingFrameCount > 1) break;
            }

            if (matchingFrameCount != 1)
                return new(false, matchingFrameCount == 0 ? "No matching MetaTrader 5 window was found." : "Several matching MetaTrader 5 windows were found.", current);

            if (!commands.Send(commandTarget, AlgoTradingCommandId))
                return new(false, "The Algo Trading command could not be posted to the MetaTrader 5 window.", current);
            var deadline = clock().Add(timeout);
            while (true)
            {
                var observed = await inspector.ReadAsync(terminal, cancellationToken);
                if (observed?.GlobalAlgoTrading == desired)
                    return new(true, enable ? "Algo Trading was enabled." : "Algo Trading was disabled.", observed);
                if (clock() >= deadline) return new(false, enable ? "Algo Trading did not become enabled." : "Algo Trading did not become disabled.", observed ?? current);
                await Task.Delay(pollInterval, cancellationToken);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) { return new(false, exception.Message, null); }
        finally { gate.Release(); }
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
    private static nint GetWindowOwner(nint window) => GetWindow(window, GwOwner);

    private sealed class Win32AlgoTradingCommandSender : IAlgoTradingCommandSender
    {
        public bool Send(nint window, int command) => PostMessage(window, WmCommand, command, 0);
    }

    private delegate bool EnumWindowsProc(nint window, nint parameter);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(nint window, out int processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsWindow(nint window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(nint window, int message, nint wParam, nint lParam);
}
