using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mt5Manager.Application.Abstractions;

namespace Mt5Manager.Infrastructure.Persistence;

/// <summary>
/// Appends one JSON object per line to the local audit file. Writes are serialized so that
/// concurrent operations cannot interleave a record, and existing lines are never rewritten.
/// </summary>
public sealed class JsonLinesAuditLogger : IAuditLogger, IDisposable
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonLinesAuditLogger(string? path = null) => _path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Mt5Manager", "audit.jsonl");

    public async Task AppendAsync(AuditRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var line = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record, Options) + "\n");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(_path))!;
            Directory.CreateDirectory(directory);
            await using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read,
                4096, FileOptions.Asynchronous);
            await stream.WriteAsync(line, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
