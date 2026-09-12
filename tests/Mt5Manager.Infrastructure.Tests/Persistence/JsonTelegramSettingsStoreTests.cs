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

    [Fact]
    public void Default_store_uses_per_user_local_app_data_and_not_machine_wide_program_data()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mt5Manager", "telegram.json");
        expected.Should().NotStartWith(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));

        var store = new JsonTelegramSettingsStore();

        store.DefaultPath.Should().Be(expected);
    }

    [Fact]
    public async Task Remove_sweeps_token_file_backups_and_temporary_leftovers()
    {
        var settingsPath = Path.Combine(_root, "telegram.json");
        var corruptBackup = $"{settingsPath}.corrupt-20260912120000000";
        var tempLeftover = Path.Combine(_root, $".{Path.GetFileName(settingsPath)}.{Guid.NewGuid():N}.tmp");
        await File.WriteAllTextAsync(settingsPath, "{}");
        await File.WriteAllTextAsync(corruptBackup, "{not-json");
        await File.WriteAllTextAsync(tempLeftover, "{not-json");
        await File.WriteAllTextAsync(Path.Combine(_root, "unrelated.txt"), "keep");

        await new JsonTelegramSettingsStore(settingsPath).RemoveAsync();

        Directory.EnumerateFiles(_root).Should().BeEquivalentTo([Path.Combine(_root, "unrelated.txt")]);
    }

    [Fact]
    public async Task Crash_leftover_temp_file_does_not_corrupt_settings_and_next_save_overwrites_atomically()
    {
        var store = new JsonTelegramSettingsStore(SettingsPath);
        await store.SaveAsync(Settings("protected-current", 1, 0, true));
        await File.WriteAllTextAsync(Path.Combine(_root, ".telegram.json.deadbeef.tmp"), "{partial-crash");

        (await store.LoadAsync()).Should().Be(Settings("protected-current", 1, 0, true));
        await store.SaveAsync(Settings("protected-next", 2, 0, true));

        Directory.EnumerateFiles(_root).Should().BeEquivalentTo([SettingsPath]);
        (await store.LoadAsync()).Should().Be(Settings("protected-next", 2, 0, true));
    }

    [Fact]
    public async Task Load_returns_null_when_file_is_removed_before_stream_open()
    {
        var store = new JsonTelegramSettingsStore(SettingsPath);
        await File.WriteAllTextAsync(SettingsPath, "{}");
        File.Delete(SettingsPath);

        (await store.LoadAsync()).Should().BeNull();
    }

    [Fact]
    public async Task Atomic_save_observes_temporary_token_file_then_leaves_only_final_settings()
    {
        var store = new JsonTelegramSettingsStore(SettingsPath);
        var observed = false;
        var observer = Task.Run(() =>
        {
            for (var i = 0; i < 100_000 && !observed; i++)
                observed |= Directory.EnumerateFiles(_root, ".telegram.json.*.tmp").Any();
        });

        await store.SaveAsync(Settings("protected", 1, 0, true));
        await observer;

        observed.Should().BeTrue();
        Directory.EnumerateFiles(_root).Should().ContainSingle().Which.Should().Be(SettingsPath);
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
