using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mt5Manager.Application.Abstractions;

namespace Mt5Manager.Infrastructure.Persistence;

/// <summary>
/// Stores JSON-lines audit records behind a serialized gate. Each terminal retains its newest
/// one hundred records so the same file can back the persistent operation-history view.
/// </summary>
public sealed class JsonLinesAuditLogger : IAuditLogger, IDisposable
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _disposed;

    public JsonLinesAuditLogger(string? path = null) => _path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Mt5Manager", "audit.jsonl");

    public async Task AppendAsync(AuditRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var records = await ReadFileAsync(cancellationToken);
            records.Add(record);
            var retained = records.GroupBy(item => item.TerminalId)
                .SelectMany(group => group.OrderByDescending(item => item.Timestamp).Take(100))
                .OrderBy(item => item.Timestamp).ToArray();
            var directory = Path.GetDirectoryName(Path.GetFullPath(_path))!;
            Directory.CreateDirectory(directory);
            var content = string.Join('\n', retained.Select(item => JsonSerializer.Serialize(item, Options))) + "\n";
            await File.WriteAllTextAsync(_path, content, new UTF8Encoding(false), cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<AuditRecord>> ReadAsync(Guid terminalId, int limit = 100,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return [.. (await ReadFileAsync(cancellationToken)).Where(item => item.TerminalId == terminalId)
                .OrderByDescending(item => item.Timestamp).Take(limit)];
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<AuditRecord>> ReadFileAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path)) return [];
        var records = new List<AuditRecord>();
        foreach (var line in await File.ReadAllLinesAsync(_path, cancellationToken))
            if (!string.IsNullOrWhiteSpace(line) && JsonSerializer.Deserialize<AuditRecord>(line, Options) is { } record)
                records.Add(record);
        return records;
    }

    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
}
