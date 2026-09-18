using System.Collections.Concurrent;
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
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PathGates = new(StringComparer.OrdinalIgnoreCase);

    private readonly string _path;
    private readonly SemaphoreSlim _gate;
    private int _disposed;

    public JsonLinesAuditLogger(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Mt5Manager", "audit.jsonl");
        _gate = PathGates.GetOrAdd(Path.GetFullPath(_path), static _ => new SemaphoreSlim(1, 1));
    }

    public async Task AppendAsync(AuditRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!IsValid(record)) throw new ArgumentException("Audit record must be valid.", nameof(record));
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var processLock = await InterprocessFileLock.AcquireAsync(_path, cancellationToken);
            var records = await ReadFileAsync(cancellationToken);
            records.Add(record);
            var retained = records.GroupBy(item => item.TerminalId)
                .SelectMany(group => group.OrderByDescending(item => item.Timestamp).Take(100))
                .OrderBy(item => item.Timestamp).ToArray();
            var directory = Path.GetDirectoryName(Path.GetFullPath(_path))!;
            Directory.CreateDirectory(directory);
            var content = string.Join('\n', retained.Select(item => JsonSerializer.Serialize(item, Options))) + "\n";
            var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
            try
            {
                await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                                 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    var bytes = new UTF8Encoding(false).GetBytes(content);
                    await stream.WriteAsync(bytes, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporaryPath, _path, overwrite: true);
            }
            finally
            {
                try { File.Delete(temporaryPath); }
                catch (FileNotFoundException) { }
            }
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
            await using var processLock = await InterprocessFileLock.AcquireAsync(_path, cancellationToken);
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
        byte[] content;
        try { content = await File.ReadAllBytesAsync(_path, cancellationToken); }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) { return []; }

        var records = new List<AuditRecord>();
        var invalid = false;
        foreach (var line in Encoding.UTF8.GetString(content).Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var record = JsonSerializer.Deserialize<AuditRecord>(line, Options);
                if (IsValid(record)) records.Add(record!);
                else invalid = true;
            }
            catch (JsonException) { invalid = true; }
        }

        if (invalid) await PreserveCorruptEvidenceAsync(content, cancellationToken);
        return records;
    }

    private async Task PreserveCorruptEvidenceAsync(byte[] content, CancellationToken cancellationToken)
    {
        var backup = $"{_path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmssfffffff}-{Guid.NewGuid():N}";
        try { await File.WriteAllBytesAsync(backup, content, cancellationToken); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private static bool IsValid(AuditRecord? record) => record is not null &&
        record.Timestamp != default &&
        record.TerminalId != Guid.Empty &&
        !string.IsNullOrWhiteSpace(record.TerminalName) &&
        !string.IsNullOrWhiteSpace(record.Operation) &&
        record.Categories is not null && record.Categories.All(Enum.IsDefined) &&
        Enum.IsDefined(record.ShutdownMethod) && Enum.IsDefined(record.Outcome) &&
        (record.Source is null || Enum.IsDefined(record.Source.Value)) &&
        record.CategoryOutcomes is not null && record.CategoryOutcomes.All(outcome =>
            Enum.IsDefined(outcome.Category) && outcome.DeletedFiles >= 0 && outcome.DeletedBytes >= 0 &&
            outcome.Failures is not null && outcome.Failures.All(failure =>
                failure is not null && !string.IsNullOrWhiteSpace(failure.Path) &&
                !string.IsNullOrWhiteSpace(failure.Error)));

    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
}
