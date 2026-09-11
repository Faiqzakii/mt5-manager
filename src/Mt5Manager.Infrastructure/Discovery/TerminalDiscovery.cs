using Mt5Manager.Application.Abstractions;
using Mt5Manager.Domain.Models;
using Mt5Manager.Infrastructure.Persistence;

namespace Mt5Manager.Infrastructure.Discovery;

public sealed class TerminalDiscovery(
    IEnumerable<ITerminalDiscoverySource> sources,
    ITerminalRegistry? registry = null,
    IMt5DataDirectoryResolver? dataDirectoryResolver = null) : ITerminalDiscovery
{
    public async Task<IReadOnlyList<TerminalRegistration>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var discovered = new Dictionary<TerminalIdentity, TerminalRegistration>();
        if (registry is not null)
            foreach (var registration in await registry.LoadAsync(cancellationToken))
                Merge(discovered, Enrich(registration));
        foreach (var source in sources)
            foreach (var candidate in await source.DiscoverAsync(cancellationToken))
                Merge(discovered, Enrich(candidate));

        var result = discovered.Values
            .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ExecutablePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (registry is not null) await registry.SaveAsync(result, cancellationToken);
        return result;
    }

    private TerminalRegistration Enrich(TerminalRegistration candidate)
    {
        if (candidate.DataDirectoryVerified || dataDirectoryResolver is null) return candidate;
        var resolved = dataDirectoryResolver.Resolve(candidate);
        return resolved is null
            ? candidate
            : candidate with { DataDirectory = resolved, DataDirectoryVerified = true };
    }

    private static void Merge(
        Dictionary<TerminalIdentity, TerminalRegistration> discovered,
        TerminalRegistration candidate)
    {
        var normalized = Normalize(candidate);
        if (!JsonTerminalRegistry.IsValid(normalized)) return;
        var key = new TerminalIdentity(normalized.ExecutablePath, normalized.DataDirectory);
        if (!discovered.TryGetValue(key, out var existing) || Priority(normalized.Source) > Priority(existing.Source))
            discovered[key] = normalized;
    }

    private static TerminalRegistration Normalize(TerminalRegistration candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var executable = Canonicalize(candidate.ExecutablePath);
        var dataDirectory = string.IsNullOrWhiteSpace(candidate.DataDirectory)
            ? string.Empty
            : Canonicalize(candidate.DataDirectory);
        var workingDirectory = string.IsNullOrWhiteSpace(candidate.WorkingDirectory)
            ? Path.GetDirectoryName(executable) ?? string.Empty
            : Canonicalize(candidate.WorkingDirectory);
        return candidate with
        {
            ExecutablePath = executable,
            DataDirectory = dataDirectory,
            WorkingDirectory = workingDirectory,
            Arguments = candidate.Arguments ?? [],
            DataDirectoryVerified = candidate.DataDirectoryVerified && dataDirectory.Length > 0
        };
    }

    private static string Canonicalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static int Priority(DiscoverySource source) => source switch
    {
        DiscoverySource.Manual => 4,
        DiscoverySource.Process => 3,
        DiscoverySource.Shortcut => 2,
        DiscoverySource.StandardLocation => 1,
        _ => 0
    };

    private readonly record struct TerminalIdentity(string ExecutablePath, string DataDirectory)
    {
        public bool Equals(TerminalIdentity other) =>
            StringComparer.OrdinalIgnoreCase.Equals(ExecutablePath, other.ExecutablePath) &&
            StringComparer.OrdinalIgnoreCase.Equals(DataDirectory, other.DataDirectory);

        public override int GetHashCode() => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(ExecutablePath),
            StringComparer.OrdinalIgnoreCase.GetHashCode(DataDirectory));
    }
}
