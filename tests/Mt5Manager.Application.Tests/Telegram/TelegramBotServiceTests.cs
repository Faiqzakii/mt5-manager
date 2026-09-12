using FluentAssertions;
using Mt5Manager.Application.Abstractions;
using Mt5Manager.Application.Telegram;
using Mt5Manager.Domain.Models;

namespace Mt5Manager.Application.Tests.Telegram;

public sealed class TelegramBotServiceTests
{
    [Fact]
    public async Task Unauthorized_message_and_callback_are_silent_before_other_dependencies()
    {
        await using var f = new Fixture();
        f.Api.Updates.Enqueue([
            new(4, new(999, 1, "/start"), null),
            new(5, null, new("cb", 999, 2, "status"))]);
        await f.RunAsync(6);
        f.Api.Calls.Should().OnlyContain(x => x == "updates");
        f.Registry.LoadCount.Should().Be(0);
        f.Runtime.ReadCount.Should().Be(0);
        f.Settings.Saved.Select(x => x.UpdateOffset).Should().Equal(5, 6);
    }

    [Theory]
    [InlineData("/start")]
    [InlineData("/status")]
    public async Task Dashboard_commands_use_fresh_registry_data(string command)
    {
        await using var f = new Fixture();
        f.Registry.Items.Add(f.Registration());
        f.Api.Updates.Enqueue([new(0, new(42, 8, command), null)]);
        await f.RunAsync(1);
        f.Api.Sent.Should().ContainSingle(x => x.Message.Text.Contains("Terminal: 1"));
    }

    [Theory]
    [InlineData("/on", "terminal:on:")]
    [InlineData("/off", "terminal:off:")]
    [InlineData("/onall", "confirm:")]
    [InlineData("/offall", "confirm:")]
    public async Task Mutation_commands_route_to_inline_confirmation_flows(string command, string callbackPrefix)
    {
        await using var f = new Fixture();
        var terminal = f.Registration(); f.Registry.Items.Add(terminal); f.Runtime.Snapshots[terminal.Id] = f.Snapshot();
        f.Api.Updates.Enqueue([new(0, new(42, 8, command), null)]);
        await f.RunAsync(1);
        f.Api.Sent.Should().ContainSingle();
        f.Api.Sent[0].Message.Keyboard.Rows.SelectMany(x => x).Should().Contain(x => x.CallbackData.StartsWith(callbackPrefix));
    }

    [Fact]
    public async Task Callback_is_acknowledged_before_any_MT5_action_and_uses_callback_chat_authorization()
    {
        await using var f = new Fixture(); var terminal = f.AvailableTerminal();
        var confirmation = await f.CreateConfirmationAsync("/onall");
        f.Api.Calls.Clear(); f.Api.Updates.Enqueue([new(1, null, new("answer-me", 42, 9, confirmation))]);
        await f.RunAsync(2);
        f.Api.Calls.Should().ContainInOrder("answer:answer-me", "algo");
    }

