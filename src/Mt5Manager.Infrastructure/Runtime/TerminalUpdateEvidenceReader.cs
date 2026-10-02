using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Mt5Manager.Application.Abstractions;
using Mt5Manager.Domain.Models;

namespace Mt5Manager.Infrastructure.Runtime;

public sealed class TerminalUpdateEvidenceReader : ITerminalUpdateEvidenceReader
{
    private const int Limit = 1024 * 1024;
    private readonly ConcurrentDictionary<Guid, (DateTimeOffset Since, Dictionary<string, long> Lengths)> baselines = new();
    public async Task<UpdateEvidence> ReadAsync(TerminalRegistration terminal, DateTimeOffset since, CancellationToken cancellationToken = default)
    {
        string? version = null;
        try { version = FileVersionInfo.GetVersionInfo(terminal.ExecutablePath).FileVersion; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        var lengths = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var hasBaseline = baselines.TryGetValue(terminal.Id, out var baseline) && baseline.Since == since;
        var available = false;
        var activity = false;
        var now = DateTimeOffset.Now;
        // Include both sides of midnight, including a restart that began yesterday.
        var dates = new[] { since.LocalDateTime.Date, now.Date, now.Date.AddDays(-1) }.Distinct();
        foreach (var date in dates)
        {
            var path = Path.Combine(terminal.DataDirectory, "logs", date.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log");
            try
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
                var length = stream.Length;
                lengths[path] = length;
                var header = new byte[4];
                var headerCount = await stream.ReadAsync(header, cancellationToken);
                var encoding = headerCount >= 2 && header[0] == 0xff && header[1] == 0xfe ? Encoding.Unicode
                    : headerCount >= 2 && header[0] == 0xfe && header[1] == 0xff ? Encoding.BigEndianUnicode
                    : headerCount >= 4 && (header[1] == 0 || header[3] == 0) ? Encoding.Unicode : new UTF8Encoding(false, true);
                var start = Math.Max(0, length - Limit);
                if (encoding == Encoding.Unicode || encoding == Encoding.BigEndianUnicode) start -= start % 2;
                stream.Position = start;
                var bytes = new byte[(int)(length - start)];
                var count = 0;
                while (count < bytes.Length)
                {
                    var read = await stream.ReadAsync(bytes.AsMemory(count), cancellationToken);
                    if (read == 0) break;
                    count += read;
                }
                var text = encoding.GetString(bytes, 0, count);
                var offset = start;
                if (text.StartsWith('\ufeff'))
                {
                    offset += encoding.GetByteCount("\ufeff");
                    text = text[1..];
                }
                foreach (var line in text.Split('\n'))
                {
                    var lineOffset = offset;
                    offset += encoding.GetByteCount(line) + encoding.GetByteCount("\n");
                    if (start > 0 && lineOffset == start) continue; // incomplete boundary line
                    var appended = hasBaseline && (!baseline.Lengths.TryGetValue(path, out var oldLength) || lineOffset >= oldLength);
                    var freshTimestamp = false;
                    foreach (var field in line.TrimEnd('\r').Split('\t', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var value = field.Trim();
                        DateTimeOffset stamp;
                        if (DateTimeOffset.TryParseExact(value, ["yyyy.MM.dd HH:mm:ss.fff", "yyyy.MM.dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.fff", "yyyy-MM-dd HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var fullStamp))
                            stamp = fullStamp;
                        else if (value.Contains(':') && TimeSpan.TryParseExact(value, ["hh\\:mm\\:ss", "hh\\:mm\\:ss\\.fff"], CultureInfo.InvariantCulture, out var time))
                            stamp = new DateTimeOffset(DateTime.SpecifyKind(date.Add(time), DateTimeKind.Local));
                        else
                            continue;
                        freshTimestamp = stamp > since && stamp <= now;
                        if (freshTimestamp) break;
                    }
                    // Numeric severity fields (for example MT5's leading "0") are not timestamps.
                    // Offsets prove appended content even when MT5 writes millisecond timestamps late.
                    if (!appended && !freshTimestamp) continue;
                    available = true;
                    if (new[] { "live update", "new version", "update to build", "update package" }.Any(p => line.Contains(p, StringComparison.OrdinalIgnoreCase))) activity = true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException) { }
        }
        if (!hasBaseline) baselines[terminal.Id] = (since, lengths);
        else baselines.TryRemove(terminal.Id, out _);
        return new(version, available, activity, DateTimeOffset.UtcNow, available ? null : "No readable fresh journal evidence.");
    }
}
