using Mt5Manager.Application.Abstractions;
using Mt5Manager.Domain.Models;

namespace Mt5Manager.Infrastructure.Discovery;

public sealed class StandardLocationDiscoverySource(IEnumerable<string>? portableRoots = null) : ITerminalDiscoverySource
{
    private readonly IReadOnlyList<string> _portableRoots = portableRoots?.ToArray() ?? [];

    public Task<IReadOnlyList<TerminalRegistration>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        }.Concat(_portableRoots).Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase);
        var result = new List<TerminalRegistration>();
        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IEnumerable<string> executables;
            try { executables = Directory.EnumerateFiles(root, "terminal64.exe", SearchOption.AllDirectories); }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }
            foreach (var executable in executables)
                result.Add(new TerminalRegistration(Guid.NewGuid(),
                    Path.GetFileName(Path.GetDirectoryName(executable)) ?? "MetaTrader 5",
                    executable, string.Empty, Path.GetDirectoryName(executable) ?? string.Empty,
                    [], DiscoverySource.StandardLocation, false));
        }
        return Task.FromResult<IReadOnlyList<TerminalRegistration>>(result);
    }
}
