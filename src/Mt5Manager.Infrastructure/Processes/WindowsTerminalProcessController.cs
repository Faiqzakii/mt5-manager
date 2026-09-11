using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Mt5Manager.Application.Abstractions;
using Mt5Manager.Domain.Models;
using Mt5Manager.Infrastructure.Discovery;

namespace Mt5Manager.Infrastructure.Processes;

public sealed class WindowsTerminalProcessController : ITerminalProcessController
{
    private readonly ConcurrentDictionary<Guid, TrackedProcess> _processes = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IProcessHandleSource _handles;

    public WindowsTerminalProcessController(IProcessHandleSource? handles = null) =>
        _handles = handles ?? new LimitedRightsProcessHandleSource();

    public async Task<TerminalRuntimeState> GetStateAsync(TerminalRegistration terminal, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            try
            {
                var match = FindRunningProcess(terminal);
                try
                {
                    if (match.Error is not null) return new TerminalRuntimeState(TerminalState.Error, null, match.Error);
                    return match.Process is null
                        ? new TerminalRuntimeState(TerminalState.Stopped, null, null)
                        : new TerminalRuntimeState(TerminalState.Running, match.Process.Id, null);
                }
                finally { DisposeMatch(match); }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return new TerminalRuntimeState(TerminalState.Error, null, exception.Message);
            }
        }
        finally { _gate.Release(); }
    }

    public async Task<int> StartAsync(TerminalRegistration terminal, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var match = FindRunningProcess(terminal);
            try
            {
                if (match.Process is not null || match.Error is not null)
                    throw new InvalidOperationException(match.Error ?? $"Terminal '{terminal.DisplayName}' is already running.");

                var startInfo = new ProcessStartInfo
                {
                    FileName = CanonicalPath(terminal.ExecutablePath),
                    WorkingDirectory = CanonicalPath(terminal.WorkingDirectory),
                    UseShellExecute = false
                };
                foreach (var argument in terminal.Arguments) startInfo.ArgumentList.Add(argument);

                var process = Process.Start(startInfo) ?? throw new InvalidOperationException("The terminal process could not be started.");
                var tracked = new TrackedProcess(process);
                _processes[terminal.Id] = tracked;
                process.Exited += (_, _) => _ = RemoveExitedAsync(terminal.Id, tracked);
                process.EnableRaisingEvents = true;
                return process.Id;
            }
            finally { DisposeMatch(match); }
        }
        finally { _gate.Release(); }
    }

    public async Task<StopResult> StopAsync(TerminalRegistration terminal, TimeSpan timeout, bool force, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var match = FindRunningProcess(terminal);
            try
            {
                if (match.Error is not null) return new StopResult(StopOutcome.Failed, match.Error);
                var process = match.Process;
                if (process is null) return new StopResult(StopOutcome.AlreadyStopped, null);

                process.CloseMainWindow();
                if (await WaitForExitAsync(process, timeout, cancellationToken))
                {
                    RemoveTracked(terminal.Id, process);
                    return new StopResult(StopOutcome.ExitedGracefully, null);
                }

                if (!force) return new StopResult(StopOutcome.TimedOut, null);

                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(cancellationToken);
                RemoveTracked(terminal.Id, process);
                return new StopResult(StopOutcome.ForceTerminated, null);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                return new StopResult(StopOutcome.Failed, exception.Message);
            }
            finally { DisposeMatch(match); }
        }
        finally { _gate.Release(); }
    }

    // A process the controller started is owned by the tracking table; any other match is owned by this lookup.
    private ProcessMatch FindRunningProcess(TerminalRegistration terminal)
    {
        if (_processes.TryGetValue(terminal.Id, out var tracked))
        {
            if (!tracked.Process.HasExited) return new ProcessMatch(tracked.Process, false, null);
            Remove(terminal.Id, tracked);
        }

        var expected = CanonicalPath(terminal.ExecutablePath);
        var indeterminate = false;
        foreach (var candidate in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(expected)))
        {
            var actual = ReadExecutablePath(candidate.Id);
            if (actual is null)
            {
                candidate.Dispose();
                indeterminate = true;
                continue;
            }

            if (StringComparer.OrdinalIgnoreCase.Equals(CanonicalPath(actual), expected))
                return new ProcessMatch(candidate, true, null);
            candidate.Dispose();
        }

        return indeterminate
            ? new ProcessMatch(null, false, $"The running state of '{expected}' could not be verified.")
            : new ProcessMatch(null, false, null);
    }

    private string? ReadExecutablePath(int processId)
    {
        var handle = _handles.Open(processId);
        if (handle == nint.Zero) return null;
        try
        {
            var size = 32768;
            var buffer = new StringBuilder(size);
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString() : null;
        }
        finally { _handles.Close(handle); }
    }

    private async Task RemoveExitedAsync(Guid registrationId, TrackedProcess tracked)
    {
        try
        {
            await _gate.WaitAsync();
            try { Remove(registrationId, tracked); }
            finally { _gate.Release(); }
        }
        catch (ObjectDisposedException) { }
    }

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private void RemoveTracked(Guid registrationId, Process process)
    {
        if (_processes.TryGetValue(registrationId, out var tracked) && ReferenceEquals(tracked.Process, process))
            Remove(registrationId, tracked);
    }

    private void Remove(Guid registrationId, TrackedProcess tracked)
    {
        if (_processes.TryRemove(new KeyValuePair<Guid, TrackedProcess>(registrationId, tracked)))
            tracked.Process.Dispose();
    }

    private static void DisposeMatch(ProcessMatch match)
    {
        if (match.Owned) match.Process?.Dispose();
    }

    private static string CanonicalPath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    // Query rights are all that may be requested: terminals run elevated, the manager does not.
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(nint process, int flags, StringBuilder name, ref int size);

    private sealed record TrackedProcess(Process Process);

    private sealed record ProcessMatch(Process? Process, bool Owned, string? Error);
}
