using System.Security.Cryptography;
using FluentAssertions;
using Mt5Manager.Application.Telegram;
using Mt5Manager.Infrastructure.Persistence;
using Mt5Manager.Infrastructure.Security;

namespace Mt5Manager.Infrastructure.Tests.Security;

public sealed class WindowsUserSecretProtectorTests
{
    [Fact]
    public void Protect_and_unprotect_round_trip_without_retaining_plaintext()
    {
        const string plaintext = "123456:telegram-secret";
        var protector = new WindowsUserSecretProtector();
        var protectedToken = protector.Protect(plaintext);
        protectedToken.Value.Should().NotContain(plaintext);
        protector.Unprotect(protectedToken).Should().Be(plaintext);
    }

    [Fact]
    public void Unprotect_rejects_corrupt_payload()
    {
        var protector = new WindowsUserSecretProtector();
        var act = () => protector.Unprotect(new ProtectedTelegramToken(Convert.ToBase64String([1, 2, 3, 4])));
        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void Unprotect_rejects_payload_protected_with_wrong_entropy()
    {
        var payload = ProtectedData.Protect("secret"u8.ToArray(), "wrong-app"u8.ToArray(), DataProtectionScope.CurrentUser);
        var protector = new WindowsUserSecretProtector();
        var act = () => protector.Unprotect(new ProtectedTelegramToken(Convert.ToBase64String(payload)));
        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public async Task Persisted_settings_do_not_contain_plaintext_token()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mt5-protected-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "telegram.json");
            const string plaintext = "987654:never-write-this";
            var protector = new WindowsUserSecretProtector();
            var store = new JsonTelegramSettingsStore(path);
            await store.SaveAsync(new TelegramSettings(protector.Protect(plaintext), 42, 0, true));
            (await File.ReadAllTextAsync(path)).Should().NotContain(plaintext);
            protector.Unprotect((await store.LoadAsync())!.BotToken).Should().Be(plaintext);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
