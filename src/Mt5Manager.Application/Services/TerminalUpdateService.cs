using Mt5Manager.Application.Abstractions;
using Mt5Manager.Application.Telegram;
using Mt5Manager.Domain.Models;

namespace Mt5Manager.Application.Services;

public sealed class TerminalUpdateService(ITerminalProcessController process, ITerminalUpdateEvidenceReader evidence,
    IAuditLogger audit, IDelay delay, TimeProvider? timeProvider = null) : ITerminalUpdateService
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim gate = new(1, 1);
    public async Task<IReadOnlyList<TerminalUpdateResult>> UpdateAsync(UpdateRequest request, IProgress<UpdateProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (request.MaxPasses is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(request.MaxPasses));
        var targets = request.Targets.GroupBy(t => t.Id).Select(g => g.First() with { Arguments = g.First().Arguments.ToArray() }).ToArray();
        var results = new List<TerminalUpdateResult>();
        await gate.WaitAsync(cancellationToken);
        try
        {
            for (var index = 0; index < targets.Length && !cancellationToken.IsCancellationRequested; index++)
            {
                var terminal = targets[index];
                progress?.Report(new(index, targets.Length, null));
                var result = await UpdateTargetAsync(terminal, request.MaxPasses, cancellationToken);
                results.Add(result);
                await audit.AppendAsync(new(clock.GetUtcNow(), terminal.Id, terminal.DisplayName, "Update", [], !result.Skipped,
                    result.Skipped ? ShutdownMethod.None : ShutdownMethod.Graceful,
                    result.Success ? AuditOutcome.Completed : AuditOutcome.Rejected, result.Message, [], result.Success && !result.Skipped, result.Success ? null : result.Message), CancellationToken.None);
                progress?.Report(new(index + 1, targets.Length, result));
                if (index + 1 < targets.Length && !cancellationToken.IsCancellationRequested)
                {
                    try { await delay.DelayAsync(TimeSpan.FromSeconds(30), cancellationToken); }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                }
            }
        }
        finally { gate.Release(); }
        return results;
    }

    private async Task<TerminalUpdateResult> UpdateTargetAsync(TerminalRegistration terminal, int maxPasses, CancellationToken token)
    {
        UpdateEvidence? before = null, after = null;
        var passes = 0;
        var history = new List<UpdatePassEvidence>();
        TerminalUpdateResult Result(bool success, bool skipped, string message) => new(terminal.Id, terminal.DisplayName, success, skipped, message, before, after, passes, history.ToArray());
        if (!terminal.DataDirectoryVerified) return Result(false, true, "Skipped: data directory is unverified.");
        try
        {
            if ((await process.GetStateAsync(terminal, token)).State != TerminalState.Running)
                return Result(false, true, "Skipped: terminal is not verifiably running.");
            for (passes = 1; passes <= maxPasses; passes++)
            {
                token.ThrowIfCancellationRequested();
                var since = clock.GetUtcNow();
                before = await evidence.ReadAsync(terminal, since, token);
                if ((await process.GetStateAsync(terminal, token)).State != TerminalState.Running)
                    return Result(false, false, "Terminal is no longer running; no further restart attempted.");
                StopResult stop;
                try { stop = await process.StopAsync(terminal, TimeSpan.FromSeconds(90), false, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    // A cancelled stop may have already closed the process. Recover only after a positive stopped observation.
                    if ((await process.GetStateAsync(terminal, CancellationToken.None)).State == TerminalState.Stopped)
                        await process.StartAsync(terminal, CancellationToken.None);
                    return Result(false, false, "Cancelled during shutdown; recovery attempted if stopped.");
                }
                if (stop.Outcome is not (StopOutcome.ExitedGracefully or StopOutcome.AlreadyStopped))
                    return Result(false, false, stop.Error ?? $"Graceful stop {stop.Outcome}; no start attempted.");
                var state = await process.GetStateAsync(terminal, CancellationToken.None);
                if (state.State != TerminalState.Stopped)
                    return Result(false, false, "Stopped state could not be verified; no start attempted.");
                // Recovery is not cancellable once shutdown has succeeded.
                await process.StartAsync(terminal, CancellationToken.None);
                if (token.IsCancellationRequested) return Result(false, false, "Cancelled; terminal recovery start completed.");
                await delay.DelayAsync(TimeSpan.FromSeconds(90), token);
                if ((await process.GetStateAsync(terminal, token)).State != TerminalState.Running)
                    return Result(false, false, "Terminal is not running after settling; no extra restart attempted.");
                after = await evidence.ReadAsync(terminal, since, token);
                var changed = before.Version is not null && after.Version is not null && before.Version != after.Version;
                var description = $"Pass {passes}: version {before.Version ?? "Unknown"} → {after.Version ?? "Unknown"}; journal {(after.JournalAvailable ? after.UpdateActivity ? "fresh update activity" : "no fresh update activity observed" : "Unknown")}.";
                history.Add(new(passes, before, after, description));
                await audit.AppendAsync(new(clock.GetUtcNow(), terminal.Id, terminal.DisplayName, "Update pass", [], true, ShutdownMethod.Graceful, AuditOutcome.Completed, description, [], true, null), CancellationToken.None);
                if (!changed && !after.UpdateActivity) return Result(true, false, description + " Update availability is not proven.");
                if (passes == maxPasses) return Result(true, false, description + " Maximum passes reached; further updates may remain.");
                await delay.DelayAsync(TimeSpan.FromSeconds(30), token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return Result(false, false, "Cancelled; no further targets will be restarted."); }
        catch (Exception ex) { return Result(false, false, $"Update failed: {ex.Message}; no extra restart attempted."); }
        return Result(false, false, "Update did not complete.");
    }
}