    [Fact]
    public async Task Confirmation_is_random_compact_scoped_immutable_expiring_and_one_use()
    {
        await using var f = new Fixture(); var terminal = f.AvailableTerminal();
        var first = await f.CreateConfirmationAsync("/onall");
        var second = await f.CreateConfirmationAsync("/onall", 1);
        first.Should().NotBe(second); first.Length.Should().BeLessThan(64); second.Length.Should().BeLessThan(64);
        await f.SendCallbacksAsync(
            new("wrong", 99, 9, first),
            new("ok", 42, 9, second),
            new("duplicate", 42, 9, second));
        f.Algo.Requests.Should().ContainSingle(x => x.TerminalId == terminal.Id);

        var expired = await f.CreateConfirmationAsync("/onall", 5);
        f.Clock.Advance(TimeSpan.FromMinutes(2));
        await f.SendCallbacksAsync(new TelegramCallbackQuery("expired", 42, 9, expired));
        f.Algo.Requests.Should().ContainSingle();

        var cancelled = await f.CreateConfirmationAsync("/onall", 9);
        await f.SendCallbacksAsync(new TelegramCallbackQuery("cancel", 42, 9, cancelled.Replace("confirm:", "cancel:")));
        f.Algo.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Tampered_picker_action_is_rejected_without_confirmation_or_mutation()
    {
        await using var f = new Fixture(); f.AvailableTerminal();
        var picker = await f.CreatePickerAsync("/on");
        await f.SendCallbacksAsync(new TelegramCallbackQuery("tampered", 42, 9, picker.Replace("terminal:on:", "terminal:off:")));
        f.Api.LastDelivered.Text.Should().Contain("tidak valid");
        f.Algo.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Individual_confirmation_uses_one_fresh_snapshot_and_suppresses_stale_data()
    {
        await using var f = new Fixture(); var terminal = f.AvailableTerminal();
        var picker = await f.CreatePickerAsync("/on");
        f.Runtime.Sequences[terminal.Id] = new Queue<TerminalAccountSnapshot?>([f.Snapshot(), null]);
        await f.SendCallbacksAsync(new TelegramCallbackQuery("pick", 42, 9, picker));
        f.Runtime.Sequences[terminal.Id].Should().ContainSingle().Which.Should().BeNull();
        f.Api.LastDelivered.Text.Should().Contain("Status saat ini");
        f.Api.LastDelivered.Text.Should().Contain("123456");
        f.Algo.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Wrong_chat_does_not_consume_confirmation_owned_by_allowed_chat()
    {
        await using var f = new Fixture(); var terminal = f.AvailableTerminal();
        var confirmation = await f.CreateConfirmationAsync("/onall");
        await f.SendCallbacksAsync(
            new("attacker", 99, 9, confirmation),
            new("owner", 42, 9, confirmation));
        f.Algo.Requests.Should().ContainSingle(x => x.TerminalId == terminal.Id);
    }

    [Fact]
    public async Task Unavailable_terminal_suppresses_stale_account_and_never_mutates()
    {
        await using var f = new Fixture(); var terminal = f.Registration(); f.Registry.Items.Add(terminal);
        f.Runtime.Snapshots[terminal.Id] = f.Snapshot();
        var picker = await f.CreatePickerAsync("/on");
        f.Runtime.Snapshots.Remove(terminal.Id);
        await f.SendCallbacksAsync(new TelegramCallbackQuery("pick", 42, 9, picker));
        f.Api.LastDelivered.Text.Should().Contain("tidak tersedia");
        f.Api.LastDelivered.Text.Should().NotContain(f.Snapshot().Login.ToString());
        f.Algo.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Bulk_execution_is_sequential_continues_after_errors_and_classifies_results()
    {
        await using var f = new Fixture();
        var a = f.AvailableTerminal("A"); var b = f.AvailableTerminal("B"); var c = f.AvailableTerminal("C");
        f.Algo.Results.Enqueue(new(a.Id, "A", true, new(true, "changed", null)));
        f.Algo.Errors.Enqueue(null); f.Algo.Errors.Enqueue(new InvalidOperationException("boom")); f.Algo.Errors.Enqueue(null);
        f.Algo.Results.Enqueue(new(c.Id, "C", true, new(true, "already enabled", null)));
        var confirm = await f.CreateConfirmationAsync("/onall");
        await f.SendCallbacksAsync(new TelegramCallbackQuery("go", 42, 1, confirm));
        f.Algo.MaxConcurrent.Should().Be(1); f.Algo.Requests.Select(x => x.TerminalId).Should().Equal(a.Id, b.Id, c.Id);
        string.Join("\n", f.Api.Sent.Select(x => x.Message.Text)).Should().ContainAll("Berhasil", "Sudah ON/OFF", "Gagal", "boom");
    }

    [Fact]
    public async Task Failed_edit_falls_back_to_send()
    {
        await using var f = new Fixture(); f.Api.ThrowOnEdit = true;
        f.Api.Updates.Enqueue([new(0, null, new("cb", 42, 8, "all:on"))]);
        await f.RunAsync(1);
        f.Api.Edits.Should().ContainSingle(); f.Api.Sent.Should().ContainSingle();
    }

    [Fact]
    public async Task Offset_is_saved_only_after_durable_handling_and_restart_does_not_replay()
    {
        await using var f = new Fixture(); f.Api.ThrowOnSend = true;
        f.Api.Updates.Enqueue([new(7, new(42, 1, "/start"), null)]);
        await f.WaitForDelayAsync();
        f.Settings.Saved.Should().BeEmpty();
        await f.Service.StopAsync();
        f.Api.ThrowOnSend = false; f.Api.Updates.Enqueue([new(7, new(42, 1, "/start"), null)]);
        await f.RunAsync(8);
        f.Settings.Current.UpdateOffset.Should().Be(8);
        await f.Service.StartAsync(); await Task.Delay(20); await f.Service.StopAsync();
        f.Api.Offsets.Last().Should().Be(8);
    }

    [Fact]
    public async Task Polling_handles_unauthorized_rate_limit_transient_backoff_and_cancellation()
    {
        await using var unauthorized = new Fixture(); unauthorized.Api.Errors.Enqueue(new TelegramBotException(TelegramBotErrorKind.Unauthorized, "no"));
        await unauthorized.Service.StartAsync(); await unauthorized.WaitStateAsync(TelegramBotState.Unauthorized);
        unauthorized.Service.State.Should().Be(TelegramBotState.Unauthorized);

        await using var retry = new Fixture();
        retry.Api.Errors.Enqueue(new TelegramBotException(TelegramBotErrorKind.RateLimited, "slow", TimeSpan.FromSeconds(17)));
        retry.Api.Errors.Enqueue(new TelegramBotException(TelegramBotErrorKind.Transient, "one"));
        retry.Api.Errors.Enqueue(new TelegramBotException(TelegramBotErrorKind.Transient, "two"));
        await retry.Service.StartAsync();
        await retry.Delay.WaitCountAsync(3);
        retry.Delay.Values.Take(3).Should().Equal(TimeSpan.FromSeconds(17), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
        await retry.Service.StopAsync(); retry.Service.State.Should().Be(TelegramBotState.Stopped);
    }

    [Fact]
    public async Task ApplySettings_restarts_safely_and_lifecycle_never_has_two_pollers()
    {
        await using var f = new Fixture(); await f.Service.StartAsync();
        await Task.WhenAll(f.Service.StartAsync(), f.Service.ApplySettingsAsync(), f.Service.ApplySettingsAsync());
        await f.Service.StopAsync(); f.Api.MaxConcurrentPolls.Should().Be(1);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public FakeSettings Settings { get; } = new(); public FakeApi Api { get; } = new(); public FakeRegistry Registry { get; } = new();
        public FakeRuntime Runtime { get; } = new(); public FakeAlgo Algo { get; } = new(); public FakeTimeProvider Clock { get; } = new(); public FakeDelay Delay { get; } = new();
        public TelegramBotService Service { get; }
        public TerminalRegistration Registration(string name = "Terminal") => new(Guid.NewGuid(), name, "exe", "data", "work", [], DiscoverySource.Manual, true);
        public TerminalAccountSnapshot Snapshot() => new(1, Clock.GetUtcNow(), "data", 123456, "Name", "Server", "Company", AccountTradeMode.Demo, true, AlgoTradingState.Disabled, true, true, true);
        public TerminalRegistration AvailableTerminal(string name = "Terminal") { var registration = Registration(name); Registry.Items.Add(registration); Runtime.Snapshots[registration.Id] = Snapshot(); return registration; }
        public Fixture() { Algo.OnSet = () => Api.Calls.Add("algo"); Service = new(Settings, new Protector(), Api, Registry, Runtime, Algo, Clock, Delay); }
        public async Task RunAsync(long expectedOffset) { await Service.StartAsync(); await Settings.WaitOffsetAsync(expectedOffset); await Service.StopAsync(); }
        public async Task<string> CreateConfirmationAsync(string command, long update = 0) { Api.Updates.Enqueue([new(update, new(42, 1, command), null)]); await RunAsync(update + 1); return Api.LastDelivered.Keyboard.Rows.SelectMany(x => x).Single(x => x.CallbackData.StartsWith("confirm:")).CallbackData; }
        public async Task<string> CreatePickerAsync(string command) { Api.Updates.Enqueue([new(0, new(42, 1, command), null)]); await RunAsync(1); return Api.LastDelivered.Keyboard.Rows.SelectMany(x => x).First(x => x.CallbackData.StartsWith("terminal:")).CallbackData; }
        public async Task SendCallbacksAsync(params TelegramCallbackQuery[] callbacks) { var start = Settings.Current.UpdateOffset; Api.Updates.Enqueue(callbacks.Select((x, i) => new TelegramUpdate(start + i, null, x)).ToArray()); await RunAsync(start + callbacks.Length); }
        public async Task WaitForDelayAsync() { await Service.StartAsync(); await Delay.WaitCountAsync(1); }
        public async Task WaitStateAsync(TelegramBotState state) { var until = DateTime.UtcNow.AddSeconds(2); while (Service.State != state && DateTime.UtcNow < until) await Task.Delay(5); }
        public async ValueTask DisposeAsync() => await Service.DisposeAsync();
    }
    private sealed class FakeSettings : ITelegramSettingsStore
    {
        public TelegramSettings Current = new(new("protected"), 42, 0, true); public List<TelegramSettings> Saved { get; } = [];
        public Task<TelegramSettings?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult<TelegramSettings?>(Current);
        public Task SaveAsync(TelegramSettings settings, CancellationToken cancellationToken = default) { Current = settings; lock (Saved) Saved.Add(settings); return Task.CompletedTask; }
        public Task RemoveAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public async Task WaitOffsetAsync(long offset) { var until = DateTime.UtcNow.AddSeconds(2); while (Current.UpdateOffset < offset && DateTime.UtcNow < until) await Task.Delay(5); Current.UpdateOffset.Should().Be(offset); }
    }
    private sealed class Protector : ISecretProtector { public ProtectedTelegramToken Protect(string plaintext) => new(plaintext); public string Unprotect(ProtectedTelegramToken protectedValue) => "secret"; }
    private sealed class FakeApi : ITelegramBotApi
    {
        private int concurrent; public int MaxConcurrentPolls; public Queue<IReadOnlyList<TelegramUpdate>> Updates { get; } = new(); public Queue<Exception> Errors { get; } = new();
        public List<string> Calls { get; } = []; public List<long> Offsets { get; } = []; public List<(long ChatId, TelegramMessage Message)> Sent { get; } = []; public List<TelegramMessage> Edits { get; } = [];
        public bool ThrowOnEdit; public bool ThrowOnSend; public TelegramMessage LastDelivered => Edits.LastOrDefault() ?? Sent.Last().Message;
        public Task<TelegramConnectionState> GetConnectionStateAsync(string botToken, CancellationToken cancellationToken = default) { Calls.Add("connection"); return Task.FromResult(new TelegramConnectionState(true, "bot", 0, null)); }
        public async Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(string botToken, long offset, CancellationToken cancellationToken = default) { Calls.Add("updates"); Offsets.Add(offset); var active = Interlocked.Increment(ref concurrent); MaxConcurrentPolls = Math.Max(MaxConcurrentPolls, active); try { if (Errors.TryDequeue(out var e)) throw e; if (Updates.TryDequeue(out var value)) return value; await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return []; } finally { Interlocked.Decrement(ref concurrent); } }
        public Task SendMessageAsync(string botToken, long chatId, TelegramMessage message, CancellationToken cancellationToken = default) { Calls.Add("send"); if (ThrowOnSend) throw new InvalidOperationException("send failed"); Sent.Add((chatId, message)); return Task.CompletedTask; }
        public Task EditMessageAsync(string botToken, long chatId, long messageId, TelegramMessage message, CancellationToken cancellationToken = default) { Calls.Add("edit"); Edits.Add(message); if (ThrowOnEdit) throw new InvalidOperationException("edit failed"); return Task.CompletedTask; }
        public Task AnswerCallbackAsync(string botToken, string callbackQueryId, string? text = null, CancellationToken cancellationToken = default) { Calls.Add($"answer:{callbackQueryId}"); return Task.CompletedTask; }
    }
    private sealed class FakeRegistry : ITerminalRegistry { public List<TerminalRegistration> Items { get; } = []; public int LoadCount; public Task<IReadOnlyList<TerminalRegistration>> LoadAsync(CancellationToken cancellationToken = default) { LoadCount++; return Task.FromResult<IReadOnlyList<TerminalRegistration>>([.. Items]); } public Task SaveAsync(IReadOnlyList<TerminalRegistration> terminals, CancellationToken cancellationToken = default) => Task.CompletedTask; }
    private sealed class FakeRuntime : ITerminalRuntimeInspector { public Dictionary<Guid, TerminalAccountSnapshot> Snapshots { get; } = []; public Dictionary<Guid, Queue<TerminalAccountSnapshot?>> Sequences { get; } = []; public int ReadCount; public Task<TerminalAccountSnapshot?> ReadAsync(TerminalRegistration terminal, CancellationToken cancellationToken = default) { ReadCount++; return Task.FromResult(Sequences.TryGetValue(terminal.Id, out var sequence) && sequence.TryDequeue(out var value) ? value : Snapshots.GetValueOrDefault(terminal.Id)); } }
    private sealed class FakeAlgo : IAlgoTradingService
    {
        private int concurrent; public int MaxConcurrent; public Action? OnSet; public List<AlgoTradingRequest> Requests { get; } = []; public Queue<AlgoTradingOperationResult> Results { get; } = new(); public Queue<Exception?> Errors { get; } = new();
        public Task<AlgoTradingOperationResult> SetAsync(AlgoTradingRequest request, CancellationToken cancellationToken = default) { var active = Interlocked.Increment(ref concurrent); MaxConcurrent = Math.Max(MaxConcurrent, active); try { Requests.Add(request); OnSet?.Invoke(); return Complete(); } finally { Interlocked.Decrement(ref concurrent); } Task<AlgoTradingOperationResult> Complete() { if (Errors.TryDequeue(out var error) && error is not null) throw error; return Task.FromResult(Results.TryDequeue(out var result) ? result : new(request.TerminalId, "Terminal", request.Enable, new(true, "changed", null))); } }
    }
    private sealed class FakeTimeProvider : TimeProvider { private DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero); public override DateTimeOffset GetUtcNow() => now; public void Advance(TimeSpan value) => now += value; }
    private sealed class FakeDelay : IDelay { public List<TimeSpan> Values { get; } = []; public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) { Values.Add(delay); return Task.CompletedTask; } public async Task WaitCountAsync(int count) { var until = DateTime.UtcNow.AddSeconds(2); while (Values.Count < count && DateTime.UtcNow < until) await Task.Delay(5); Values.Count.Should().BeGreaterThanOrEqualTo(count); } }
}
