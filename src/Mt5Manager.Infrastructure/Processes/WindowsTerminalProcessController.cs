using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using Mt5Manager.Application.Abstractions;
using Mt5Manager.Domain.Models;
using Mt5Manager.Infrastructure.Discovery;

namespace Mt5Manager.Infrastructure.Processes;

public sealed class WindowsTerminalProcessController : ITerminalProcessController, IDisposable
{
    private static readonly TimeSpan MainWindowGrace = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan KillWait = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);

    private readonly ConcurrentDictionary<Guid, TrackedProcess> _processes = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IProcessHandleSource _handles;
    private readonly IMt5DataDirectoryResolver _dataDirectoryResolver;
    private readonly object _lifetimeLock = new();
    private bool _disposed;
    private int _activeOperations;

    public WindowsTerminalProcessController(
        IProcessHandleSource? handles = null,
        IMt5DataDirectoryResolver? dataDirectoryResolver = null)
    {
        _handles = handles ?? new LimitedRightsProcessHandleSource();
        _dataDirectoryResolver = dataDirectoryResolver ?? new Mt5DataDirectoryResolver();
    }

    public async Task<TerminalRuntimeState> GetStateAsync(TerminalRegistration terminal, CancellationToken cancellationToken)
    {
        EnterOperation();
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                try
                {
                    var scan = Scan(terminal);
                    try
                    {
                        if (scan.Error is not null) return new TerminalRuntimeState(TerminalState.Error, null, scan.Error);
                        return scan.Matches.Count == 0
                            ? new TerminalRuntimeState(TerminalState.Stopped, null, null)
                            : new TerminalRuntimeState(TerminalState.Running, scan.Matches.Min(process => process.Id), null);
                    }
                    finally { scan.Dispose(); }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    return new TerminalRuntimeState(TerminalState.Error, null, exception.Message);
                }
            }
            finally { _gate.Release(); }
        }
        finally { ExitOperation(); }
    }

    public async Task<int> StartAsync(TerminalRegistration terminal, CancellationToken cancellationToken)
    {
        EnterOperation();
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var scan = Scan(terminal);
                try
                {
                    if (scan.Error is not null || scan.Matches.Count > 0)
                        throw new InvalidOperationException(scan.Error ?? $"Terminal '{terminal.DisplayName}' is already running.");

                    var startInfo = new ProcessStartInfo
                    {
                        FileName = WindowsProcessQuery.CanonicalPath(terminal.ExecutablePath),
                        WorkingDirectory = terminal.WorkingDirectory,
                        UseShellExecute = false
                    };
                    foreach (var argument in terminal.Arguments) startInfo.ArgumentList.Add(argument);

                    var process = Process.Start(startInfo) ?? throw new InvalidOperationException("The terminal process could not be started.");
                    var tracked = new TrackedProcess(process);
                    tracked.ExitHandler = (_, _) => RemoveExited(terminal.Id, tracked);
                    _processes[terminal.Id] = tracked;
                    process.Exited += tracked.ExitHandler;
                    process.EnableRaisingEvents = true;
                    return process.Id;
                }
                finally { scan.Dispose(); }
            }
            finally { _gate.Release(); }
        }
        finally { ExitOperation(); }
    }

    public async Task<StopResult> StopAsync(TerminalRegistration terminal, TimeSpan timeout, bool force, CancellationToken cancellationToken)
    {
        if (DataDirectory(terminal) is null)
            return new StopResult(StopOutcome.Failed, $"Terminal '{terminal.DisplayName}' does not have a verified data directory.");
        EnterOperation();
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var scan = Scan(terminal, destructive: true);
                try
                {
                    if (scan.Error is not null) return new StopResult(StopOutcome.Failed, scan.Error);
                    var matches = scan.Matches;
                    if (matches.Count == 0) return new StopResult(StopOutcome.AlreadyStopped, null);

                    var budget = Stopwatch.StartNew();
                    foreach (var process in matches) await CloseMainWindowAsync(process, WindowGrace(timeout), cancellationToken).ConfigureAwait(false);

                    if (await WaitForAllExitAsync(matches, Remaining(timeout, budget), cancellationToken).ConfigureAwait(false))
                        return Release(terminal.Id, matches, StopOutcome.ExitedGracefully);

                    if (!force) return new StopResult(StopOutcome.TimedOut, null);

                    foreach (var process in matches) Kill(process);
                    if (!await WaitForAllExitAsync(matches, KillWait, cancellationToken).ConfigureAwait(false))
                        return new StopResult(StopOutcome.Failed, $"Terminal '{terminal.DisplayName}' could not be terminated.");

                    return Release(terminal.Id, matches, StopOutcome.ForceTerminated);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception exception)
                {
                    return new StopResult(StopOutcome.Failed, exception.Message);
                }
                finally { scan.Dispose(); }
            }
            finally { _gate.Release(); }
        }
        finally { ExitOperation(); }
    }

    // Every live process belonging to this terminal record: the one this controller started plus any duplicates
    // launched elsewhere. Processes the controller started are owned by the tracking table; the rest by the scan.
    private ProcessScan Scan(TerminalRegistration terminal, bool destructive = false)
    {
        var matches = new List<Process>();
        var owned = new List<Process>();
        var known = new HashSet<int>();

        if (!destructive && _processes.TryGetValue(terminal.Id, out var tracked))
        {
            if (IsRunning(tracked.Process))
            {
                matches.Add(tracked.Process);
                known.Add(tracked.Process.Id);
            }
            else Remove(terminal.Id, tracked);
        }

        var expected = WindowsProcessQuery.CanonicalPath(terminal.ExecutablePath);
        var candidates = new List<Process>();
        var unreadableImage = false;
        foreach (var candidate in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(expected)))
        {
            if (known.Contains(candidate.Id)) { candidate.Dispose(); continue; }
            var imagePath = ReadImagePath(candidate.Id);
            if (imagePath is null)
            {
                // A terminating process refuses a handle too; only a live one is genuinely opaque.
                if (IsRunning(candidate)) unreadableImage = true;
                candidate.Dispose();
                continue;
            }

            if (StringComparer.OrdinalIgnoreCase.Equals(WindowsProcessQuery.CanonicalPath(imagePath), expected))
                candidates.Add(candidate);
            else candidate.Dispose();
        }
        for (var index = candidates.Count - 1; index >= 0; index--)
            if (!IsRunning(candidates[index])) { candidates[index].Dispose(); candidates.RemoveAt(index); }
        owned.AddRange(candidates);

        var dataDirectory = DataDirectory(terminal);
        if (dataDirectory is null)
        {
            // Manual or unverified records carry no data directory to discriminate installs by.
            matches.AddRange(candidates);
        }
        else
        {
            var unresolved = 0;
            foreach (var candidate in candidates)
            {
                var candidateData = ReadDataDirectory(candidate.Id, terminal, expected);
                if (candidateData is null) { unresolved++; continue; }
                if (StringComparer.OrdinalIgnoreCase.Equals(candidateData, dataDirectory)) matches.Add(candidate);
            }

            if (unresolved > 0)
            {
                if (destructive)
                {
                    var message = $"The process identity of '{expected}' could not be verified because at least one matching data directory could not be read.";
                    return new ProcessScan([], owned, message);
                }

                // Non-destructive discovery may report one executable-only candidate as running.
                if (candidates.Count == 1) matches.Add(candidates[0]);
                else
                {
                    var message = $"The running state of '{expected}' is ambiguous: {candidates.Count} processes match it and at least one data directory could not be read.";
                    return new ProcessScan([], owned, message);
                }
            }
        }

        return matches.Count == 0 && unreadableImage
            ? new ProcessScan([], owned, $"The running state of '{expected}' could not be verified.")
            : new ProcessScan(matches, owned, null);
    }

    private string? ReadImagePath(int processId)
    {
        var handle = _handles.Open(processId);
        if (handle == nint.Zero) return null;
        try { return WindowsProcessQuery.ReadImagePath(handle); }
        finally { _handles.Close(handle); }
    }

    private string? ReadDataDirectory(int processId, TerminalRegistration terminal, string executablePath)
    {
        var handle = _handles.Open(processId);
        if (handle == nint.Zero) return null;
        try
        {
            var commandLine = WindowsProcessQuery.ReadCommandLine(handle);
            if (commandLine is null) return null;
            var arguments = WindowsProcessQuery.Split(commandLine);
            if (arguments.Count > 0) arguments.RemoveAt(0);
            var explicitData = WindowsProcessQuery.FindDataDirectory(arguments);
            if (explicitData is not null) return WindowsProcessQuery.CanonicalPath(explicitData);

            return _dataDirectoryResolver.Resolve(terminal with
            {
                ExecutablePath = executablePath,
                DataDirectory = string.Empty,
                Arguments = arguments,
                DataDirectoryVerified = false
            });
        }
        catch (Win32Exception) { return null; }
        finally { _handles.Close(handle); }
    }

    private static string? DataDirectory(TerminalRegistration terminal) =>
        terminal.DataDirectoryVerified && !string.IsNullOrWhiteSpace(terminal.DataDirectory)
            ? WindowsProcessQuery.CanonicalPath(terminal.DataDirectory)
            : null;

    private static async Task CloseMainWindowAsync(Process process, TimeSpan grace, CancellationToken cancellationToken)
    {
        if (CloseMainWindow(process)) return;
        var budget = Stopwatch.StartNew();
        while (budget.Elapsed < grace)
        {
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            if (!IsRunning(process)) return;
            // A terminal that has only just been launched may not own its main window yet.
            if (CloseMainWindow(process)) return;
        }
    }

    private static bool CloseMainWindow(Process process)
    {
        try
        {
            process.Refresh();
            return process.CloseMainWindow();
        }
        catch (InvalidOperationException) { return false; }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!IsRunning(process)) return;
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
    }

    private static async Task<bool> WaitForAllExitAsync(IReadOnlyList<Process> processes, TimeSpan wait, CancellationToken cancellationToken)
    {
        var budget = Stopwatch.StartNew();
        while (true)
        {
            if (processes.All(process => !IsRunning(process))) return true;
            if (budget.Elapsed >= wait) return false;
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsRunning(Process process)
    {
        try { return !process.HasExited; }
        catch (InvalidOperationException) { return false; }
    }

    private static TimeSpan WindowGrace(TimeSpan timeout) =>
        timeout >= TimeSpan.Zero && timeout < MainWindowGrace ? timeout : MainWindowGrace;

    private static TimeSpan Remaining(TimeSpan timeout, Stopwatch budget)
    {
        if (timeout < TimeSpan.Zero) return timeout;
        var left = timeout - budget.Elapsed;
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    private StopResult Release(Guid registrationId, IReadOnlyList<Process> matches, StopOutcome outcome)
    {
        foreach (var process in matches) RemoveTracked(registrationId, process);
        return new StopResult(outcome, null);
    }

    // The Exited callback runs either on a thread pool thread while holding the Process object's
    // internal lock, or synchronously on whatever thread polls HasExited — which may be an operation
    // that already owns the gate. Blocking on the gate here would therefore deadlock. Queue the
    // removal behind the gate as a counted operation instead: Dispose waits for in-flight removals
    // before it disposes the semaphore, so the queued continuation can never touch a disposed gate.
    private void RemoveExited(Guid registrationId, TrackedProcess tracked) =>
        _ = RemoveExitedAsync(registrationId, tracked);

    private async Task RemoveExitedAsync(Guid registrationId, TrackedProcess tracked)
    {
        if (!TryEnterOperation()) return;
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try { Remove(registrationId, tracked); }
            finally { _gate.Release(); }
        }
        finally { ExitOperation(); }
    }

    private void RemoveTracked(Guid registrationId, Process process)
    {
        if (_processes.TryGetValue(registrationId, out var tracked) && tracked.Process.Id == process.Id)
            Remove(registrationId, tracked);
    }

    private void Remove(Guid registrationId, TrackedProcess tracked)
    {
        if (!_processes.TryRemove(new KeyValuePair<Guid, TrackedProcess>(registrationId, tracked))) return;
        tracked.Process.Exited -= tracked.ExitHandler;
        tracked.Process.Dispose();
    }

    public void Dispose()
    {
        lock (_lifetimeLock)
        {
            if (_disposed) return;
            _disposed = true;
            while (_activeOperations != 0) Monitor.Wait(_lifetimeLock);
        }

        foreach (var entry in _processes.ToArray()) Remove(entry.Key, entry.Value);
        _gate.Dispose();
    }

    private void EnterOperation()
    {
        if (!TryEnterOperation()) throw new ObjectDisposedException(nameof(WindowsTerminalProcessController));
    }

    private bool TryEnterOperation()
    {
        lock (_lifetimeLock)
        {
            if (_disposed) return false;
            _activeOperations++;
            return true;
        }
    }

    private void ExitOperation()
    {
        lock (_lifetimeLock)
        {
            _activeOperations--;
            if (_disposed && _activeOperations == 0) Monitor.PulseAll(_lifetimeLock);
        }
    }

    private sealed class TrackedProcess(Process process)
    {
        public Process Process { get; } = process;
        public EventHandler ExitHandler { get; set; } = null!;
    }

    private sealed class ProcessScan(List<Process> matches, List<Process> owned, string? error) : IDisposable
    {
        public IReadOnlyList<Process> Matches { get; } = matches;
        public string? Error { get; } = error;

        public void Dispose()
        {
            foreach (var process in owned) process.Dispose();
        }
    }
}
