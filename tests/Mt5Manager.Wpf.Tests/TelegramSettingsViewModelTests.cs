using FluentAssertions;
using Mt5Manager.Application.Telegram;
using Mt5Manager.Wpf.ViewModels;

namespace Mt5Manager.Wpf.Tests;

public sealed class TelegramSettingsViewModelTests
{
    const string Token = "123456:top-secret-token";

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-7")]
    [InlineData("not-a-number")]
    public async Task Save_rejects_non_positive_numeric_chat_id_without_side_effects(string chatId)
    {
        var fixture = new Fixture();
        fixture.ViewModel.Enabled = true;
        fixture.ViewModel.BotToken = Token;
        fixture.ViewModel.AllowedChatId = chatId;

        var saved = await fixture.ViewModel.SaveAsync();

        saved.Should().BeFalse();
        fixture.ViewModel.ValidationMessage.Should().Contain("positive numeric");
        fixture.Store.Events.Should().BeEmpty();
        fixture.Bot.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task Save_requires_token_when_enabled()
    {
        var fixture = new Fixture();
        fixture.ViewModel.Enabled = true;
        fixture.ViewModel.AllowedChatId = "42";

        (await fixture.ViewModel.SaveAsync()).Should().BeFalse();

        fixture.ViewModel.ValidationMessage.Should().Contain("token");
        fixture.Store.Events.Should().BeEmpty();
        fixture.Bot.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task Load_unprotects_existing_token_into_editor_without_projecting_plaintext()
    {
        var fixture = new Fixture(new TelegramSettings(new ProtectedTelegramToken("ciphertext"), 42, 9, true));

        await fixture.ViewModel.LoadAsync();

        fixture.ViewModel.Enabled.Should().BeTrue();
        fixture.ViewModel.AllowedChatId.Should().Be("42");
        fixture.ViewModel.BotToken.Should().Be(Token);
        fixture.ViewModel.Status.Should().NotContain(Token);
        fixture.ViewModel.ValidationMessage.Should().NotContain(Token);
    }

    [Fact]
    public async Task Save_allows_disabled_configuration_without_token()
    {
        var fixture = new Fixture();
        fixture.ViewModel.Enabled = false;
        fixture.ViewModel.BotToken = "";
        fixture.ViewModel.AllowedChatId = "42";

        (await fixture.ViewModel.SaveAsync()).Should().BeTrue();

        fixture.Store.Saved!.Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task Test_connection_gets_identity_then_messages_authorized_chat_without_saving_or_starting()
    {
        var fixture = new Fixture();
        fixture.ViewModel.Enabled = true;
        fixture.ViewModel.BotToken = Token;
        fixture.ViewModel.AllowedChatId = "42";

        await fixture.ViewModel.TestConnectionAsync();

        fixture.Api.Events.Should().Equal("getMe", "send:42");
        fixture.Store.Events.Should().BeEmpty();
        fixture.Bot.Events.Should().BeEmpty();
        fixture.ViewModel.Status.Should().Contain("connected").And.Contain("test_bot");
    }

    [Fact]
    public async Task Save_protects_and_persists_before_applying_settings()
    {
        var timeline = new List<string>();
        var fixture = new Fixture(timeline: timeline);
        fixture.ViewModel.Enabled = true;
        fixture.ViewModel.BotToken = Token;
        fixture.ViewModel.AllowedChatId = "42";

        (await fixture.ViewModel.SaveAsync()).Should().BeTrue();

        timeline.Should().Equal("protect", "save", "apply");
        fixture.Store.Saved.Should().Be(new TelegramSettings(new ProtectedTelegramToken("ciphertext"), 42, 0, true));
        fixture.Bot.StartCalls.Should().Be(0);
    }

    [Fact]
    public async Task Errors_and_status_never_include_plaintext_token_and_busy_state_recovers()
    {
        var fixture = new Fixture();
        fixture.ViewModel.Enabled = true;
        fixture.ViewModel.BotToken = Token;
        fixture.ViewModel.AllowedChatId = "42";
        fixture.Api.Exception = new InvalidOperationException($"request containing {Token} failed");

        await fixture.ViewModel.TestConnectionAsync();

        fixture.ViewModel.IsBusy.Should().BeFalse();
        fixture.ViewModel.CanMutate.Should().BeTrue();
        fixture.ViewModel.Status.Should().NotContain(Token);
        fixture.ViewModel.ValidationMessage.Should().NotContain(Token);
        fixture.ViewModel.Status.Should().Contain("failed");
    }

    [Fact]
    public async Task Confirmed_remove_clears_store_editor_and_applies_disabled_settings()
    {
        var fixture = new Fixture();
        fixture.ViewModel.Enabled = true;
        fixture.ViewModel.BotToken = Token;
        fixture.ViewModel.AllowedChatId = "42";

        await fixture.ViewModel.RemoveConfirmedAsync();

        fixture.Store.Events.Should().Equal("remove");
        fixture.Bot.Events.Should().Equal("apply");
        fixture.ViewModel.BotToken.Should().BeEmpty();
        fixture.ViewModel.AllowedChatId.Should().BeEmpty();
        fixture.ViewModel.Enabled.Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Failed_remove_preserves_editor_secret_for_retry(bool storeFails)
    {
        var fixture = new Fixture();
        fixture.ViewModel.Enabled = true;
        fixture.ViewModel.BotToken = Token;
        fixture.ViewModel.AllowedChatId = "42";
        if (storeFails) fixture.Store.RemoveException = new InvalidOperationException($"failed {Token}");
        else fixture.Bot.ApplyException = new InvalidOperationException($"failed {Token}");

        var removed = await fixture.ViewModel.RemoveConfirmedAsync();

        removed.Should().BeFalse();
        fixture.ViewModel.BotToken.Should().Be(Token);
        fixture.ViewModel.AllowedChatId.Should().Be("42");
        fixture.ViewModel.Enabled.Should().BeTrue();
        fixture.ViewModel.Status.Should().NotContain(Token);
        fixture.ViewModel.ValidationMessage.Should().NotContain(Token);
    }

    [Fact]
    public async Task Cancelled_load_cannot_repopulate_cleared_secret_after_close_boundary()
    {
        var fixture = new Fixture(new TelegramSettings(new ProtectedTelegramToken("ciphertext"), 42, 0, true));
        fixture.Store.LoadBlock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var lifetime = new CancellationTokenSource();

        var loading = fixture.ViewModel.LoadAsync(lifetime.Token);
        await fixture.Store.LoadStarted.Task;
        lifetime.Cancel();
        fixture.ViewModel.ClearSecret();
        fixture.Store.LoadBlock.SetResult();
        await loading;

        fixture.ViewModel.BotToken.Should().BeEmpty();
    }

    [Fact]
    public async Task Cancellation_between_connection_check_and_send_prevents_message()
    {
        var fixture = new Fixture();
        fixture.ViewModel.Enabled = true;
        fixture.ViewModel.BotToken = Token;
        fixture.ViewModel.AllowedChatId = "42";
        fixture.Api.AfterConnection = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var lifetime = new CancellationTokenSource();

        var testing = fixture.ViewModel.TestConnectionAsync(lifetime.Token);
        await fixture.Api.ConnectionReturned.Task;
        lifetime.Cancel();
        fixture.ViewModel.ClearSecret();
        fixture.Api.AfterConnection.SetResult();
        await testing;

        fixture.Api.Events.Should().Equal("getMe");
        fixture.ViewModel.BotToken.Should().BeEmpty();
    }

    [Fact]
    public async Task Cancellation_does_not_surface_an_error_and_busy_disables_mutation()
    {
        var fixture = new Fixture();
        fixture.ViewModel.Enabled = true;
        fixture.ViewModel.BotToken = Token;
        fixture.ViewModel.AllowedChatId = "42";
        fixture.Api.Block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();

        var operation = fixture.ViewModel.TestConnectionAsync(cancellation.Token);
        await fixture.Api.Started.Task;
        fixture.ViewModel.IsBusy.Should().BeTrue();
        fixture.ViewModel.CanMutate.Should().BeFalse();
        cancellation.Cancel();
        await operation;

        fixture.ViewModel.IsBusy.Should().BeFalse();
        fixture.ViewModel.ValidationMessage.Should().BeNull();
    }

    sealed class Fixture
    {
        public Fixture(TelegramSettings? settings = null, List<string>? timeline = null)
        {
            timeline ??= [];
            Store = new Store(settings, timeline);
            Protector = new Protector(timeline);
            Api = new Api();
            Bot = new Bot(timeline);
            ViewModel = new TelegramSettingsViewModel(Store, Protector, Api, Bot);
        }
        public Store Store { get; }
        public Protector Protector { get; }
        public Api Api { get; }
        public Bot Bot { get; }
        public TelegramSettingsViewModel ViewModel { get; }
    }

    sealed class Store(TelegramSettings? loaded, List<string> timeline) : ITelegramSettingsStore
    {
        public List<string> Events { get; } = [];
        public TelegramSettings? Saved { get; private set; }
        public Exception? RemoveException { get; set; }
        public TaskCompletionSource LoadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? LoadBlock { get; set; }
        public async Task<TelegramSettings?> LoadAsync(CancellationToken cancellationToken = default) { LoadStarted.TrySetResult(); if (LoadBlock is not null) await LoadBlock.Task; return loaded; }
        public Task SaveAsync(TelegramSettings settings, CancellationToken cancellationToken = default) { Events.Add("save"); timeline.Add("save"); Saved = settings; return Task.CompletedTask; }
        public Task RemoveAsync(CancellationToken cancellationToken = default) { Events.Add("remove"); timeline.Add("remove"); return RemoveException is null ? Task.CompletedTask : Task.FromException(RemoveException); }
    }

    sealed class Protector(List<string> timeline) : ISecretProtector
    {
        public ProtectedTelegramToken Protect(string plaintext) { plaintext.Should().BeOneOf(Token, ""); timeline.Add("protect"); return new("ciphertext"); }
        public string Unprotect(ProtectedTelegramToken protectedValue) => Token;
    }

    sealed class Api : ITelegramBotApi
    {
        public List<string> Events { get; } = [];
        public Exception? Exception { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? Block { get; set; }
        public TaskCompletionSource ConnectionReturned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? AfterConnection { get; set; }
        public async Task<TelegramConnectionState> GetConnectionStateAsync(string botToken, CancellationToken cancellationToken = default)
        {
            botToken.Should().Be(Token); Events.Add("getMe"); Started.TrySetResult();
            if (Block is not null) await Block.Task.WaitAsync(cancellationToken);
            if (Exception is not null) throw Exception;
            ConnectionReturned.TrySetResult();
            if (AfterConnection is not null) await AfterConnection.Task;
            return new(true, "test_bot", 0, null);
        }
        public Task SendMessageAsync(string botToken, long chatId, TelegramMessage message, CancellationToken cancellationToken = default) { Events.Add($"send:{chatId}"); return Task.CompletedTask; }
        public Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(string botToken, long offset, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task EditMessageAsync(string botToken, long chatId, long messageId, TelegramMessage message, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task AnswerCallbackAsync(string botToken, string callbackQueryId, string? text = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    sealed class Bot(List<string> timeline) : ITelegramBotService
    {
        public TelegramBotState State => TelegramBotState.Stopped;
#pragma warning disable CS0067 // Required interface member is intentionally unused by this test double.
        public event EventHandler? StateChanged;
#pragma warning restore CS0067
        public List<string> Events { get; } = [];
        public Exception? ApplyException { get; set; }
        public int StartCalls { get; private set; }
        public Task StartAsync(CancellationToken cancellationToken = default) { StartCalls++; Events.Add("start"); return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ApplySettingsAsync(CancellationToken cancellationToken = default) { Events.Add("apply"); timeline.Add("apply"); return ApplyException is null ? Task.CompletedTask : Task.FromException(ApplyException); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
