namespace Mt5Manager.Domain.Models;

public enum Mt5PackageKind
{
    ExpertAdvisor,
    Indicator
}

public sealed record Mt5PackageInstallTarget(TerminalRegistration Terminal);

public sealed record Mt5PackageInstallOutcome(
    Guid TerminalId,
    string DisplayName,
    bool Success,
    string Message,
    string? DestinationPath);

public sealed record Mt5PackageInstallResult(
    string SourcePath,
    Mt5PackageKind Kind,
    IReadOnlyList<Mt5PackageInstallOutcome> Outcomes)
{
    public bool Success => Outcomes.Count > 0 && Outcomes.All(outcome => outcome.Success);
}
