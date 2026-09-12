using System.Text.Json;
using Mt5Manager.Application.Telegram;

namespace Mt5Manager.Infrastructure.Persistence;

public sealed class JsonTelegramSettingsStore : ITelegramSettingsStore
{
    public const int CurrentVersion = 1;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;

    public string DefaultPath => _path;

    public JsonTelegramSettingsStore(string? path = null) => _path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mt5Manager", "telegram.json");

    public async Task<TelegramSettings?> LoadAsync(CancellationToken cancellationToken = default)
    {
        TelegramSettingsDocument? document;
        try
        {
            await using (var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read))
                document = await JsonSerializer.DeserializeAsync<TelegramSettingsDocument>(stream, Options, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (JsonException)
        {
            PreserveUnreadableFile();
            return null;
        }

        if (document is null || document.Version != CurrentVersion || !IsValid(document.Settings))
        {
            PreserveUnreadableFile();
            return null;
        }
        return document.Settings;
    }

    public async Task SaveAsync(TelegramSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!IsValid(settings)) throw new ArgumentException("Telegram settings must be valid.", nameof(settings));
        var directory = Path.GetDirectoryName(Path.GetFullPath(_path))!;
        Directory.CreateDirectory(directory);
        DeleteTemporaryFiles(directory);
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
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public Task RemoveAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.GetDirectoryName(Path.GetFullPath(_path))!;
        if (!Directory.Exists(directory)) return Task.CompletedTask;

        if (File.Exists(_path)) File.Delete(_path);
        var pattern = Path.GetFileName(_path);
        foreach (var file in Directory.EnumerateFiles(directory, $"{pattern}.corrupt-*")) File.Delete(file);
        DeleteTemporaryFiles(directory);
        return Task.CompletedTask;
    }

    private void DeleteTemporaryFiles(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, $".{Path.GetFileName(_path)}.*.tmp")) File.Delete(file);
    }

    private void PreserveUnreadableFile()
    {
        try
        {
            var backup = $"{_path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmssfffffff}";
            if (!File.Exists(backup)) File.Move(_path, backup);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    internal static bool IsValid(TelegramSettings? settings) => settings is not null &&
        settings.UpdateOffset >= 0 &&
        (!settings.Enabled || (settings.AllowedChatId > 0 && !string.IsNullOrWhiteSpace(settings.BotToken?.Value)));

    public sealed record TelegramSettingsDocument(int Version, TelegramSettings Settings);
}
