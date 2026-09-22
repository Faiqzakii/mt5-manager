using Mt5Manager.Application.Abstractions;
using Mt5Manager.Application.Telegram;
using Mt5Manager.Domain.Models;

namespace Mt5Manager.Application.Services;

public sealed class TelegramAlgoScheduleFailureNotifier : IAlgoScheduleFailureNotifier
{
    private readonly ITelegramSettingsStore settingsStore;
    private readonly ISecretProtector protector;
    private readonly ITelegramBotApi api;

    public TelegramAlgoScheduleFailureNotifier(
        ITelegramSettingsStore settingsStore,
        ISecretProtector protector,
        ITelegramBotApi api)
    {
        this.settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        this.protector = protector ?? throw new ArgumentNullException(nameof(protector));
        this.api = api ?? throw new ArgumentNullException(nameof(api));
    }

    public async Task<AlgoScheduleNotificationResult> NotifyAsync(
        AlgoScheduleFailureNotification notification,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        var settings = await settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (settings is null || !settings.Enabled || settings.AllowedChatId <= 0 || string.IsNullOrWhiteSpace(settings.BotToken.Value))
            return new(false, "Telegram notification was not sent because Telegram is not configured and enabled.");

        var action = notification.Enable ? "ON" : "OFF";
        var message = new TelegramMessage(
            $"MT5 Manager scheduler failed after 2 attempts.\n\n" +
            $"Schedule: {notification.ScheduleName}\n" +
            $"Terminal: {notification.TerminalName}\n" +
            $"Requested state: {action}\n" +
            $"Scheduled time: {notification.ScheduledAt:yyyy-MM-dd HH:mm zzz}\n" +
            $"Result: {notification.Error}",
            new TelegramKeyboard([]));
        await api.SendMessageAsync(
            protector.Unprotect(settings.BotToken),
            settings.AllowedChatId,
            message,
            cancellationToken).ConfigureAwait(false);
        return new(true, "Failure notification sent to Telegram.");
    }
}
