using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Mt5Manager.Application.Abstractions;
using Mt5Manager.Application.Services;
using Mt5Manager.Application.Telegram;
using Mt5Manager.Domain.Models;
using Mt5Manager.Infrastructure.Persistence;
using Mt5Manager.Infrastructure.Security;
using Mt5Manager.Infrastructure.Telegram;

namespace Mt5Manager.Infrastructure.Tests.Telegram;

public sealed class TelegramFakeEndpointIntegrationTests : IDisposable
{
    private const string BotToken = "123456:top-secret-token";
    private const long ChatId = 42;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly string directory = Path.Combine(Path.GetTempPath(), $"mt5manager-telegram-{Guid.NewGuid():N}");
    private readonly string settingsPath;
    private readonly FakeTelegramHttp http = new();
    private readonly FakeTerminalRegistry registry = new();
    private readonly FakeRuntimeInspector runtime = new();
    private readonly FakeAlgoTradingService algo = new();

    public TelegramFakeEndpointIntegrationTests() =>
        settingsPath = Path.Combine(directory, "telegram.json");

    [Fact]
    public async Task Settings_lifecycle_and_fake_telegram_controls_flow_without_real_endpoints()
    {
        var protector = new WindowsUserSecretProtector();
        var store = new JsonTelegramSettingsStore(settingsPath);
        var api = new TelegramBotApiClient(new HttpClient(http));
        await using var bot = new TelegramBotService(store, protector, api, registry, runtime, algo, TimeProvider.System, new ImmediateDelay());

        var alphaId = Guid.NewGuid();
        var bravoId = Guid.NewGuid();
        registry.Items.Add(Terminal(alphaId, "Alpha"));
        registry.Items.Add(Terminal(bravoId, "Bravo"));
        runtime.Snapshots[alphaId] = Snapshot(111, AlgoTradingState.Disabled);
        runtime.Snapshots[bravoId] = null;
        algo.Results[alphaId] = new AlgoTradingOperationResult(alphaId, "Alpha", true, new(true, "changed", null));

        await store.SaveAsync(new TelegramSettings(new ProtectedTelegramToken("protected"), ChatId, 0, false)).WaitAsync(Timeout);
        await bot.StartAsync().WaitAsync(Timeout);
        bot.State.Should().Be(TelegramBotState.Stopped);
        File.Exists(settingsPath).Should().BeTrue();
        var savedText = await File.ReadAllTextAsync(settingsPath).WaitAsync(Timeout);
        savedText.Should().Contain("botToken").And.Contain("protected").And.NotContain(BotToken);

        var protectedToken = protector.Protect(BotToken);
        http.SetResponse("getMe", UserResult("mt5_manager_bot"));
        http.Enqueue("getUpdates", UpdatesResult(new Update(100, new IncomingMessage(ChatId, 1, "/start"), null)));
        await store.SaveAsync(new TelegramSettings(protectedToken, ChatId, 0, true)).WaitAsync(Timeout);
        await bot.StartAsync().WaitAsync(Timeout);
        bot.State.Should().Be(TelegramBotState.Running);
        var persisted = await store.LoadAsync().WaitAsync(Timeout);
        persisted.Should().NotBeNull();
        persisted!.Enabled.Should().BeTrue();
        protector.Unprotect(persisted.BotToken).Should().Be(BotToken);

        // Initial update was queued before polling started to keep this fake endpoint deterministic.
        await WaitAsync(() => http.Sent.Any(message => message.Text.Contains("Terminal: 2")));
        http.Enqueue("getUpdates", UpdatesResult(new Update(101, new IncomingMessage(ChatId, 2, "/on_all"), null)));
        await WaitAsync(() => http.Sent.Count >= 2);
        http.Sent.Should().Contain(message =>
            message.Text.Contains("Aktifkan Algo Trading untuk semua 2 terminal?") &&
            message.Text.Contains("Alpha — 111 (OFF)") &&
            message.Text.Contains("Bravo — akun tidak tersedia (tidak diketahui)"));
        http.Sent.Should().Contain(message =>
            message.Keyboard.Rows.Count == 3 &&
            message.Keyboard.Rows[0][0].CallbackData == "status" &&
            message.Keyboard.Rows[1][0].CallbackData == "pick:on" &&
            message.Keyboard.Rows[1][1].CallbackData == "pick:off" &&
            message.Keyboard.Rows[2][0].CallbackData == "all:on" &&
            message.Keyboard.Rows[2][1].CallbackData == "all:off");

        var confirmation = http.Sent.First(message => message.Text.Contains("Aktifkan Algo Trading untuk semua 2 terminal?")).Keyboard.Rows[0][0].CallbackData;
        confirmation.Should().StartWith("confirm:");

        http.Enqueue("getUpdates", UpdatesResult(new Update(102, null, new CallbackQuery("answer", ChatId, 9, confirmation))));
        await WaitAsync(() => http.Calls.Any(call => call.Method == "answerCallbackQuery" && call.CallbackId == "answer"));
        await WaitAsync(() => algo.Requests.Count == 2);
        algo.Requests.Should().Contain(request => request.TerminalId == alphaId && request.Enable && request.Source == AlgoOperationSource.Telegram);
        await WaitAsync(() => http.Edited.Any(message => message.Text.Contains("sedang diproses")),
            $"no in-progress edit; edited={string.Join(" | ", http.Edited.Select(message => message.Text))}");
        await WaitAsync(() =>
            http.Sent.Any(message => message.Text.Contains("Berhasil") && message.Text.Contains("Alpha — 111")) &&
            http.Sent.Any(message => message.Text.Contains("Gagal") && message.Text.Contains("Bravo") && message.Text.Contains("akun tidak tersedia")) &&
            http.Sent.Any(message => message.Text.Contains("Status: Terhubung")),
            $"no bulk result or refreshed dashboard; sent={string.Join(" | ", http.Sent.Select(message => message.Text))}; algo={algo.Requests.Count}");

        http.Enqueue("getUpdates", UpdatesResult(new Update(103, null, new CallbackQuery("replay", ChatId, 2, confirmation))));
        await WaitAsync(() => http.Calls.Any(call => call.Method == "answerCallbackQuery" && call.CallbackId == "replay"));
        await WaitAsync(() => http.Sent.Any(message => message.Text.Contains("Konfirmasi tidak valid atau kedaluwarsa.")));
        algo.Requests.Should().HaveCount(2);

        await bot.StopAsync().WaitAsync(Timeout);
        bot.State.Should().Be(TelegramBotState.Stopped);

        await store.RemoveAsync().WaitAsync(Timeout);
        await bot.ApplySettingsAsync().WaitAsync(Timeout);
        File.Exists(settingsPath).Should().BeFalse();
        (await store.LoadAsync().WaitAsync(Timeout)).Should().BeNull();
        bot.State.Should().Be(TelegramBotState.Stopped);
        http.Requests.Should().OnlyContain(uri => uri.Host == "api.telegram.org" && uri.AbsolutePath.Contains($"/bot{Uri.EscapeDataString(BotToken)}/"));
        http.Requests.Should().NotContain(uri => uri.Query.Contains(BotToken));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private static TerminalRegistration Terminal(Guid id, string name) =>
        new(id, name, "terminal64.exe", "data", "work", [], DiscoverySource.Manual, true);

    private static TerminalAccountSnapshot Snapshot(long login, AlgoTradingState state) =>
        new(1, DateTimeOffset.UtcNow, "data", login, "Account", "Server", "Company", AccountTradeMode.Demo, true, state, true, true, true);

    private static async Task WaitAsync(Func<bool> condition, string context = "")
    {
        var deadline = DateTime.UtcNow.Add(Timeout);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        condition().Should().BeTrue($"the expected fake Telegram activity did not complete within the timeout. {context}");
    }

    private static string UserResult(string username)
    {
        var result = new { id = 1, is_bot = true, username };
        return $$"""{"ok":true,"result":{{JsonSerializer.Serialize(result)}}}""";
    }

    private static string UpdatesResult(params Update[] updates)
    {
        var results = updates.Select(update =>
        {
            var item = new Dictionary<string, object?> { ["update_id"] = update.Id };
            if (update.Message is not null)
            {
                item["message"] = new Dictionary<string, object?>
                {
                    ["message_id"] = update.Message.MessageId,
                    ["chat"] = new Dictionary<string, object?> { ["id"] = update.Message.ChatId },
                    ["text"] = update.Message.Text
                };
            }
            if (update.Callback is not null)
            {
                item["callback_query"] = new Dictionary<string, object?>
                {
                    ["id"] = update.Callback.Id,
                    ["data"] = update.Callback.Data,
                    ["message"] = new Dictionary<string, object?>
                    {
                        ["message_id"] = update.Callback.MessageId,
                        ["chat"] = new Dictionary<string, object?> { ["id"] = update.Callback.ChatId }
                    }
                };
            }
            return item;
        });
        var result = JsonSerializer.Serialize(results);
        return $$"""{"ok":true,"result":{{result}}}""";
    }

    private sealed record Update(long Id, IncomingMessage? Message, CallbackQuery? Callback);
    private sealed record IncomingMessage(long ChatId, long MessageId, string Text);
    private sealed record CallbackQuery(string Id, long ChatId, long MessageId, string Data);

    private sealed class ImmediateDelay : IDelay
    {
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeTelegramHttp : HttpMessageHandler
    {
        private readonly object gate = new();
        private readonly Dictionary<string, string> methodResponses = new();
        private readonly Queue<(string Method, string Response)> responses = new();
        private readonly SemaphoreSlim queuedResponses = new(0);
        private readonly List<(string Method, string? CallbackId)> calls = [];
        private readonly List<TelegramMessage> sent = [];
        private readonly List<TelegramMessage> edited = [];
        private readonly List<Uri> requests = [];

        public IReadOnlyList<(string Method, string? CallbackId)> Calls
        {
            get { lock (gate) return calls.ToArray(); }
        }

        public IReadOnlyList<TelegramMessage> Sent
        {
            get { lock (gate) return sent.ToArray(); }
        }

        public IReadOnlyList<TelegramMessage> Edited
        {
            get { lock (gate) return edited.ToArray(); }
        }

        public IReadOnlyList<Uri> Requests
        {
            get { lock (gate) return requests.ToArray(); }
        }

        public void Enqueue(string method, string response)
        {
            lock (gate) responses.Enqueue((method, response));
            queuedResponses.Release();
        }

        public void SetResponse(string method, string response)
        {
            lock (gate) methodResponses[method] = response;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var method = uri.Segments[^1];
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            string? callbackId = null;

            using (var document = body is null ? null : JsonDocument.Parse(body))
            {
                if (document is not null)
                {
                    if (method == "sendMessage" || method == "editMessageText")
                    {
                        var message = ReadMessage(document.RootElement);
                        lock (gate)
                        {
                            if (method == "sendMessage") sent.Add(message);
                            else edited.Add(message);
                        }
                    }
                    else if (method == "answerCallbackQuery")
                    {
                        callbackId = document.RootElement.GetProperty("callback_query_id").GetString();
                    }
                }
            }
            if (method == "getUpdates")
                await queuedResponses.WaitAsync(cancellationToken);

            string? response;
            lock (gate)
            {
                requests.Add(uri);
                calls.Add((method, callbackId));

                if (responses.TryPeek(out var queued) && queued.Method == method)
                {
                    responses.Dequeue();
                    response = queued.Response;
                }
                else if (!methodResponses.TryGetValue(method, out response))
                    response = method == "sendMessage"
                        ? """{"ok":true,"result":{"message_id":9,"chat":{"id":42}}}"""
                        : method is "editMessageText" or "answerCallbackQuery" ? """{"ok":true,"result":true}""" : """{"ok":true,"result":[]}""";
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response!, Encoding.UTF8, "application/json") };
        }

        private static TelegramMessage ReadMessage(JsonElement root)
        {
            var rows = new List<IReadOnlyList<TelegramButton>>();
            if (root.TryGetProperty("reply_markup", out var markup) &&
                markup.TryGetProperty("inline_keyboard", out var keyboard) &&
                keyboard.ValueKind == JsonValueKind.Array)
            {
                foreach (var row in keyboard.EnumerateArray())
                {
                    var buttons = new List<TelegramButton>();
                    foreach (var button in row.EnumerateArray())
                        buttons.Add(new TelegramButton(button.GetProperty("text").GetString()!, button.GetProperty("callback_data").GetString()!));
                    rows.Add(buttons);
                }
            }

            return new TelegramMessage(root.GetProperty("text").GetString()!, new TelegramKeyboard(rows));
        }
    }

    private sealed class FakeTerminalRegistry : ITerminalRegistry
    {
        public List<TerminalRegistration> Items { get; } = [];
        public Task<IReadOnlyList<TerminalRegistration>> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TerminalRegistration>>([.. Items]);
        public Task SaveAsync(IReadOnlyList<TerminalRegistration> terminals, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeRuntimeInspector : ITerminalRuntimeInspector
    {
        public Dictionary<Guid, TerminalAccountSnapshot?> Snapshots { get; } = [];
        public Task<TerminalAccountSnapshot?> ReadAsync(TerminalRegistration terminal, CancellationToken cancellationToken = default) =>
            Task.FromResult(Snapshots.GetValueOrDefault(terminal.Id));
    }

    private sealed class FakeAlgoTradingService : IAlgoTradingService
    {
        public List<AlgoTradingRequest> Requests { get; } = [];
        public Dictionary<Guid, AlgoTradingOperationResult> Results { get; } = [];

        public Task<AlgoTradingOperationResult> SetAsync(AlgoTradingRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(Results.GetValueOrDefault(request.TerminalId) ??
                new AlgoTradingOperationResult(request.TerminalId, "Terminal", request.Enable, new(false, "failed", null)));
        }
    }
}
