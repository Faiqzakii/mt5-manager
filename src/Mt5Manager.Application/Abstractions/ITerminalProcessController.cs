using Mt5Manager.Domain.Models;

namespace Mt5Manager.Application.Abstractions;

public enum StopOutcome { AlreadyStopped, ExitedGracefully, TimedOut, ForceTerminated, Failed }
public sealed record StopResult(StopOutcome Outcome, string? Error);

public interface ITerminalProcessController
{
    Task<TerminalRuntimeState> GetStateAsync(TerminalRegistration terminal, CancellationToken cancellationToken);
    Task<int> StartAsync(TerminalRegistration terminal, CancellationToken cancellationToken);
    Task<StopResult> StopAsync(TerminalRegistration terminal, TimeSpan timeout, bool force, CancellationToken cancellationToken);
}

public interface ITerminalRuntimeInspector
{
    Task<TerminalAccountSnapshot?> ReadAsync(TerminalRegistration terminal, CancellationToken cancellationToken = default);
}

public interface ITerminalAlgoTradingController
{
    Task<AlgoTradingControlResult> SetAsync(TerminalRegistration terminal, bool enable, CancellationToken cancellationToken = default);
}
