using Mt5Manager.Domain.Models;

namespace Mt5Manager.Infrastructure.Discovery;

public interface IMt5DataDirectoryResolver
{
    string? Resolve(TerminalRegistration terminal);
}

public sealed class Mt5DataDirectoryResolver : IMt5DataDirectoryResolver
{
    private readonly string appDataTerminalRoot;

    public Mt5DataDirectoryResolver(string? appDataTerminalRoot = null)
    {
        this.appDataTerminalRoot = appDataTerminalRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MetaQuotes", "Terminal");
    }

    public string? Resolve(TerminalRegistration terminal)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        if (terminal.DataDirectoryVerified && IsSafeStructuredDirectory(terminal.DataDirectory))
            return Canonicalize(terminal.DataDirectory);

        var matches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (HasPortableArgument(terminal.Arguments) && IsSafeStructuredDirectory(terminal.WorkingDirectory))
            matches.Add(Canonicalize(terminal.WorkingDirectory));

        foreach (var candidate in OriginCandidates(terminal.ExecutablePath)) matches.Add(candidate);
        return matches.Count == 1 ? matches.Single() : null;
    }

    private IEnumerable<string> OriginCandidates(string executablePath)
    {
        if (!Directory.Exists(appDataTerminalRoot)) yield break;
        var installation = Path.GetDirectoryName(Canonicalize(executablePath));
        if (installation is null) yield break;

        string[] candidates;
        try { candidates = Directory.GetDirectories(appDataTerminalRoot); }
        catch (UnauthorizedAccessException) { yield break; }
        catch (IOException) { yield break; }

        foreach (var candidate in candidates)
        {
            string origin;
            try
            {
                var originFile = Path.Combine(candidate, "origin.txt");
                if (!File.Exists(originFile) || !IsSafeStructuredDirectory(candidate)) continue;
                origin = Canonicalize(File.ReadAllText(originFile).Trim());
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                continue;
            }

            if (StringComparer.OrdinalIgnoreCase.Equals(origin, installation))
                yield return Canonicalize(candidate);
        }
    }

    private static bool HasPortableArgument(IEnumerable<string> arguments) => arguments.Any(argument =>
        string.Equals(argument.Trim(), "/portable", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(argument.Trim(), "-portable", StringComparison.OrdinalIgnoreCase));

    private static bool IsSafeStructuredDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var canonical = Canonicalize(path);
            if (!Directory.Exists(canonical) || Path.GetPathRoot(canonical) == canonical) return false;
            var attributes = File.GetAttributes(canonical);
            if ((attributes & FileAttributes.ReparsePoint) != 0) return false;
            return Directory.Exists(Path.Combine(canonical, "config")) &&
                   Directory.Exists(Path.Combine(canonical, "bases")) &&
                   Directory.Exists(Path.Combine(canonical, "logs"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static string Canonicalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
