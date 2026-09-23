using Mt5Manager.Application.Abstractions;
using Mt5Manager.Domain.Models;
using Mt5Manager.Infrastructure.Persistence;
using Mt5Manager.Infrastructure.Runtime;

namespace Mt5Manager.Infrastructure.Discovery;

public sealed class TerminalDiscovery(
    IEnumerable<ITerminalDiscoverySource> sources,
    ITerminalRegistry? registry = null,
    IMt5DataDirectoryResolver? dataDirectoryResolver = null,
    IMt5RuntimeIdentityResolver? runtimeIdentityResolver = null) : ITerminalDiscovery
{
    public async Task<IReadOnlyList<TerminalRegistration>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var discovered = new Dictionary<TerminalIdentity, TerminalRegistration>();
        IReadOnlyList<TerminalRegistration> registrations = registry is null
            ? []
            : await ResolveAllAsync(await registry.LoadAsync(cancellationToken), cancellationToken);
        foreach (var registration in registrations)
            Merge(discovered, registration);
        foreach (var source in sources)
            foreach (var candidate in await source.DiscoverAsync(cancellationToken))
                Merge(discovered, await EnrichAsync(candidate, cancellationToken));

        var result = discovered.Values
            .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ExecutablePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (registry is not null) await registry.SaveAsync(result, cancellationToken);
        return result;
    }

    private async Task<IReadOnlyList<TerminalRegistration>> ResolveAllAsync(
        IReadOnlyList<TerminalRegistration> candidates,
        CancellationToken cancellationToken)
    {
        var resolved = new TerminalRegistration[candidates.Count];
        for (var index = 0; index < candidates.Count; index++)
            resolved[index] = await EnrichAsync(candidates[index], cancellationToken);
        return resolved;
    }

    private async Task<TerminalRegistration> EnrichAsync(
        TerminalRegistration candidate,
        CancellationToken cancellationToken)
    {
        string? resolved = null;
        if (runtimeIdentityResolver is not null)
            resolved = await runtimeIdentityResolver.ResolveDataDirectoryAsync(candidate.ExecutablePath, cancellationToken);
        if (resolved is null && dataDirectoryResolver is not null)
            resolved = dataDirectoryResolver.Resolve(candidate);
        return resolved is null
            ? candidate with { DataDirectoryVerified = false }
            : candidate with { DataDirectory = resolved, DataDirectoryVerified = true };
    }

    private static void Merge(
        Dictionary<TerminalIdentity, TerminalRegistration> discovered,
        TerminalRegistration candidate)
    {
        var normalized = Normalize(candidate);
        if (!JsonTerminalRegistry.IsValid(normalized)) return;
        var key = new TerminalIdentity(normalized.ExecutablePath, normalized.DataDirectory);
        if (!discovered.TryGetValue(key, out var existing))
            discovered[key] = normalized;
        else if (Priority(normalized.Source) > Priority(existing.Source))
            discovered[key] = normalized with { Id = existing.Id };
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
