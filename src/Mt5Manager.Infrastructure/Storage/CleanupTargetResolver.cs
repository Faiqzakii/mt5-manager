using Mt5Manager.Application.Abstractions;
using Mt5Manager.Domain.Models;

namespace Mt5Manager.Infrastructure.Storage;

public sealed class CleanupTargetResolver : ICleanupTargetResolver
{
    public IReadOnlyList<string> Resolve(TerminalRegistration terminal, CleanupCategory category)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        if (!terminal.DataDirectoryVerified)
            throw new InvalidOperationException("The terminal data directory has not been verified.");
        if (string.IsNullOrWhiteSpace(terminal.DataDirectory))
            throw new InvalidOperationException("The terminal data directory is invalid.");

        var root = Path.GetFullPath(terminal.DataDirectory);
        if (!string.Equals(root, TrimEndingSeparators(terminal.DataDirectory), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The terminal data directory must be a canonical path.");
        RejectNonLocalRoot(root);
        if (!Directory.Exists(root))
            throw new InvalidOperationException("The verified terminal data directory does not exist.");

        RejectReparseSegments(root, root);

        IEnumerable<string> candidates = category switch
        {
            CleanupCategory.Logs => [Path.Combine(root, "Logs"), Path.Combine(root, "MQL5", "Logs")],
            CleanupCategory.Ticks => ExpandBrokerTargets(root, "ticks"),
            CleanupCategory.History => ExpandBrokerTargets(root, "history"),
            _ => throw new ArgumentOutOfRangeException(nameof(category), category, "Unsupported cleanup category.")
        };

        return candidates
            .Where(Directory.Exists)
            .Select(path => ValidateTarget(root, path))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<string> ExpandBrokerTargets(string root, string leaf)
    {
        var bases = Path.Combine(root, "bases");
        if (!Directory.Exists(bases)) return [];

        RejectReparseSegments(root, bases);
        return Directory.EnumerateDirectories(bases)
            .Select(broker =>
            {
                RejectReparseSegments(root, broker);
                return Path.Combine(broker, leaf);
            });
    }

    private static string ValidateTarget(string root, string target)
    {
        var canonical = Path.GetFullPath(target);
        var rootPrefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (!canonical.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Cleanup target resolves outside the verified data directory.");

        RejectReparseSegments(root, canonical);
        return canonical;
    }

    private static void RejectReparseSegments(string root, string path)
    {
        RejectReparsePoint(root);
        var relative = Path.GetRelativePath(root, path);
        if (relative == ".") return;
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("Cleanup target resolves outside the verified data directory.");

        var current = root;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            if (Directory.Exists(current) || File.Exists(current)) RejectReparsePoint(current);
        }
    }

    private static void RejectNonLocalRoot(string root)
    {
        var pathRoot = Path.GetPathRoot(root);
        if (string.IsNullOrEmpty(pathRoot))
            throw new InvalidOperationException("The verified terminal data directory must be an absolute path.");
        if (string.Equals(Path.TrimEndingDirectorySeparator(root), Path.TrimEndingDirectorySeparator(pathRoot),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Cleanup requires a data directory below a drive root, not the drive root itself.");
        if (pathRoot.StartsWith(@"\\", StringComparison.Ordinal))
            throw new InvalidOperationException($"Cleanup requires a local data directory: {root} is a network path.");
        if (TryGetDriveType(pathRoot) == DriveType.Network)
            throw new InvalidOperationException($"Cleanup requires a local data directory: {root} is on a network drive.");
    }

    private static DriveType? TryGetDriveType(string pathRoot)
    {
        try { return new DriveInfo(pathRoot).DriveType; }
        catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException($"Cleanup path contains a reparse point: {path}");
    }

    private static string TrimEndingSeparators(string path) =>
        Path.TrimEndingDirectorySeparator(path);
}
