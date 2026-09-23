using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mt5Manager.Application.Abstractions;
using Mt5Manager.Domain.Models;

namespace Mt5Manager.Infrastructure.Runtime;

public interface IMt5RuntimeIdentityResolver
{
    Task<string?> ResolveDataDirectoryAsync(string executablePath, CancellationToken cancellationToken = default);
}

public sealed class Mt5RuntimeSnapshotReader : ITerminalRuntimeInspector, IMt5RuntimeIdentityResolver
{
    public const int ProtocolVersion = 2;
    private static readonly TimeSpan Freshness = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ClockSkewTolerance = TimeSpan.FromMinutes(1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
    private readonly string commonFilesRoot;

    public Mt5RuntimeSnapshotReader(string? commonFilesRoot = null) =>
        this.commonFilesRoot = commonFilesRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MetaQuotes", "Terminal", "Common", "Files");

    public static string SnapshotPath(string commonFilesRoot, string dataDirectory)
    {
        ArgumentNullException.ThrowIfNull(dataDirectory);
        return Path.Combine(commonFilesRoot, "Mt5Manager", $"runtime-{Hash(Canonicalize(dataDirectory))}.json");
    }

    public async Task<TerminalAccountSnapshot?> ReadAsync(TerminalRegistration terminal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        cancellationToken.ThrowIfCancellationRequested();
        if (!terminal.DataDirectoryVerified || string.IsNullOrWhiteSpace(terminal.DataDirectory))
            return null;

        var path = SnapshotPath(commonFilesRoot, terminal.DataDirectory);
        return await ReadAcceptedAsync(path, terminal.DataDirectory, cancellationToken).ConfigureAwait(false) ??
            await ReadAcceptedAsync(path + ".bak", terminal.DataDirectory, cancellationToken).ConfigureAwait(false);
    }
    public async Task<string?> ResolveDataDirectoryAsync(string executablePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        cancellationToken.ThrowIfCancellationRequested();

        string executableDirectory;
        try
        {
            executableDirectory = Path.GetDirectoryName(Canonicalize(executablePath)) ?? string.Empty;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
        if (executableDirectory.Length == 0) return null;

        var snapshotDirectory = Path.Combine(commonFilesRoot, "Mt5Manager");
        string[] paths;
        try
        {
            if (!Directory.Exists(snapshotDirectory)) return null;
            paths = Directory.GetFiles(snapshotDirectory, "runtime-*.json", SearchOption.TopDirectoryOnly);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }

        var matches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = await ReadFreshAsync(path, cancellationToken).ConfigureAwait(false) ??
                await ReadFreshAsync(path + ".bak", cancellationToken).ConfigureAwait(false);
            if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.TerminalPath)) continue;

            try
            {
                var terminalDirectory = Canonicalize(snapshot.TerminalPath);
                if (StringComparer.OrdinalIgnoreCase.Equals(terminalDirectory, executableDirectory) &&
                    IsSafeStructuredDataDirectory(snapshot.DataPath))
                    matches.Add(Canonicalize(snapshot.DataPath));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // Ignore malformed or inaccessible snapshots and fail closed.
            }
        }

        return matches.Count == 1 ? matches.Single() : null;
    }

    private static async Task<TerminalAccountSnapshot?> ReadAcceptedAsync(string path, string dataDirectory, CancellationToken cancellationToken)
    {
        var snapshot = await ReadFreshAsync(path, cancellationToken).ConfigureAwait(false);
        return snapshot is not null &&
            StringComparer.OrdinalIgnoreCase.Equals(Canonicalize(snapshot.DataPath), Canonicalize(dataDirectory))
            ? snapshot
            : null;
    }

    private static async Task<TerminalAccountSnapshot?> ReadFreshAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            var snapshot = JsonSerializer.Deserialize<TerminalAccountSnapshot>(content, Json);
            return IsFresh(snapshot) ? snapshot : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool IsFresh(TerminalAccountSnapshot? snapshot)
    {
        if (snapshot is null) return false;
        var age = DateTimeOffset.UtcNow - snapshot.Timestamp;
        return snapshot.ProtocolVersion == ProtocolVersion &&
            age >= -ClockSkewTolerance &&
            age <= Freshness;
    }

    private static bool IsSafeStructuredDataDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var canonical = Canonicalize(path);
        var root = Path.GetPathRoot(canonical);
        if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal) ||
            StringComparer.OrdinalIgnoreCase.Equals(root, canonical) || !Directory.Exists(canonical)) return false;
        if (new DriveInfo(root).DriveType == DriveType.Network) return false;
        if ((File.GetAttributes(canonical) & FileAttributes.ReparsePoint) != 0) return false;
        return Directory.Exists(Path.Combine(canonical, "config")) &&
            Directory.Exists(Path.Combine(canonical, "bases")) &&
            Directory.Exists(Path.Combine(canonical, "logs"));
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant()))).ToLowerInvariant();
    private static string Canonicalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
