using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mt5Manager.Application.Abstractions;
using Mt5Manager.Domain.Models;

namespace Mt5Manager.Infrastructure.Runtime;

public sealed class Mt5RuntimeSnapshotReader : ITerminalRuntimeInspector
{
    public const int ProtocolVersion = 1;
    private static readonly TimeSpan Freshness = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
    private readonly string commonFilesRoot;

    public Mt5RuntimeSnapshotReader(string? commonFilesRoot = null) =>
        this.commonFilesRoot = commonFilesRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MetaQuotes", "Terminal", "Common", "Files");

    public static string SnapshotPath(string commonFilesRoot, string dataDirectory)
    {
        ArgumentNullException.ThrowIfNull(dataDirectory);
        return Path.Combine(commonFilesRoot, "Mt5Manager", $"runtime-{Hash(Canonicalize(dataDirectory))}.json");
    }

    public Task<TerminalAccountSnapshot?> ReadAsync(TerminalRegistration terminal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        if (!terminal.DataDirectoryVerified || string.IsNullOrWhiteSpace(terminal.DataDirectory))
            return Task.FromResult<TerminalAccountSnapshot?>(null);

        try
        {
            var path = SnapshotPath(commonFilesRoot, terminal.DataDirectory);
            if (!File.Exists(path)) return Task.FromResult<TerminalAccountSnapshot?>(null);
            var snapshot = JsonSerializer.Deserialize<TerminalAccountSnapshot>(File.ReadAllText(path), Json);
            return Task.FromResult(IsAccepted(snapshot, terminal.DataDirectory) ? snapshot : null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            return Task.FromResult<TerminalAccountSnapshot?>(null);
        }
    }

    private static bool IsAccepted(TerminalAccountSnapshot? snapshot, string dataDirectory) =>
        snapshot is not null &&
        snapshot.ProtocolVersion == ProtocolVersion &&
        DateTimeOffset.UtcNow - snapshot.Timestamp <= Freshness &&
        StringComparer.OrdinalIgnoreCase.Equals(Canonicalize(snapshot.DataPath), Canonicalize(dataDirectory));

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant()))).ToLowerInvariant();
    private static string Canonicalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
