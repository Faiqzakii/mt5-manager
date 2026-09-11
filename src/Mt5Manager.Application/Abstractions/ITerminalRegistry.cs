using Mt5Manager.Domain.Models;

namespace Mt5Manager.Application.Abstractions;

public interface ITerminalRegistry
{
    Task<IReadOnlyList<TerminalRegistration>> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(IReadOnlyList<TerminalRegistration> terminals, CancellationToken cancellationToken = default);
}
