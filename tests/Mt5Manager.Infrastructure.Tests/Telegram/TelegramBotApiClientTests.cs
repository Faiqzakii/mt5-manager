using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Mt5Manager.Application.Telegram;
using Mt5Manager.Infrastructure.Telegram;

namespace Mt5Manager.Infrastructure.Tests.Telegram;

public sealed class TelegramBotApiClientTests
{
    private const string Token = "123456:TOP-SECRET";

    [Fact]
    public async Task GetConnectionState_maps_success_and_api_failure()
    {
        var handler = new FakeHandler(
            Response(HttpStatusCode.OK, """{"ok":true,"result":{"id":7,"is_bot":true,"username":"control_bot"}}"""),
            Response(HttpStatusCode.OK, """{"ok":false,"error_code":400,"description":"Bad bot request"}"""));
        var sut = Create(handler);

        (await sut.GetConnectionStateAsync(Token)).Should().Be(new TelegramConnectionState(true, "control_bot", 0, null));
        var failed = await sut.GetConnectionStateAsync(Token);
        failed.IsConnected.Should().BeFalse();
        failed.Error.Should().Be("Bad bot request");
        failed.Error.Should().NotContain(Token);
    }

    [Fact]
    public async Task GetUpdates_maps_long_poll_request_and_transport_contracts()
    {
        var handler = new FakeHandler(Response(HttpStatusCode.OK, """
            {"ok":true,"result":[
              {"update_id":42,"message":{"message_id":8,"chat":{"id":99},"text":"/start"}},
              {"update_id":43,"callback_query":{"id":"cb","data":"refresh","message":{"message_id":9,"chat":{"id":99}}}}
            ]}
            """));
        var sut = Create(handler);

        var updates = await sut.GetUpdatesAsync(Token, 41);

        updates.Should().Equal(
            new TelegramUpdate(42, new TelegramIncomingMessage(99, 8, "/start"), null),
            new TelegramUpdate(43, null, new TelegramCallbackQuery("cb", 99, 9, "refresh")));
        var request = handler.Requests.Single();
        request.Method.Should().Be(HttpMethod.Post);
        var json = JsonDocument.Parse(request.Body).RootElement;
        json.GetProperty("offset").GetInt64().Should().Be(41);
        json.GetProperty("timeout").GetInt32().Should().Be(25);
        json.GetProperty("allowed_updates").EnumerateArray().Select(x => x.GetString()).Should().Equal("message", "callback_query");
    }

    [Fact]
    public async Task Send_and_edit_serialize_inline_keyboard()
    {
        var handler = new FakeHandler(Response(HttpStatusCode.OK, Success), Response(HttpStatusCode.OK, Success));
        var sut = Create(handler);
        var message = new TelegramMessage("Status", new TelegramKeyboard([
            [new TelegramButton("Refresh", "refresh"), new TelegramButton("ON", "on:1")]
        ]));

        await sut.SendMessageAsync(Token, 99, message);
        await sut.EditMessageAsync(Token, 99, 12, message);

        handler.Requests.Select(x => x.MethodName).Should().Equal("sendMessage", "editMessageText");
        AssertMessage(handler.Requests[0].Body, includeMessageId: false);
        AssertMessage(handler.Requests[1].Body, includeMessageId: true);
    }

