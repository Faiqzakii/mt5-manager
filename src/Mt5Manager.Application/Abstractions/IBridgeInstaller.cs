using Mt5Manager.Domain.Models;

namespace Mt5Manager.Application.Abstractions;

public interface IBridgeInstaller
{
    /// <summary>Current installation state, or <c>null</c> when the terminal has no addressable MQL5 folder.</summary>
    Task<BridgeInstallationStatus?> InspectAsync(TerminalRegistration terminal, CancellationToken cancellationToken = default);

    Task<BridgeInstallationResult> InstallAsync(TerminalRegistration terminal, CancellationToken cancellationToken = default);
}
