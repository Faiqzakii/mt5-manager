namespace Mt5Manager.Application.Telegram;

public sealed record ProtectedTelegramToken(string Value);

public sealed record TelegramSettings(
    ProtectedTelegramToken BotToken,
    long AllowedChatId,
    long UpdateOffset,
    bool Enabled);

public sealed record TelegramConnectionState(
    bool IsConnected,
    string? BotUsername,
    int TerminalCount,
    string? Error);

public sealed record TelegramTerminal(Guid Id, string Name, string? Login, bool IsAvailable, bool? CurrentlyEnabled = null);

public enum TelegramTerminalOutcome { Changed, AlreadyInRequestedState, Failed }

public sealed record TelegramTerminalResult(
    Guid TerminalId,
    string TerminalName,
    string? Login,
    bool Enable,
    TelegramTerminalOutcome Outcome,
    string? Error);

public sealed record TelegramButton(string Text, string CallbackData);
public sealed record TelegramKeyboard(IReadOnlyList<IReadOnlyList<TelegramButton>> Rows);
public sealed record TelegramMessage(string Text, TelegramKeyboard Keyboard);
public sealed record TelegramIncomingMessage(long ChatId, long MessageId, string? Text);
public sealed record TelegramCallbackQuery(string Id, long ChatId, long MessageId, string Data);
public sealed record TelegramUpdate(long UpdateId, TelegramIncomingMessage? Message, TelegramCallbackQuery? CallbackQuery);

public interface ITelegramSettingsStore
{
    Task<TelegramSettings?> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(TelegramSettings settings, CancellationToken cancellationToken = default);
    Task RemoveAsync(CancellationToken cancellationToken = default);
}

public interface ISecretProtector
{
    ProtectedTelegramToken Protect(string plaintext);
    string Unprotect(ProtectedTelegramToken protectedValue);
}

public interface ITelegramBotApi
{
    Task<TelegramConnectionState> GetConnectionStateAsync(string botToken, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(string botToken, long offset, CancellationToken cancellationToken = default);
    Task<long> SendMessageAsync(string botToken, long chatId, TelegramMessage message, CancellationToken cancellationToken = default);
    Task EditMessageAsync(string botToken, long chatId, long messageId, TelegramMessage message,
        CancellationToken cancellationToken = default);
    Task AnswerCallbackAsync(string botToken, string callbackQueryId, string? text = null, CancellationToken cancellationToken = default);
}

public enum TelegramBotState { Stopped, Running, Unauthorized }

public interface IDelay
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public interface ITelegramBotService : IAsyncDisposable
{
    TelegramBotState State { get; }
    event EventHandler? StateChanged;
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task ApplySettingsAsync(CancellationToken cancellationToken = default);
}

public enum TelegramBotErrorKind { Unauthorized, RateLimited, Permanent, Transient }

public interface ITelegramBotApiError
{
    TelegramBotErrorKind BotErrorKind { get; }
    TimeSpan? RetryAfter { get; }
}

public sealed class TelegramBotException(TelegramBotErrorKind kind, string message, TimeSpan? retryAfter = null) : Exception(message), ITelegramBotApiError
{
    public TelegramBotErrorKind Kind { get; } = kind;
    public TelegramBotErrorKind BotErrorKind => Kind;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
