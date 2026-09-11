using Mt5Manager.Domain.Models;

namespace Mt5Manager.Application.Abstractions;

public interface ICleanupTargetResolver
{
    IReadOnlyList<string> Resolve(TerminalRegistration terminal, CleanupCategory category);
}

public interface ITerminalStorageInspector
{
    Task<IReadOnlyList<CategoryUsage>> InspectAsync(TerminalRegistration terminal, CancellationToken cancellationToken);
}

public interface ITerminalCleanupService
{
    Task<IReadOnlyList<CleanupCategoryResult>> CleanAsync(
        TerminalRegistration terminal,
        IReadOnlySet<CleanupCategory> categories,
        CancellationToken cancellationToken);
}
