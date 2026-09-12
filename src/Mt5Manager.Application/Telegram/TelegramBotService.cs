using System.Security.Cryptography;
using Mt5Manager.Application.Abstractions;
using Mt5Manager.Domain.Models;

namespace Mt5Manager.Application.Telegram;

public sealed class TelegramBotService : ITelegramBotService
{
    private readonly ITelegramSettingsStore settingsStore;
    private readonly ISecretProtector protector;
    private readonly ITelegramBotApi api;
    private readonly ITerminalRegistry registry;
    private readonly ITerminalRuntimeInspector runtime;
    private readonly IAlgoTradingService algo;
    private readonly TimeProvider clock;
    private readonly IDelay delay;
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly Dictionary<string, Confirmation> confirmations = [];
    private CancellationTokenSource? pollingCancellation;
    private Task? pollingTask;
    private TelegramSettings? settings;
    private string? token;

    public TelegramBotService(ITelegramSettingsStore settingsStore, ISecretProtector protector, ITelegramBotApi api,
        ITerminalRegistry registry, ITerminalRuntimeInspector runtime, IAlgoTradingService algo,
        TimeProvider clock, IDelay delay)
    {
        this.settingsStore = settingsStore; this.protector = protector; this.api = api; this.registry = registry;
        this.runtime = runtime; this.algo = algo; this.clock = clock; this.delay = delay;
    }

