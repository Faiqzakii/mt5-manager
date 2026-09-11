using Mt5Manager.Domain.Models;

namespace Mt5Manager.Application.Abstractions;

public interface ITerminalDiscovery
{
    Task<IReadOnlyList<TerminalRegistration>> DiscoverAsync(CancellationToken cancellationToken = default);
}

public interface ITerminalDiscoverySource
{
    Task<IReadOnlyList<TerminalRegistration>> DiscoverAsync(CancellationToken cancellationToken = default);
}
