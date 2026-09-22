using Mt5Manager.Application.Abstractions;
using Mt5Manager.Domain.Models;

namespace Mt5Manager.Application.Services;

public sealed class AlgoScheduler : IAlgoScheduler
{
    internal static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(30);
    private const int HistoryLimit = 500;

    private readonly IAlgoScheduleStore store;
    private readonly ITerminalRegistry registry;
    private readonly IAlgoTradingService algo;
    private readonly IAlgoScheduleFailureNotifier notifier;
    private readonly TimeProvider clock;
    private readonly TimeZoneInfo timeZone;
    private readonly TimeSpan retryDelay;
    private readonly TimeSpan pollInterval;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly SemaphoreSlim stateGate = new(1, 1);
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private readonly object statusSync = new();
    private CancellationTokenSource? runCancellation;
    private Task? worker;
    private DateTimeOffset? lastEvaluationAt;
    private string? lastError;
    private int disposed;

    public AlgoScheduler(
        IAlgoScheduleStore store,
        ITerminalRegistry registry,
        IAlgoTradingService algo,
        IAlgoScheduleFailureNotifier notifier,
        TimeProvider clock,
        TimeZoneInfo? timeZone = null,
        TimeSpan? retryDelay = null,
        TimeSpan? pollInterval = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        this.algo = algo ?? throw new ArgumentNullException(nameof(algo));
        this.notifier = notifier ?? throw new ArgumentNullException(nameof(notifier));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.timeZone = timeZone ?? TimeZoneInfo.Local;
        this.retryDelay = retryDelay ?? DefaultRetryDelay;
        this.pollInterval = pollInterval ?? DefaultPollInterval;
        this.delay = delay ?? ((duration, cancellationToken) => Task.Delay(duration, cancellationToken));
        if (this.retryDelay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(retryDelay));
        if (this.pollInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(pollInterval));
    }

    public bool IsRunning
    {
        get
        {
            lock (statusSync) return worker is { IsCompleted: false };
        }
    }

    public DateTimeOffset? LastEvaluationAt
    {
        get { lock (statusSync) return lastEvaluationAt; }
    }

    public string? LastError
    {
        get { lock (statusSync) return lastError; }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (statusSync)
            {
                if (worker is { IsCompleted: false }) return;
                runCancellation?.Dispose();
                runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                worker = Task.Run(() => RunAsync(runCancellation.Token), CancellationToken.None);
            }
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task? pending;
        CancellationTokenSource? source;
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (statusSync)
            {
                pending = worker;
                source = runCancellation;
                source?.Cancel();
            }
        }
        finally
        {
            lifecycleGate.Release();
        }

