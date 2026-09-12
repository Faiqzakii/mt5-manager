using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mt5Manager.Application.Telegram;

namespace Mt5Manager.Infrastructure.Telegram;

public enum TelegramApiErrorKind
{
    Api,
    Unauthorized,
    RateLimited,
    Transient,
    MalformedResponse,
    Network
}

public sealed class TelegramApiException : Exception, ITelegramBotApiError
{
    public TelegramApiException(
        TelegramApiErrorKind kind,
        HttpStatusCode? statusCode,
        TimeSpan? retryAfter,
        string description)
        : base(description)
    {
        Kind = kind;
        StatusCode = statusCode;
        RetryAfter = retryAfter;
        Description = description;
    }

    public TelegramApiErrorKind Kind { get; }
    public TelegramBotErrorKind BotErrorKind => Kind switch
    {
        TelegramApiErrorKind.Unauthorized => TelegramBotErrorKind.Unauthorized,
        TelegramApiErrorKind.RateLimited => TelegramBotErrorKind.RateLimited,
        _ => TelegramBotErrorKind.Transient
    };
    public HttpStatusCode? StatusCode { get; }
    public TimeSpan? RetryAfter { get; }
    public string Description { get; }
}

public sealed class TelegramBotApiClient(HttpClient httpClient) : ITelegramBotApi
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const int PollTimeoutSeconds = 25;

    public async Task<TelegramConnectionState> GetConnectionStateAsync(
        string botToken,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await PostAsync<UserResult>(botToken, "getMe", null, cancellationToken);
            return new TelegramConnectionState(true, result.Username, 0, null);
        }
        catch (TelegramApiException error)
        {
            return new TelegramConnectionState(false, null, 0, error.Description);
        }
    }

    public async Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(
        string botToken,
        long offset,
        CancellationToken cancellationToken = default)
    {
        var results = await PostAsync<List<UpdateResult>>(botToken, "getUpdates", new
        {
            offset,
            timeout = PollTimeoutSeconds,
            allowed_updates = new[] { "message", "callback_query" }
        }, cancellationToken);

        return results.Select(MapUpdate).ToArray();
    }

    public Task SendMessageAsync(
        string botToken,
        long chatId,
        TelegramMessage message,
        CancellationToken cancellationToken = default) =>
        PostWithoutResultAsync(botToken, "sendMessage", CreateMessagePayload(chatId, null, message), cancellationToken);

    public Task EditMessageAsync(
        string botToken,
        long chatId,
        long messageId,
        TelegramMessage message,
        CancellationToken cancellationToken = default) =>
        PostWithoutResultAsync(botToken, "editMessageText", CreateMessagePayload(chatId, messageId, message), cancellationToken);

    public Task AnswerCallbackAsync(
        string botToken,
        string callbackQueryId,
        string? text = null,
        CancellationToken cancellationToken = default) =>
        PostWithoutResultAsync(botToken, "answerCallbackQuery", new
        {
            callback_query_id = callbackQueryId,
            text
        }, cancellationToken);

    private static object CreateMessagePayload(long chatId, long? messageId, TelegramMessage message) => new
    {
        chat_id = chatId,
        message_id = messageId,
        text = message.Text,
        reply_markup = new
        {
            inline_keyboard = message.Keyboard.Rows.Select(row =>
                row.Select(button => new { text = button.Text, callback_data = button.CallbackData }).ToArray()).ToArray()
        }
    };

    private async Task PostWithoutResultAsync(
        string token,
        string method,
        object payload,
        CancellationToken cancellationToken) =>
        _ = await PostAsync<JsonElement>(token, method, payload, cancellationToken);

    private async Task<T> PostAsync<T>(
        string token,
        string method,
        object? payload,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, BuildUri(token, method));
            if (payload is not null)
                request.Content = JsonContent.Create(payload, options: JsonOptions);

            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            string content = await response.Content.ReadAsStringAsync(cancellationToken);
            Envelope<T>? envelope;
            try
            {
                envelope = JsonSerializer.Deserialize<Envelope<T>>(content, JsonOptions);
            }
            catch (JsonException) when (!response.IsSuccessStatusCode)
            {
                throw FromHttpStatus<T>(response.StatusCode, null, token);
            }
            catch (JsonException)
            {
                throw Failure(TelegramApiErrorKind.MalformedResponse, response.StatusCode, null,
                    "Telegram returned a malformed response.");
            }

            if (!response.IsSuccessStatusCode)
                throw FromHttpStatus(response.StatusCode, envelope, token);

            if (envelope is null)
                throw Failure(TelegramApiErrorKind.MalformedResponse, response.StatusCode, null,
                    "Telegram returned a malformed response.");

            if (!envelope.Ok)
                throw FromHttpStatus(response.StatusCode, envelope, token);

            if (envelope.Result is null)
                throw Failure(TelegramApiErrorKind.MalformedResponse, response.StatusCode, null,
                    "Telegram response did not contain a result.");

            return envelope.Result;
        }
        catch (TelegramApiException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            throw Failure(TelegramApiErrorKind.Network, null, null, "Telegram network request failed.");
        }
    }

    private static TelegramApiException FromHttpStatus<T>(HttpStatusCode statusCode, Envelope<T>? envelope, string token)
    {
        var kind = statusCode switch
        {
            HttpStatusCode.Unauthorized => TelegramApiErrorKind.Unauthorized,
            (HttpStatusCode)429 => TelegramApiErrorKind.RateLimited,
            >= HttpStatusCode.InternalServerError => TelegramApiErrorKind.Transient,
            _ => TelegramApiErrorKind.Api
        };
        TimeSpan? retryAfter = envelope?.Parameters?.RetryAfter is int seconds
            ? TimeSpan.FromSeconds(seconds)
            : null;
        string safeDescription = string.IsNullOrWhiteSpace(envelope?.Description)
            ? kind switch
            {
                TelegramApiErrorKind.Unauthorized => "Telegram rejected the bot credentials.",
                TelegramApiErrorKind.RateLimited => "Telegram rate limit exceeded.",
                TelegramApiErrorKind.Transient => "Telegram is temporarily unavailable.",
                _ => "Telegram rejected the request."
            }
            : Redact(envelope.Description, token);
        return Failure(kind, statusCode, retryAfter, safeDescription);
    }

    private static string Redact(string value, string token) =>
        string.IsNullOrEmpty(token) ? value : value.Replace(token, "[REDACTED]", StringComparison.Ordinal);

    private static TelegramApiException Failure(
        TelegramApiErrorKind kind,
        HttpStatusCode? statusCode,
        TimeSpan? retryAfter,
        string description) => new(kind, statusCode, retryAfter, description);

    private static Uri BuildUri(string token, string method) =>
        new($"https://api.telegram.org/bot{Uri.EscapeDataString(token)}/{method}");

    private static TelegramUpdate MapUpdate(UpdateResult update)
    {
        TelegramIncomingMessage? message = update.Message is null
            ? null
            : new TelegramIncomingMessage(update.Message.Chat.Id, update.Message.MessageId, update.Message.Text);
        TelegramCallbackQuery? callback = update.CallbackQuery?.Message is null
            ? null
            : new TelegramCallbackQuery(
                update.CallbackQuery.Id,
                update.CallbackQuery.Message.Chat.Id,
                update.CallbackQuery.Message.MessageId,
                update.CallbackQuery.Data ?? string.Empty);
        return new TelegramUpdate(update.UpdateId, message, callback);
    }

    private sealed record Envelope<T>(
        bool Ok,
        T? Result,
        [property: JsonPropertyName("error_code")] int? ErrorCode,
        string? Description,
        ResponseParameters? Parameters);

    private sealed record ResponseParameters([property: JsonPropertyName("retry_after")] int? RetryAfter);
    private sealed record UserResult(string? Username);
    private sealed record UpdateResult(
        [property: JsonPropertyName("update_id")] long UpdateId,
        MessageResult? Message,
        [property: JsonPropertyName("callback_query")] CallbackResult? CallbackQuery);
    private sealed record MessageResult(
        [property: JsonPropertyName("message_id")] long MessageId,
        ChatResult Chat,
        string? Text);
    private sealed record ChatResult(long Id);
    private sealed record CallbackResult(string Id, MessageResult? Message, string? Data);
}
