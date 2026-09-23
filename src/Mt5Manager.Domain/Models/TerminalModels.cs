namespace Mt5Manager.Domain.Models;

public enum TerminalState { Stopped, Running, Busy, Error }
public enum CleanupCategory { Logs, Ticks, History }
public enum DiscoverySource { Process, Shortcut, StandardLocation, Manual }

public enum AccountTradeMode { Demo, Contest, Real }
public enum AlgoTradingState { Enabled, Disabled, Unknown }

public sealed record TerminalAccountSnapshot(
    int ProtocolVersion,
    DateTimeOffset Timestamp,
    string DataPath,
    long Login,
    string AccountName,
    string Server,
    string Company,
    AccountTradeMode TradeMode,
    bool Connected,
    AlgoTradingState GlobalAlgoTrading,
    bool EaTradingAllowed,
    bool AccountTradingAllowed,
    bool AccountExpertAllowed,
    string TerminalPath = "");

public sealed record AlgoTradingControlResult(bool Success, string Message, TerminalAccountSnapshot? Snapshot);

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
