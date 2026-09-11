using Mt5Manager.Application.Abstractions;
using Mt5Manager.Domain.Models;

namespace Mt5Manager.Infrastructure.Discovery;

public sealed class StandardLocationDiscoverySource(IEnumerable<string>? portableRoots = null) : ITerminalDiscoverySource
{
    private readonly IReadOnlyList<string> _portableRoots = portableRoots?.ToArray() ?? [];

    public Task<IReadOnlyList<TerminalRegistration>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<TerminalRegistration>();
        var pending = new Queue<string>(new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        }.Concat(_portableRoots).Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase));
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Dequeue();
            if (!visited.Add(directory)) continue;

            // Enumerate one directory at a time: an inaccessible subdirectory must not abort the scan.
            string[] executables;
            try { executables = Directory.GetFiles(directory, "terminal64.exe"); }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }
            foreach (var executable in executables)
                result.Add(new TerminalRegistration(Guid.NewGuid(),
                    Path.GetFileName(Path.GetDirectoryName(executable)) ?? "MetaTrader 5",
                    executable, string.Empty, Path.GetDirectoryName(executable) ?? string.Empty,
                    [], DiscoverySource.StandardLocation, false));

            string[] children;
            try { children = Directory.GetDirectories(directory); }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }
            foreach (var child in children) pending.Enqueue(child);
        }
        return Task.FromResult<IReadOnlyList<TerminalRegistration>>(result);
    }
}
