using FluentAssertions;
using Mt5Manager.Application.Services;
using Mt5Manager.Application.Telegram;
using Mt5Manager.Domain.Models;

namespace Mt5Manager.Application.Tests.Services;

public sealed class TelegramAlgoScheduleFailureNotifierTests
{
    [Fact]
    public async Task Disabled_Telegram_returns_local_failure_without_calling_api()
    {
        var api = new Api();
        var notifier = new TelegramAlgoScheduleFailureNotifier(
            new Settings(new(new ProtectedTelegramToken(""), 0, 0, false)),
            new Protector(),
            api);

        var result = await notifier.NotifyAsync(Notification());

        result.Sent.Should().BeFalse();
        result.Message.Should().Contain("not configured");
        api.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task Enabled_Telegram_sends_second_failure_to_the_allowed_chat()
    {
        var api = new Api();
        var notifier = new TelegramAlgoScheduleFailureNotifier(
            new Settings(new(new ProtectedTelegramToken("protected"), 42, 0, true)),
            new Protector(),
            api);

        var result = await notifier.NotifyAsync(Notification());

        result.Sent.Should().BeTrue();
        var sent = api.Messages.Should().ContainSingle().Subject;
        sent.Token.Should().Be("plain-token");
        sent.ChatId.Should().Be(42);
        sent.Message.Text.Should().ContainAll("London off", "Broker", "OFF", "bridge unavailable", "2 attempts");
        sent.Message.Keyboard.Rows.Should().BeEmpty();
    }

    private static AlgoScheduleFailureNotification Notification() => new(
        Guid.NewGuid(),
        "London off",
        Guid.NewGuid(),
        "Broker",
        false,
        new DateTimeOffset(2026, 9, 23, 8, 0, 0, TimeSpan.Zero),
        "bridge unavailable");

    private sealed class Settings(TelegramSettings? current) : ITelegramSettingsStore
    {
        public Task<TelegramSettings?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(current);
        public Task SaveAsync(TelegramSettings settings, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class Protector : ISecretProtector
    {
        public ProtectedTelegramToken Protect(string plaintext) => new(plaintext);
        public string Unprotect(ProtectedTelegramToken protectedValue) => "plain-token";
    }

    private sealed class Api : ITelegramBotApi
    {
        public List<(string Token, long ChatId, TelegramMessage Message)> Messages { get; } = [];
        public Task<long> SendMessageAsync(string botToken, long chatId, TelegramMessage message, CancellationToken cancellationToken = default)
        {
            Messages.Add((botToken, chatId, message));
            return Task.FromResult(1L);
        }
        public Task<TelegramConnectionState> GetConnectionStateAsync(string botToken, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(string botToken, long offset, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task EditMessageAsync(string botToken, long chatId, long messageId, TelegramMessage message, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task AnswerCallbackAsync(string botToken, string callbackQueryId, string? text = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
