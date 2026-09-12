using FluentAssertions;
using Mt5Manager.Application.Abstractions;
using Mt5Manager.Application.Telegram;
using Mt5Manager.Domain.Models;

namespace Mt5Manager.Application.Tests.Telegram;

public sealed class TelegramBotServiceTests
{
    [Fact]
    public async Task Unauthorized_updates_are_silently_skipped_and_offset_is_persisted()
    {
        var fixture = new Fixture();
        fixture.Api.Updates.Enqueue([new TelegramUpdate(4, new TelegramIncomingMessage(999, 1, "/start"), null)]);
        fixture.Api.OnGetUpdates = fixture.Stop;

        await fixture.Service.StartAsync();
        await fixture.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await fixture.Service.StopAsync();

        fixture.Api.Sent.Should().BeEmpty();
        fixture.Settings.Saved.Should().ContainSingle(x => x.UpdateOffset == 5);
    }

    [Fact]
    public async Task Start_command_sends_dashboard_to_authorized_chat()
    {
        var fixture = new Fixture();
        fixture.Api.Updates.Enqueue([new TelegramUpdate(0, new TelegramIncomingMessage(42, 8, "/start"), null)]);
        fixture.Api.OnGetUpdates = fixture.Stop;

        await fixture.Service.StartAsync();
        await fixture.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await fixture.Service.StopAsync();

        fixture.Api.Sent.Should().ContainSingle(x => x.Message.Text.Contains("MT5 Manager"));
    }

    [Fact]
    public async Task Lifecycle_is_idempotent_and_uses_one_polling_task()
    {
        var fixture = new Fixture();
        fixture.Api.OnGetUpdates = fixture.Stop;
        await Task.WhenAll(fixture.Service.StartAsync(), fixture.Service.StartAsync());
        await fixture.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.WhenAll(fixture.Service.StopAsync(), fixture.Service.StopAsync());
        fixture.Api.MaxConcurrentPolls.Should().Be(1);
    }

    private sealed class Fixture
    {
        public readonly FakeSettings Settings = new();
        public readonly FakeApi Api = new();
        public readonly TaskCompletionSource Stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TelegramBotService Service;
        public Fixture() => Service = new TelegramBotService(Settings, new Protector(), Api, new Registry(), new Runtime(), new Algo(), TimeProvider.System, new ImmediateDelay());
        public void Stop() => Stopped.TrySetResult();
    }

    private sealed class FakeSettings : ITelegramSettingsStore
    {
        public TelegramSettings Current = new(new("protected"), 42, 0, true);
        public List<TelegramSettings> Saved { get; } = [];
        public Task<TelegramSettings?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult<TelegramSettings?>(Current);
        public Task SaveAsync(TelegramSettings settings, CancellationToken cancellationToken = default) { Current = settings; Saved.Add(settings); return Task.CompletedTask; }
        public Task RemoveAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class Protector : ISecretProtector { public ProtectedTelegramToken Protect(string plaintext) => new(plaintext); public string Unprotect(ProtectedTelegramToken protectedValue) => "secret"; }
    private sealed class FakeApi : ITelegramBotApi
    {
        int concurrent;
        public int MaxConcurrentPolls;
        public Queue<IReadOnlyList<TelegramUpdate>> Updates { get; } = new();
        public Action? OnGetUpdates;
        public List<(long ChatId, TelegramMessage Message)> Sent { get; } = [];
        public Task<TelegramConnectionState> GetConnectionStateAsync(string botToken, CancellationToken cancellationToken = default) => Task.FromResult(new TelegramConnectionState(true, "bot", 0, null));
        public Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(string botToken, long offset, CancellationToken cancellationToken = default)
        {
            var active = Interlocked.Increment(ref concurrent); MaxConcurrentPolls = Math.Max(MaxConcurrentPolls, active);
            try { OnGetUpdates?.Invoke(); return Task.FromResult(Updates.Count > 0 ? Updates.Dequeue() : (IReadOnlyList<TelegramUpdate>)[]); }
            finally { Interlocked.Decrement(ref concurrent); }
        }
        public Task SendMessageAsync(string botToken, long chatId, TelegramMessage message, CancellationToken cancellationToken = default) { Sent.Add((chatId, message)); return Task.CompletedTask; }
        public Task EditMessageAsync(string botToken, long chatId, long messageId, TelegramMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task AnswerCallbackAsync(string botToken, string callbackQueryId, string? text = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class Registry : ITerminalRegistry { public Task<IReadOnlyList<TerminalRegistration>> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TerminalRegistration>>([]); public Task SaveAsync(IReadOnlyList<TerminalRegistration> terminals, CancellationToken cancellationToken = default) => Task.CompletedTask; }
    private sealed class Runtime : ITerminalRuntimeInspector { public Task<TerminalAccountSnapshot?> ReadAsync(TerminalRegistration terminal, CancellationToken cancellationToken = default) => Task.FromResult<TerminalAccountSnapshot?>(null); }
    private sealed class Algo : IAlgoTradingService { public Task<AlgoTradingOperationResult> SetAsync(AlgoTradingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException(); }
    private sealed class ImmediateDelay : IDelay { public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.CompletedTask; }
}
