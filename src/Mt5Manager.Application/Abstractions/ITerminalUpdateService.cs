using Mt5Manager.Domain.Models;

namespace Mt5Manager.Application.Abstractions;

public sealed record UpdateEvidence(string? Version, bool JournalAvailable, bool UpdateActivity, DateTimeOffset ObservedAt, string? Detail = null);
public sealed record UpdatePassEvidence(int Pass, UpdateEvidence? Before, UpdateEvidence? After, string Message);
public sealed record TerminalUpdateResult(Guid TerminalId, string TerminalName, bool Success, bool Skipped, string Message, UpdateEvidence? Before, UpdateEvidence? After, int Passes, IReadOnlyList<UpdatePassEvidence>? History = null);
public sealed record UpdateProgress(int Completed, int Total, TerminalUpdateResult? Result);
public sealed record UpdateRequest(IReadOnlyList<TerminalRegistration> Targets, int MaxPasses = 3);
public interface ITerminalUpdateEvidenceReader
{
    Task<UpdateEvidence> ReadAsync(TerminalRegistration terminal, DateTimeOffset since, CancellationToken cancellationToken = default);
}
public interface ITerminalUpdateService
{
    Task<IReadOnlyList<TerminalUpdateResult>> UpdateAsync(UpdateRequest request, IProgress<UpdateProgress>? progress = null, CancellationToken cancellationToken = default);
}