        if (pending is not null)
        {
            try { await pending.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (source?.IsCancellationRequested == true && !cancellationToken.IsCancellationRequested) { }
        }

        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (statusSync)
            {
                if (ReferenceEquals(worker, pending)) worker = null;
                if (ReferenceEquals(runCancellation, source)) runCancellation = null;
            }
            source?.Dispose();
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async Task<AlgoSchedulerSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        await stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
            var terminals = await registry.LoadAsync(cancellationToken).ConfigureAwait(false);
            return new(
                state.Schedules.OrderBy(schedule => schedule.LocalTime).ThenBy(schedule => schedule.Name).ToArray(),
                state.Executions.OrderByDescending(execution => execution.AttemptedAt).ToArray(),
                terminals.OrderBy(terminal => terminal.DisplayName).ToArray(),
                IsRunning,
                LastEvaluationAt,
                LastError,
                timeZone.DisplayName);
        }
        finally
        {
            stateGate.Release();
        }
    }

    public async Task<AlgoSchedule> SaveAsync(AlgoSchedule schedule, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        AlgoSchedule saved;
        await stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
            var terminals = await registry.LoadAsync(cancellationToken).ConfigureAwait(false);
            var terminalIds = schedule.TerminalIds.Distinct().ToArray();
            var missing = terminalIds.Except(terminals.Select(terminal => terminal.Id)).ToArray();
            if (missing.Length > 0) throw new InvalidOperationException("Every scheduled terminal must still be registered.");

            var existing = state.Schedules.FirstOrDefault(item => item.Id == schedule.Id);
            saved = schedule with
            {
                Name = schedule.Name.Trim(),
                TerminalIds = terminalIds,
                EffectiveFrom = clock.GetUtcNow(),
                Revision = (existing?.Revision ?? 0) + 1
            };
            Validate(saved);

            var schedules = state.Schedules.Where(item => item.Id != saved.Id).Append(saved).ToArray();
            ValidateConflicts(schedules);
            await store.SaveAsync(state with { Schedules = schedules }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            stateGate.Release();
        }

        await RunDueAsync(cancellationToken).ConfigureAwait(false);
        return saved;
    }

    public async Task RemoveAsync(Guid scheduleId, CancellationToken cancellationToken = default)
    {
        if (scheduleId == Guid.Empty) throw new ArgumentException("Schedule id must not be empty.", nameof(scheduleId));
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        await stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
            var schedules = state.Schedules.Where(schedule => schedule.Id != scheduleId).ToArray();
            if (schedules.Length == state.Schedules.Count) return;
            await store.SaveAsync(state with { Schedules = schedules }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            stateGate.Release();
        }
    }

    internal async Task RunDueAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        await stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = clock.GetUtcNow();
            var state = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
            ValidateConflicts(state.Schedules);
            var registrations = await registry.LoadAsync(cancellationToken).ConfigureAwait(false);
            var byId = registrations.ToDictionary(terminal => terminal.Id);
            var terminalIds = state.Schedules.Where(schedule => schedule.IsEnabled)
                .SelectMany(schedule => schedule.TerminalIds).Distinct().ToArray();

            foreach (var terminalId in terminalIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var occurrence = LatestOccurrence(state.Schedules, terminalId, now, timeZone);
                if (occurrence is null) continue;

                var attempts = state.Executions.Where(execution =>
                        execution.ScheduleId == occurrence.Schedule.Id &&
                        execution.ScheduleRevision == occurrence.Schedule.Revision &&
                        execution.OccurrenceDate == occurrence.LocalDate &&
                        execution.TerminalId == terminalId)
                    .OrderBy(execution => execution.Attempt)
                    .ToArray();
                if (attempts.Any(attempt => attempt.Outcome == AlgoScheduleExecutionOutcome.Succeeded)) continue;
                if (attempts.Length >= 2)
                {
                    if (!attempts[^1].TelegramNotificationAttempted)
                        state = await NotifyFailureAsync(state, attempts[^1], cancellationToken).ConfigureAwait(false);
                    continue;
                }
                if (attempts.Length == 1 && now - attempts[0].AttemptedAt < retryDelay) continue;

                var attemptNumber = attempts.Length + 1;
                byId.TryGetValue(terminalId, out var registration);
                var execution = await ExecuteAsync(occurrence, registration, attemptNumber, now, cancellationToken).ConfigureAwait(false);
                state = Retain(state with { Executions = state.Executions.Append(execution).ToArray() });
                await store.SaveAsync(state, cancellationToken).ConfigureAwait(false);
                if (execution.Outcome == AlgoScheduleExecutionOutcome.Failed && attemptNumber == 2)
                    state = await NotifyFailureAsync(state, execution, cancellationToken).ConfigureAwait(false);
            }

            SetStatus(now, null);
        }
        finally
        {
            stateGate.Release();
        }
    }

    internal static ScheduledOccurrence? LatestOccurrence(
        IReadOnlyList<AlgoSchedule> schedules,
        Guid terminalId,
        DateTimeOffset now,
        TimeZoneInfo timeZone)
    {
        ScheduledOccurrence? latest = null;
        foreach (var schedule in schedules.Where(schedule => schedule.IsEnabled && schedule.TerminalIds.Contains(terminalId)))
        {
            var candidate = LatestOccurrence(schedule, terminalId, now, timeZone);
            if (candidate is not null && (latest is null || candidate.ScheduledAt.UtcDateTime > latest.ScheduledAt.UtcDateTime))
                latest = candidate;
        }
        return latest;
    }

    internal static void ValidateConflicts(IReadOnlyList<AlgoSchedule> schedules)
    {
        var enabled = schedules.Where(schedule => schedule.IsEnabled).ToArray();
        for (var index = 0; index < enabled.Length; index++)
        for (var otherIndex = index + 1; otherIndex < enabled.Length; otherIndex++)
        {
            var left = enabled[index];
            var right = enabled[otherIndex];
            if (left.LocalTime != right.LocalTime || (left.Days & right.Days) == AlgoScheduleDays.None) continue;
            if (!left.TerminalIds.Intersect(right.TerminalIds).Any()) continue;
            throw new InvalidOperationException($"Schedules '{left.Name}' and '{right.Name}' target the same terminal at the same local time.");
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RunDueAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                SetStatus(clock.GetUtcNow(), exception.Message);
            }

            try
            {
                await delay(pollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task<AlgoScheduleExecution> ExecuteAsync(
        ScheduledOccurrence occurrence,
        TerminalRegistration? terminal,
        int attempt,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (terminal is null)
        {
            return Execution(occurrence, $"Missing terminal ({occurrence.TerminalId})", attempt, now, false,
                "The scheduled terminal is no longer registered.");
        }

        try
        {
            var operation = await algo.SetAsync(
                new AlgoTradingRequest(terminal.Id, occurrence.Schedule.Enable, AlgoOperationSource.Scheduler),
                cancellationToken).ConfigureAwait(false);
            return Execution(occurrence, terminal.DisplayName, attempt, now, operation.Result.Success, operation.Result.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Execution(occurrence, terminal.DisplayName, attempt, now, false,
                $"The scheduled operation failed: {exception.Message}");
        }
    }

    private async Task<AlgoScheduleState> NotifyFailureAsync(
        AlgoScheduleState state,
        AlgoScheduleExecution execution,
        CancellationToken cancellationToken)
    {
        AlgoScheduleNotificationResult result;
        try
        {
            result = await notifier.NotifyAsync(new(
                execution.ScheduleId,
                execution.ScheduleName,
                execution.TerminalId,
                execution.TerminalName,
                execution.Enable,
                execution.ScheduledAt,
                execution.Message), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            result = new(false, $"Telegram notification failed: {exception.Message}");
        }

        var updated = execution with
        {
            TelegramNotificationAttempted = true,
            TelegramNotificationSent = result.Sent,
            TelegramNotificationMessage = result.Message
        };
        state = state with
        {
            Executions = state.Executions.Select(item => item.Id == updated.Id ? updated : item).ToArray()
        };
        await store.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        return state;
    }

    private static AlgoScheduleExecution Execution(
        ScheduledOccurrence occurrence,
        string terminalName,
        int attempt,
        DateTimeOffset now,
        bool success,
        string message) => new(
            Guid.NewGuid(),
            occurrence.Schedule.Id,
            occurrence.Schedule.Revision,
            occurrence.Schedule.Name,
            occurrence.LocalDate,
            occurrence.ScheduledAt,
            occurrence.TerminalId,
            terminalName,
            occurrence.Schedule.Enable,
            attempt,
            now,
            success ? AlgoScheduleExecutionOutcome.Succeeded : AlgoScheduleExecutionOutcome.Failed,
            string.IsNullOrWhiteSpace(message) ? (success ? "Scheduled operation completed." : "Scheduled operation failed.") : message);

    private static ScheduledOccurrence? LatestOccurrence(AlgoSchedule schedule, Guid terminalId, DateTimeOffset now, TimeZoneInfo timeZone)
    {
        var localNow = TimeZoneInfo.ConvertTime(now, timeZone).DateTime;
        var today = DateOnly.FromDateTime(localNow);
        for (var offset = 0; offset <= 7; offset++)
        {
            var date = today.AddDays(-offset);
            if (!Includes(schedule.Days, date.DayOfWeek)) continue;
            var localDateTime = date.ToDateTime(schedule.LocalTime, DateTimeKind.Unspecified);
            var scheduledAt = ResolveLocal(localDateTime, timeZone);
            if (scheduledAt <= now && scheduledAt >= schedule.EffectiveFrom)
                return new(schedule, date, scheduledAt, terminalId);
        }
        return null;
    }

    private static DateTimeOffset ResolveLocal(DateTime localDateTime, TimeZoneInfo timeZone)
    {
        while (timeZone.IsInvalidTime(localDateTime)) localDateTime = localDateTime.AddMinutes(1);
        var offset = timeZone.IsAmbiguousTime(localDateTime)
            ? timeZone.GetAmbiguousTimeOffsets(localDateTime).Max()
            : timeZone.GetUtcOffset(localDateTime);
        return new(localDateTime, offset);
    }

    private static bool Includes(AlgoScheduleDays days, DayOfWeek day) => (days & day switch
    {
        DayOfWeek.Monday => AlgoScheduleDays.Monday,
        DayOfWeek.Tuesday => AlgoScheduleDays.Tuesday,
        DayOfWeek.Wednesday => AlgoScheduleDays.Wednesday,
        DayOfWeek.Thursday => AlgoScheduleDays.Thursday,
        DayOfWeek.Friday => AlgoScheduleDays.Friday,
        DayOfWeek.Saturday => AlgoScheduleDays.Saturday,
        DayOfWeek.Sunday => AlgoScheduleDays.Sunday,
        _ => AlgoScheduleDays.None
    }) != 0;

    private static void Validate(AlgoSchedule schedule)
    {
        if (schedule.Id == Guid.Empty) throw new ArgumentException("Schedule id must not be empty.", nameof(schedule));
        if (string.IsNullOrWhiteSpace(schedule.Name)) throw new ArgumentException("Schedule name is required.", nameof(schedule));
        if (schedule.Days == AlgoScheduleDays.None || (schedule.Days & ~AlgoScheduleDays.EveryDay) != 0)
            throw new ArgumentException("Select at least one valid schedule day.", nameof(schedule));
        if (schedule.TerminalIds is null || schedule.TerminalIds.Count == 0 || schedule.TerminalIds.Any(id => id == Guid.Empty))
            throw new ArgumentException("Select at least one terminal.", nameof(schedule));
    }

    private static AlgoScheduleState Retain(AlgoScheduleState state) => state with
    {
        Executions = state.Executions.OrderByDescending(execution => execution.AttemptedAt).Take(HistoryLimit)
            .OrderBy(execution => execution.AttemptedAt).ToArray()
    };

    private void SetStatus(DateTimeOffset evaluatedAt, string? error)
    {
        lock (statusSync)
        {
            lastEvaluationAt = evaluatedAt;
            lastError = error;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        await StopAsync().ConfigureAwait(false);
        stateGate.Dispose();
        lifecycleGate.Dispose();
    }

    internal sealed record ScheduledOccurrence(
        AlgoSchedule Schedule,
        DateOnly LocalDate,
        DateTimeOffset ScheduledAt,
        Guid TerminalId);
}
