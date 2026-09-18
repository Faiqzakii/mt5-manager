using System.Collections.Concurrent;
using System.Text.Json;
using Mt5Manager.Application.Telegram;

namespace Mt5Manager.Infrastructure.Persistence;

public sealed class JsonTelegramSettingsStore : ITelegramSettingsStore
{
    public const int CurrentVersion = 1;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PathGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _path;

    public string DefaultPath => _path;

    public JsonTelegramSettingsStore(string? path = null) => _path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mt5Manager", "telegram.json");

    public async Task<TelegramSettings?> LoadAsync(CancellationToken cancellationToken = default)
    {
        var gate = GetGate();
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var processLock = await InterprocessFileLock.AcquireAsync(_path, cancellationToken);
            byte[] content;
            try
            {
                content = await File.ReadAllBytesAsync(_path, cancellationToken);
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                return null;
            }

            TelegramSettingsDocument? document;
            try
            {
                document = JsonSerializer.Deserialize<TelegramSettingsDocument>(content, Options);
            }
            catch (JsonException)
            {
                await PreserveUnreadableFileAsync(content, cancellationToken);
                return null;
            }

            if (document is null || document.Version != CurrentVersion || !IsValid(document.Settings))
            {
                await PreserveUnreadableFileAsync(content, cancellationToken);
                return null;
            }
            return document.Settings;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SaveAsync(TelegramSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!IsValid(settings)) throw new ArgumentException("Telegram settings must be valid.", nameof(settings));
        var gate = GetGate();
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var processLock = await InterprocessFileLock.AcquireAsync(_path, cancellationToken);
            var directory = Path.GetDirectoryName(Path.GetFullPath(_path))!;
            Directory.CreateDirectory(directory);
            var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
            try
            {
                await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                                 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(stream, new TelegramSettingsDocument(CurrentVersion, settings), Options, cancellationToken);
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
            gate.Release();
        }
    }

    public async Task RemoveAsync(CancellationToken cancellationToken = default)
    {
        var gate = GetGate();
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var processLock = await InterprocessFileLock.AcquireAsync(_path, cancellationToken);
            var directory = Path.GetDirectoryName(Path.GetFullPath(_path))!;
            if (!Directory.Exists(directory)) return;

            File.Delete(_path);
            var pattern = Path.GetFileName(_path);
            foreach (var file in Directory.EnumerateFiles(directory, $"{pattern}.corrupt-*")) File.Delete(file);
            foreach (var file in Directory.EnumerateFiles(directory, $".{pattern}.*.tmp")) File.Delete(file);
        }
        finally
        {
            gate.Release();
        }
    }


    private async Task PreserveUnreadableFileAsync(byte[] content, CancellationToken cancellationToken)
    {
        var backup = $"{_path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmssfffffff}-{Guid.NewGuid():N}";
        try
        {
            await File.WriteAllBytesAsync(backup, content, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private SemaphoreSlim GetGate() => PathGates.GetOrAdd(Path.GetFullPath(_path), static _ => new SemaphoreSlim(1, 1));

    internal static bool IsValid(TelegramSettings? settings) => settings is not null &&
        settings.UpdateOffset >= 0 &&
        (!settings.Enabled || (settings.AllowedChatId > 0 && !string.IsNullOrWhiteSpace(settings.BotToken?.Value)));

    public sealed record TelegramSettingsDocument(int Version, TelegramSettings Settings);
}
