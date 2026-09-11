namespace Mt5Manager.Domain.Models;

public enum TerminalState { Stopped, Running, Busy, Error }
public enum CleanupCategory { Logs, Ticks, History }
public enum DiscoverySource { Process, Shortcut, StandardLocation, Manual }

public sealed record TerminalRegistration(
    Guid Id,
    string DisplayName,
    string ExecutablePath,
    string DataDirectory,
    string WorkingDirectory,
    IReadOnlyList<string> Arguments,
    DiscoverySource Source,
    bool DataDirectoryVerified);

public sealed record TerminalRuntimeState(TerminalState State, int? ProcessId, string? Error);
public sealed record CategoryUsage(CleanupCategory Category, long FileCount, long Bytes);
public sealed record FileFailure(string Path, string Error);
public sealed record CleanupCategoryResult(CleanupCategory Category, long DeletedFiles, long DeletedBytes, IReadOnlyList<FileFailure> Failures);
public sealed record CleanupResult(bool WasRunning, bool Restarted, IReadOnlyList<CleanupCategoryResult> Categories, string? RestartError);
