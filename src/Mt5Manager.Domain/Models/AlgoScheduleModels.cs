namespace Mt5Manager.Domain.Models;

[Flags]
public enum AlgoScheduleDays
{
    None = 0,
    Monday = 1 << 0,
    Tuesday = 1 << 1,
    Wednesday = 1 << 2,
    Thursday = 1 << 3,
    Friday = 1 << 4,
    Saturday = 1 << 5,
    Sunday = 1 << 6,
    Weekdays = Monday | Tuesday | Wednesday | Thursday | Friday,
    EveryDay = Weekdays | Saturday | Sunday
}

public sealed record AlgoSchedule(
    Guid Id,
    string Name,
    TimeOnly LocalTime,
    AlgoScheduleDays Days,
    bool Enable,
    IReadOnlyList<Guid> TerminalIds,
    bool IsEnabled,
    DateTimeOffset EffectiveFrom = default,
    int Revision = 0);

public enum AlgoScheduleExecutionOutcome
{
    Succeeded,
    Failed
}

public sealed record AlgoScheduleExecution(
    Guid Id,
    Guid ScheduleId,
    int ScheduleRevision,
    string ScheduleName,
    DateOnly OccurrenceDate,
    DateTimeOffset ScheduledAt,
    Guid TerminalId,
    string TerminalName,
    bool Enable,
    int Attempt,
    DateTimeOffset AttemptedAt,
    AlgoScheduleExecutionOutcome Outcome,
    string Message,
    bool TelegramNotificationAttempted = false,
    bool TelegramNotificationSent = false,
    string? TelegramNotificationMessage = null);

public sealed record AlgoScheduleState(
    IReadOnlyList<AlgoSchedule> Schedules,
    IReadOnlyList<AlgoScheduleExecution> Executions)
{
    public static AlgoScheduleState Empty { get; } = new([], []);
}

public sealed record AlgoScheduleFailureNotification(
    Guid ScheduleId,
    string ScheduleName,
    Guid TerminalId,
    string TerminalName,
    bool Enable,
    DateTimeOffset ScheduledAt,
    string Error);

public sealed record AlgoScheduleNotificationResult(bool Sent, string Message);

public sealed record AlgoSchedulerSnapshot(
    IReadOnlyList<AlgoSchedule> Schedules,
    IReadOnlyList<AlgoScheduleExecution> Executions,
    IReadOnlyList<TerminalRegistration> Terminals,
    bool IsRunning,
    DateTimeOffset? LastEvaluationAt,
    string? LastError,
    string TimeZoneDisplayName);
