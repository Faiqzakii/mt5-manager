using CommunityToolkit.Mvvm.ComponentModel;
using Mt5Manager.Application.Telegram;

namespace Mt5Manager.Wpf.ViewModels;

public sealed partial class TelegramSettingsViewModel(
    ITelegramSettingsStore store,
    ISecretProtector protector,
    ITelegramBotApi api,
    ITelegramBotService botService) : ObservableObject, IDisposable
{
    private TelegramSettings? loadedSettings;
    private SynchronizationContext? stateContext;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanMutate))]
    bool isBusy;

    [ObservableProperty]
    bool enabled;

    [ObservableProperty]
    string botToken = "";

    [ObservableProperty]
    string allowedChatId = "";

    [ObservableProperty]
    string status = "Load or enter Telegram bot settings.";

    [ObservableProperty]
    string? validationMessage;

    [ObservableProperty]
    string connectionStatus = "Nonaktif";

    public bool CanMutate => !IsBusy;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy) return;
        try
        {
            IsBusy = true;
            ValidationMessage = null;
            var settings = await store.LoadAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            loadedSettings = settings;
            if (settings is null)
            {
                ClearEditor();
                Status = "No Telegram bot configuration is saved.";
                UpdateConnectionStatus();
                return;
            }

            Enabled = settings.Enabled;
            AllowedChatId = settings.AllowedChatId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            BotToken = protector.Unprotect(settings.BotToken);
            Status = "Telegram bot configuration loaded. Token is hidden.";
            UpdateConnectionStatus();
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            ValidationMessage = "Unable to load Telegram bot configuration.";
            Status = "Loading failed. Check the saved configuration and try again.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy || !TryValidate(requireToken: true, out var chatId)) return;
        var token = BotToken;
        try
        {
            IsBusy = true;
            ValidationMessage = null;
            Status = "Testing Telegram connection…";
            ConnectionStatus = "Menghubungkan";
            var connection = await api.GetConnectionStateAsync(token, cancellationToken);
            if (!connection.IsConnected)
            {
                Status = "Connection failed. Verify the bot token and try again.";
                ConnectionStatus = "Token/Chat ID tidak valid";
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            await api.SendMessageAsync(token, chatId,
                new TelegramMessage("MT5 Manager connection test succeeded.", new TelegramKeyboard([])), cancellationToken);
            ConnectionStatus = connection.BotUsername is { Length: > 0 }
                ? $"Aktif sebagai @{connection.BotUsername.TrimStart('@')}"
                : "Aktif";
            Status = "Test message sent.";
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            Status = "Connection test failed. Verify the token, Chat ID, and network connection.";
            ConnectionStatus = "Gangguan koneksi — mencoba kembali";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<bool> SaveAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy || !TryValidate(requireToken: Enabled, out var chatId)) return false;
        try
        {
            IsBusy = true;
            ValidationMessage = null;
            await botService.StopAsync(cancellationToken);
            var settings = new TelegramSettings(protector.Protect(BotToken), chatId, loadedSettings?.UpdateOffset ?? 0, Enabled);
            await store.SaveAsync(settings, cancellationToken);
            loadedSettings = settings;
            await botService.StartAsync(cancellationToken);
            Status = "Telegram bot configuration saved and applied.";
            UpdateConnectionStatus();
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch
        {
            ValidationMessage = "Unable to save and apply Telegram bot configuration.";
            Status = "Save failed. The token remains hidden.";
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<bool> RemoveConfirmedAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy) return false;
        try
        {
            IsBusy = true;
            ValidationMessage = null;
            await botService.StopAsync(cancellationToken);
            await store.RemoveAsync(cancellationToken);
            loadedSettings = null;
            ClearEditor();
            Status = "Telegram bot configuration removed.";
            UpdateConnectionStatus();
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch
        {
            ValidationMessage = "Unable to remove Telegram bot configuration.";
            Status = "Removal failed. The token remains hidden.";
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void ClearSecret() => BotToken = "";

    bool TryValidate(bool requireToken, out long chatId)
    {
        ValidationMessage = null;
        if (!long.TryParse(AllowedChatId, out chatId) || chatId <= 0)
        {
            ValidationMessage = "Allowed Chat ID must be a positive numeric value.";
            return false;
        }

        if (requireToken && string.IsNullOrWhiteSpace(BotToken))
        {
            ValidationMessage = Enabled
                ? "Bot token is required when Telegram is enabled."
                : "Bot token is required to test this configuration.";
            return false;
        }

        return true;
    }

    void ClearEditor()
    {
        Enabled = false;
        BotToken = "";
        AllowedChatId = "";
    }

    public void AttachToBotState()
    {
        botService.StateChanged -= OnBotStateChanged;
        stateContext = SynchronizationContext.Current;
        botService.StateChanged += OnBotStateChanged;
        UpdateConnectionStatus();
    }

    void UpdateConnectionStatus() =>
        ConnectionStatus = botService.State switch
        {
            TelegramBotState.Running => "Aktif",
            TelegramBotState.Conflict => "Token bot sedang digunakan aplikasi MT5 Manager atau VPS lain — terapkan pengaturan atau mulai ulang untuk mencoba lagi",
            TelegramBotState.Unauthorized => "Token/Chat ID tidak valid",
            _ => loadedSettings is { Enabled: true } ? "Menghubungkan" : "Nonaktif"
        };

    public void Dispose() => botService.StateChanged -= OnBotStateChanged;

    void OnBotStateChanged(object? sender, EventArgs e)
    {
        if (stateContext is { } context && SynchronizationContext.Current != context)
            context.Post(_ => UpdateConnectionStatus(), null);
        else
            UpdateConnectionStatus();
    }
}
