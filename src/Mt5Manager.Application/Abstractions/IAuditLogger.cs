using Mt5Manager.Domain.Models;

namespace Mt5Manager.Application.Abstractions;

public enum AuditOutcome { Completed, ForceDeclined, Rejected }

public enum ShutdownMethod { None, Graceful, Force }

public sealed record AuditCategoryOutcome(
    CleanupCategory Category,
    long DeletedFiles,
    long DeletedBytes,
    IReadOnlyList<FileFailure> Failures);

public sealed record AuditRecord(
    DateTimeOffset Timestamp,
    Guid TerminalId,
    string TerminalName,
    string Operation,
    IReadOnlyList<CleanupCategory> Categories,
    bool WasRunning,
    ShutdownMethod ShutdownMethod,
    AuditOutcome Outcome,
    string? Message,
    IReadOnlyList<AuditCategoryOutcome> CategoryOutcomes,
    bool Restarted,
    string? RestartError);

public interface IAuditLogger
{
    Task AppendAsync(AuditRecord record, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AuditRecord>> ReadAsync(Guid terminalId, int limit = 100,
        CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AuditRecord>>([]);
}
