using Mt5Manager.Domain.Models;

namespace Mt5Manager.Application.Abstractions;

public interface IAlgoScheduleStore
{
    Task<AlgoScheduleState> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(AlgoScheduleState state, CancellationToken cancellationToken = default);
}

public interface IAlgoScheduleFailureNotifier
{
    Task<AlgoScheduleNotificationResult> NotifyAsync(
        AlgoScheduleFailureNotification notification,
        CancellationToken cancellationToken = default);
}

public interface IAlgoScheduler : IAsyncDisposable
{
    bool IsRunning { get; }
    DateTimeOffset? LastEvaluationAt { get; }
    string? LastError { get; }

    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task<AlgoSchedulerSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
    Task<AlgoSchedule> SaveAsync(AlgoSchedule schedule, CancellationToken cancellationToken = default);
    Task RemoveAsync(Guid scheduleId, CancellationToken cancellationToken = default);
}
