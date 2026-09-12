using CommunityToolkit.Mvvm.ComponentModel;
using Mt5Manager.Application.Telegram;

namespace Mt5Manager.Wpf.ViewModels;

public sealed partial class TelegramSettingsViewModel(
    ITelegramSettingsStore store,
    ISecretProtector protector,
    ITelegramBotApi api,
    ITelegramBotService botService) : ObservableObject
{
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

    public bool CanMutate => !IsBusy;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy) return;
        try
        {
            IsBusy = true;
            ValidationMessage = null;
            var settings = await store.LoadAsync(cancellationToken);
            if (settings is null)
            {
                ClearEditor();
                Status = "No Telegram bot configuration is saved.";
                return;
            }

            Enabled = settings.Enabled;
            AllowedChatId = settings.AllowedChatId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            BotToken = protector.Unprotect(settings.BotToken);
            Status = "Telegram bot configuration loaded. Token is hidden.";
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
        try
        {
            IsBusy = true;
            ValidationMessage = null;
            Status = "Testing Telegram connection…";
            var connection = await api.GetConnectionStateAsync(BotToken, cancellationToken);
            if (!connection.IsConnected)
            {
                Status = "Connection failed. Verify the bot token and try again.";
                return;
            }

            await api.SendMessageAsync(BotToken, chatId,
                new TelegramMessage("MT5 Manager connection test succeeded.", new TelegramKeyboard([])), cancellationToken);
            Status = connection.BotUsername is { Length: > 0 }
                ? $"connected as @{connection.BotUsername}; test message sent."
                : "Telegram connected; test message sent.";
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            Status = "Connection test failed. Verify the token, Chat ID, and network connection.";
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
            var settings = new TelegramSettings(protector.Protect(BotToken), chatId, 0, Enabled);
            await store.SaveAsync(settings, cancellationToken);
            await botService.ApplySettingsAsync(cancellationToken);
            Status = "Telegram bot configuration saved and applied.";
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
            await store.RemoveAsync(cancellationToken);
            await botService.ApplySettingsAsync(cancellationToken);
            ClearEditor();
            Status = "Telegram bot configuration removed.";
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
}