    [Fact]
    public async Task AnswerCallback_serializes_optional_text()
    {
        var handler = new FakeHandler(Response(HttpStatusCode.OK, Success));
        var sut = Create(handler);

        await sut.AnswerCallbackAsync(Token, "query-1", "Done");

        var json = JsonDocument.Parse(handler.Requests.Single().Body).RootElement;
        handler.Requests.Single().MethodName.Should().Be("answerCallbackQuery");
        json.GetProperty("callback_query_id").GetString().Should().Be("query-1");
        json.GetProperty("text").GetString().Should().Be("Done");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, TelegramApiErrorKind.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError, TelegramApiErrorKind.Transient)]
    public async Task Http_failures_are_typed_and_token_safe(HttpStatusCode status, TelegramApiErrorKind kind)
    {
        var handler = new FakeHandler(Response(status, """{"ok":false,"description":"Request failed"}"""));
        var sut = Create(handler);

        var error = await FluentActions.Awaiting(() => sut.GetUpdatesAsync(Token, 0)).Should().ThrowAsync<TelegramApiException>();

        error.Which.StatusCode.Should().Be(status);
        error.Which.Kind.Should().Be(kind);
        AssertTokenSafe(error.Which);
    }

    [Fact]
    public async Task Rate_limit_exposes_retry_after()
    {
        var handler = new FakeHandler(Response((HttpStatusCode)429,
            """{"ok":false,"error_code":429,"description":"Too Many Requests","parameters":{"retry_after":7}}"""));
        var sut = Create(handler);

        var error = await FluentActions.Awaiting(() => sut.SendMessageAsync(Token, 1,
            new TelegramMessage("x", new TelegramKeyboard([])))).Should().ThrowAsync<TelegramApiException>();

        error.Which.Kind.Should().Be(TelegramApiErrorKind.RateLimited);
        error.Which.RetryAfter.Should().Be(TimeSpan.FromSeconds(7));
        AssertTokenSafe(error.Which);
    }
    [Fact]
    public async Task Api_error_malformed_json_and_network_error_are_typed_and_token_safe()
    {
        var handler = new FakeHandler(
            Response(HttpStatusCode.OK, $"{{\"ok\":false,\"error_code\":400,\"description\":\"Invalid token {Token}\"}}"),
            Response(HttpStatusCode.OK, "not-json"),
            new HttpRequestException($"Network failed at https://api.telegram.org/bot{Token}/getMe"));
        var sut = Create(handler);

        var api = await FluentActions.Awaiting(() => sut.GetUpdatesAsync(Token, 0)).Should().ThrowAsync<TelegramApiException>();
        api.Which.Description.Should().Be("Invalid token [REDACTED]");
        api.Which.Kind.Should().Be(TelegramApiErrorKind.Api);
        AssertTokenSafe(api.Which);

        var malformed = await FluentActions.Awaiting(() => sut.GetUpdatesAsync(Token, 0)).Should().ThrowAsync<TelegramApiException>();
        malformed.Which.Kind.Should().Be(TelegramApiErrorKind.MalformedResponse);
        AssertTokenSafe(malformed.Which);

        var network = await FluentActions.Awaiting(() => sut.GetUpdatesAsync(Token, 0)).Should().ThrowAsync<TelegramApiException>();
        network.Which.Kind.Should().Be(TelegramApiErrorKind.Network);
        AssertTokenSafe(network.Which);
    }

    private static TelegramBotApiClient Create(FakeHandler handler) =>
        new(new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) });

    private static HttpResponseMessage Response(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static void AssertMessage(string body, bool includeMessageId)
    {
        var json = JsonDocument.Parse(body).RootElement;
        json.GetProperty("chat_id").GetInt64().Should().Be(99);
        json.GetProperty("text").GetString().Should().Be("Status");
        if (includeMessageId) json.GetProperty("message_id").GetInt64().Should().Be(12);
        var buttons = json.GetProperty("reply_markup").GetProperty("inline_keyboard")[0];
        buttons[0].GetProperty("text").GetString().Should().Be("Refresh");
        buttons[0].GetProperty("callback_data").GetString().Should().Be("refresh");
    }

    private static void AssertTokenSafe(Exception error)
    {
        error.ToString().Should().NotContain(Token);
        (error as TelegramApiException)!.Description.Should().NotContain(Token);
    }

    private const string Success = """{"ok":true,"result":true}""";

    private sealed class FakeHandler(params object[] outcomes) : HttpMessageHandler
    {
        private readonly Queue<object> _outcomes = new(outcomes);
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new CapturedRequest(
                request.RequestUri!.Segments.Last(),
                request.Method,
                request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            var outcome = _outcomes.Dequeue();
            return outcome is Exception error ? throw error : (HttpResponseMessage)outcome;
        }
    }

    private sealed record CapturedRequest(string MethodName, HttpMethod Method, string Body);
}
