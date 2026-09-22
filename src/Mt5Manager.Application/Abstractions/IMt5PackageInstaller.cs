using Mt5Manager.Domain.Models;

namespace Mt5Manager.Application.Abstractions;

public interface IMt5PackageInstaller
{
    Task<Mt5PackageInstallResult> InstallAsync(
        string sourcePath,
        Mt5PackageKind kind,
        IReadOnlyList<TerminalRegistration> terminals,
        CancellationToken cancellationToken = default);
}