    public TelegramBotState State { get; private set; } = TelegramBotState.Stopped;
    public event EventHandler? StateChanged;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (pollingTask is { IsCompleted: false }) return;
            settings = await settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (settings is null || !settings.Enabled) return;
            token = protector.Unprotect(settings.BotToken);
            pollingCancellation = new CancellationTokenSource();
            SetState(TelegramBotState.Running);
            pollingTask = Task.Run(() => PollAsync(pollingCancellation.Token), CancellationToken.None);
        }
        finally { lifecycle.Release(); }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task? task;
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { pollingCancellation?.Cancel(); task = pollingTask; }
        finally { lifecycle.Release(); }
        if (task is not null) try { await task.WaitAsync(cancellationToken).ConfigureAwait(false); } catch (OperationCanceledException) { }
        SetState(TelegramBotState.Stopped);
    }

    public async Task ApplySettingsAsync(CancellationToken cancellationToken = default)
    { await StopAsync(cancellationToken).ConfigureAwait(false); await StartAsync(cancellationToken).ConfigureAwait(false); }

    public async ValueTask DisposeAsync() { await StopAsync().ConfigureAwait(false); pollingCancellation?.Dispose(); lifecycle.Dispose(); }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        var failures = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested && settings is not null && token is not null)
            {
                try
                {
                    var updates = await api.GetUpdatesAsync(token, settings.UpdateOffset, cancellationToken).ConfigureAwait(false);
                    foreach (var update in updates.OrderBy(x => x.UpdateId))
                    {
                        await HandleAsync(update, cancellationToken).ConfigureAwait(false);
                        settings = settings with { UpdateOffset = update.UpdateId + 1 };
                        await settingsStore.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
                    }
                    failures = 0;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                catch (Exception error)
                {
                    var (kind, retryAfter) = Classify(error);
                    if (kind == TelegramBotErrorKind.Unauthorized) { SetState(TelegramBotState.Unauthorized); break; }
                    var wait = kind == TelegramBotErrorKind.RateLimited && retryAfter is not null
                        ? retryAfter.Value : TimeSpan.FromSeconds(Math.Min(30, 1 << Math.Min(failures++, 4)));
                    await delay.DelayAsync(wait, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally { if (State != TelegramBotState.Unauthorized) SetState(TelegramBotState.Stopped); }
    }

    private async Task HandleAsync(TelegramUpdate update, CancellationToken cancellationToken)
    {
        if (settings is null || token is null) return;
        if (update.Message is { } message)
        {
            if (message.ChatId != settings.AllowedChatId) return;
            await HandleCommandAsync(message, cancellationToken).ConfigureAwait(false);
        }
        else if (update.CallbackQuery is { } callback)
        {
            if (callback.ChatId != settings.AllowedChatId) return;
            await api.AnswerCallbackAsync(token, callback.Id, cancellationToken: cancellationToken).ConfigureAwait(false);
            await HandleCallbackAsync(callback, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleCommandAsync(TelegramIncomingMessage message, CancellationToken cancellationToken)
    {
        var command = (message.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant();
        if (command is "/start" or "/status") { await SendDashboardAsync(message.ChatId, cancellationToken).ConfigureAwait(false); return; }
        if (command is "/onall" or "/offall") { await BeginAllAsync(message.ChatId, message.MessageId, command == "/onall", false, cancellationToken).ConfigureAwait(false); return; }
        if (command is "/on" or "/off") { await ShowPickerAsync(message.ChatId, message.MessageId, command == "/on", false, cancellationToken).ConfigureAwait(false); return; }
        await SendDashboardAsync(message.ChatId, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleCallbackAsync(TelegramCallbackQuery callback, CancellationToken cancellationToken)
    {
        var parts = callback.Data.Split(':');
        if (callback.Data == "status") { await SendDashboardAsync(callback.ChatId, cancellationToken).ConfigureAwait(false); return; }
        if (parts.Length == 2 && parts[0] == "pick") { await ShowPickerAsync(callback.ChatId, callback.MessageId, parts[1] == "on", true, cancellationToken); return; }
        if (parts.Length == 2 && parts[0] == "all") { await BeginAllAsync(callback.ChatId, callback.MessageId, parts[1] == "on", true, cancellationToken); return; }
        if (parts.Length == 4 && parts[0] == "terminal" && Guid.TryParseExact(parts[2], "N", out var id))
        {
            if (!Take(parts[3], callback.ChatId, out var picker) || picker.TerminalIds is null || !picker.TerminalIds.Contains(id) || picker.Enable != (parts[1] == "on")) { await InvalidAsync(callback, cancellationToken); return; }
            await BeginConfirmationAsync(callback.ChatId, callback.MessageId, picker.Enable, [id], cancellationToken); return;
        }
        if (parts.Length == 2 && parts[0] == "cancel") { Take(parts[1], callback.ChatId, out _); return; }
        if (parts.Length == 2 && parts[0] == "confirm")
        {
            if (!Take(parts[1], callback.ChatId, out var confirmation) || confirmation.TerminalIds is null) { await InvalidAsync(callback, cancellationToken); return; }
            await ExecuteAsync(callback.ChatId, confirmation.Enable, confirmation.TerminalIds, cancellationToken); return;
        }
        await InvalidAsync(callback, cancellationToken);
    }

    private async Task ShowPickerAsync(long chatId, long messageId, bool enable, bool edit, CancellationToken ct)
    {
        var terminals = await LoadTerminalsAsync(ct).ConfigureAwait(false); var key = Store(chatId, enable, terminals.Select(x => x.Id).ToArray());
        await DeliverAsync(chatId, messageId, TelegramDashboard.TerminalPicker(enable, terminals, key), edit, ct).ConfigureAwait(false);
    }
    private async Task BeginAllAsync(long chatId, long messageId, bool enable, bool edit, CancellationToken ct)
    {
        var ids = (await registry.LoadAsync(ct).ConfigureAwait(false)).Select(x => x.Id).ToArray();
        var key = Store(chatId, enable, ids); await DeliverAsync(chatId, messageId, TelegramDashboard.ConfirmAll(enable, ids.Length, key), edit, ct).ConfigureAwait(false);
    }
    private async Task BeginConfirmationAsync(long chatId, long messageId, bool enable, Guid[] ids, CancellationToken ct)
    {
        var (registration, snapshot) = await FreshAsync(ids[0], ct).ConfigureAwait(false);
        if (registration is null || snapshot is null) { await DeliverAsync(chatId, messageId, new TelegramMessage("Terminal tidak tersedia.", new([])), true, ct); return; }
        var terminal = new TelegramTerminal(registration.Id, registration.DisplayName, snapshot.Login.ToString(), true);
        var key = Store(chatId, enable, ids);
        await DeliverAsync(chatId, messageId, TelegramDashboard.ConfirmTerminal(enable, snapshot.GlobalAlgoTrading == AlgoTradingState.Enabled, terminal, key), true, ct).ConfigureAwait(false);
    }

    private async Task ExecuteAsync(long chatId, bool enable, Guid[] ids, CancellationToken ct)
    {
        var registrations = await registry.LoadAsync(ct).ConfigureAwait(false); var results = new List<TelegramTerminalResult>();
        foreach (var id in ids)
        {
            var registration = registrations.FirstOrDefault(x => x.Id == id);
            if (registration is null) { results.Add(new(id, "Terminal", null, enable, TelegramTerminalOutcome.Failed, "Terminal tidak tersedia.")); continue; }
            var snapshot = await runtime.ReadAsync(registration, ct).ConfigureAwait(false);
            if (snapshot is null) { results.Add(new(id, registration.DisplayName, null, enable, TelegramTerminalOutcome.Failed, "Akun tidak tersedia.")); continue; }
            try
            {
                var operation = await algo.SetAsync(new(id, enable, AlgoOperationSource.Telegram), ct).ConfigureAwait(false);
                var already = operation.Result.Success && operation.Result.Message.Contains("already", StringComparison.OrdinalIgnoreCase);
                results.Add(new(id, registration.DisplayName, snapshot.Login.ToString(), enable, operation.Result.Success ? (already ? TelegramTerminalOutcome.AlreadyInRequestedState : TelegramTerminalOutcome.Changed) : TelegramTerminalOutcome.Failed, operation.Result.Success ? null : operation.Result.Message));
            }
            catch (Exception error) { results.Add(new(id, registration.DisplayName, snapshot.Login.ToString(), enable, TelegramTerminalOutcome.Failed, error.Message)); }
        }
        foreach (var text in TelegramDashboard.Results(results)) await api.SendMessageAsync(token!, chatId, new(text, new([])), ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<TelegramTerminal>> LoadTerminalsAsync(CancellationToken ct)
    {
        var registrations = await registry.LoadAsync(ct).ConfigureAwait(false); var result = new List<TelegramTerminal>();
        foreach (var registration in registrations) { var snapshot = await runtime.ReadAsync(registration, ct).ConfigureAwait(false); result.Add(new(registration.Id, registration.DisplayName, snapshot?.Login.ToString(), snapshot is not null)); }
        return result;
    }
    private async Task<(TerminalRegistration? Registration, TerminalAccountSnapshot? Snapshot)> FreshAsync(Guid id, CancellationToken ct)
    { var registration = (await registry.LoadAsync(ct).ConfigureAwait(false)).FirstOrDefault(x => x.Id == id); return (registration, registration is null ? null : await runtime.ReadAsync(registration, ct).ConfigureAwait(false)); }
    private async Task SendDashboardAsync(long chatId, CancellationToken ct)
    { var connection = await api.GetConnectionStateAsync(token!, ct).ConfigureAwait(false); var count = (await registry.LoadAsync(ct).ConfigureAwait(false)).Count; await api.SendMessageAsync(token!, chatId, TelegramDashboard.Main(connection with { TerminalCount = count }), ct).ConfigureAwait(false); }
    private async Task DeliverAsync(long chatId, long messageId, TelegramMessage message, bool edit, CancellationToken ct)
    { if (edit) try { await api.EditMessageAsync(token!, chatId, messageId, message, ct).ConfigureAwait(false); return; } catch { } await api.SendMessageAsync(token!, chatId, message, ct).ConfigureAwait(false); }
    private Task InvalidAsync(TelegramCallbackQuery callback, CancellationToken ct) => api.SendMessageAsync(token!, callback.ChatId, new("Konfirmasi tidak valid atau kedaluwarsa.", new([])), ct);
    private string Store(long chat, bool enable, Guid[] ids) { var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(9)).ToLowerInvariant(); confirmations[key] = new(chat, enable, [.. ids], clock.GetUtcNow().AddMinutes(2)); return key; }
    private bool Take(string key, long chat, out Confirmation value) { if (confirmations.TryGetValue(key, out value!) && value.ChatId == chat && value.ExpiresAt > clock.GetUtcNow() && confirmations.Remove(key)) return true; value = null!; return false; }
    private void SetState(TelegramBotState value) { if (State == value) return; State = value; StateChanged?.Invoke(this, EventArgs.Empty); }
    private static (TelegramBotErrorKind Kind, TimeSpan? RetryAfter) Classify(Exception error) =>
        error is ITelegramBotApiError known
            ? (known.BotErrorKind, known.RetryAfter)
            : (TelegramBotErrorKind.Transient, null);
    private sealed record Confirmation(long ChatId, bool Enable, Guid[] TerminalIds, DateTimeOffset ExpiresAt);
}

