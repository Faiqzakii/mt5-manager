using System.Collections.Concurrent;
using System.Diagnostics;
using Mt5Manager.Application.Abstractions;
using Mt5Manager.Domain.Models;

namespace Mt5Manager.Infrastructure.Processes;

public sealed class WindowsTerminalProcessController : ITerminalProcessController
{
    private readonly ConcurrentDictionary<Guid, TrackedProcess> _processes = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<TerminalRuntimeState> GetStateAsync(TerminalRegistration terminal, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var process = FindRunningProcess(terminal);
            return process is null
                ? new TerminalRuntimeState(TerminalState.Stopped, null, null)
                : new TerminalRuntimeState(TerminalState.Running, process.Id, null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new TerminalRuntimeState(TerminalState.Error, null, exception.Message);
        }
        finally { _gate.Release(); }
    }

    public async Task<int> StartAsync(TerminalRegistration terminal, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (FindRunningProcess(terminal) is not null)
                throw new InvalidOperationException($"Terminal '{terminal.DisplayName}' is already running.");

            var startInfo = new ProcessStartInfo
            {
                FileName = CanonicalPath(terminal.ExecutablePath),
                WorkingDirectory = CanonicalPath(terminal.WorkingDirectory),
                UseShellExecute = false
            };
            foreach (var argument in terminal.Arguments) startInfo.ArgumentList.Add(argument);

            var process = Process.Start(startInfo) ?? throw new InvalidOperationException("The terminal process could not be started.");
            var tracked = new TrackedProcess(process, CanonicalPath(terminal.ExecutablePath));
            if (!_processes.TryAdd(terminal.Id, tracked))
            {
                process.Kill(entireProcessTree: true);
                process.Dispose();
                throw new InvalidOperationException($"Terminal '{terminal.DisplayName}' is already running.");
            }
            return process.Id;
        }
        finally { _gate.Release(); }
    }

    public async Task<StopResult> StopAsync(TerminalRegistration terminal, TimeSpan timeout, bool force, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var process = FindRunningProcess(terminal);
            if (process is null) return new StopResult(StopOutcome.AlreadyStopped, null);

            process.CloseMainWindow();
            if (await WaitForExitAsync(process, timeout, cancellationToken))
            {
                Remove(terminal.Id, _processes[terminal.Id]);
                return new StopResult(StopOutcome.ExitedGracefully, null);
            }

            if (!force) return new StopResult(StopOutcome.TimedOut, null);

            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(cancellationToken);
            Remove(terminal.Id, _processes[terminal.Id]);
            return new StopResult(StopOutcome.ForceTerminated, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            return new StopResult(StopOutcome.Failed, exception.Message);
        }
        finally { _gate.Release(); }
    }

    private Process? FindRunningProcess(TerminalRegistration terminal)
    {
        if (!_processes.TryGetValue(terminal.Id, out var tracked)) return null;
        try
        {
            if (tracked.Process.HasExited)
            {
                Remove(terminal.Id, tracked);
                return null;
            }

            var actualPath = CanonicalPath(tracked.Process.MainModule?.FileName
                ?? throw new InvalidOperationException("Cannot determine the terminal executable path."));
            return StringComparer.OrdinalIgnoreCase.Equals(actualPath, CanonicalPath(terminal.ExecutablePath)) &&
                   StringComparer.OrdinalIgnoreCase.Equals(actualPath, tracked.ExecutablePath)
                ? tracked.Process
                : null;
        }
        catch (InvalidOperationException)
        {
            Remove(terminal.Id, tracked);
            return null;
        }
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

    private void Remove(Guid registrationId, TrackedProcess tracked)
    {
        if (_processes.TryRemove(new KeyValuePair<Guid, TrackedProcess>(registrationId, tracked)))
            tracked.Process.Dispose();
    }

    private static string CanonicalPath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private sealed record TrackedProcess(Process Process, string ExecutablePath);
}
