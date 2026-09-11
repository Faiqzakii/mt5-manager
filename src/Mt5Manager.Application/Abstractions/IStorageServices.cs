using Mt5Manager.Domain.Models;

namespace Mt5Manager.Application.Abstractions;

public interface ICleanupTargetResolver
{
    IReadOnlyList<string> Resolve(TerminalRegistration terminal, CleanupCategory category);
}
