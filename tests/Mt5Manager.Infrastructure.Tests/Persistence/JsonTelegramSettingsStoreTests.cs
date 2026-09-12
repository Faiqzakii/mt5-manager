using System.Text.Json;
using FluentAssertions;
using Mt5Manager.Application.Telegram;
using Mt5Manager.Infrastructure.Persistence;

namespace Mt5Manager.Infrastructure.Tests.Persistence;

public sealed class JsonTelegramSettingsStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"mt5-telegram-{Guid.NewGuid():N}");
    private string SettingsPath => Path.Combine(_root, "telegram.json");
    public JsonTelegramSettingsStoreTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Save_and_load_round_trip_and_atomic_overwrite()
    {
        var store = new JsonTelegramSettingsStore(SettingsPath);
        await store.SaveAsync(Settings("protected-original", 11, 2, true));
        var replacement = Settings("protected-replacement", 22, 9, true);
        await store.SaveAsync(replacement);
        (await store.LoadAsync()).Should().Be(replacement);
        Directory.EnumerateFiles(_root).Should().ContainSingle().Which.Should().Be(SettingsPath);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(SettingsPath));
        json.RootElement.GetProperty("version").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Remove_deletes_saved_settings_and_missing_file_is_harmless()
    {
        var store = new JsonTelegramSettingsStore(SettingsPath);
        await store.SaveAsync(Settings("protected", 1, 0, true));
        await store.RemoveAsync();
        await store.RemoveAsync();
        File.Exists(SettingsPath).Should().BeFalse();
        (await store.LoadAsync()).Should().BeNull();
    }

    [Theory]
    [InlineData("", 1, 0, true)]
    [InlineData("protected", 0, 0, true)]
    [InlineData("protected", -1, 0, true)]
    [InlineData("protected", 1, -1, true)]
    public async Task Save_rejects_invalid_settings(string token, long chatId, long offset, bool enabled)
    {
        var store = new JsonTelegramSettingsStore(SettingsPath);
        var act = () => store.SaveAsync(Settings(token, chatId, offset, enabled));
        await act.Should().ThrowAsync<ArgumentException>();
        File.Exists(SettingsPath).Should().BeFalse();
    }

    [Fact]
    public async Task Disabled_settings_allow_blank_token_and_no_allowed_chat()
    {
        var store = new JsonTelegramSettingsStore(SettingsPath);
        var settings = Settings("", 0, 0, false);
        await store.SaveAsync(settings);
        (await store.LoadAsync()).Should().Be(settings);
    }

    [Theory]
    [InlineData("{not-json")]
    [InlineData("{\"version\":2,\"settings\":{\"botToken\":{\"value\":\"protected\"},\"allowedChatId\":1,\"updateOffset\":0,\"enabled\":true}}")]
    [InlineData("{\"version\":1,\"settings\":{\"botToken\":{\"value\":\"\"},\"allowedChatId\":1,\"updateOffset\":0,\"enabled\":true}}")]
    public async Task Load_rejects_and_preserves_corrupt_unsupported_or_invalid_documents(string content)
    {
        await File.WriteAllTextAsync(SettingsPath, content);
        var store = new JsonTelegramSettingsStore(SettingsPath);
        (await store.LoadAsync()).Should().BeNull();
        File.Exists(SettingsPath).Should().BeFalse();
        var preserved = Directory.EnumerateFiles(_root, "telegram.json.corrupt-*").Single();
        (await File.ReadAllTextAsync(preserved)).Should().Be(content);
    }

    private static TelegramSettings Settings(string token, long chatId, long offset, bool enabled) =>
        new(new ProtectedTelegramToken(token), chatId, offset, enabled);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
