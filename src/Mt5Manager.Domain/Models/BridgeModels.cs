namespace Mt5Manager.Domain.Models;

public enum BridgeInstallationState { NotInstalled, SourceOutdated, NotCompiled, Installed }

public sealed record BridgeInstallationStatus(
    BridgeInstallationState State,
    string SourcePath,
    string CompiledPath,
    bool CanCompile);

public sealed record BridgeInstallationResult(bool Success, string Message, BridgeInstallationStatus? Status);
