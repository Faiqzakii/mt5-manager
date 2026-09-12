using System.ComponentModel;
using System.Windows;
using Mt5Manager.Wpf.ViewModels;

namespace Mt5Manager.Wpf.Views;

public partial class TelegramSettingsDialog : Window
{
    readonly TelegramSettingsViewModel viewModel;
    bool synchronizingToken;
    readonly CancellationTokenSource lifetime = new();

    public TelegramSettingsDialog(TelegramSettingsViewModel viewModel)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        DataContext = viewModel;
        viewModel.PropertyChanged += ViewModel_PropertyChanged;
        Loaded += async (_, _) => { viewModel.AttachToBotState(); await viewModel.LoadAsync(lifetime.Token); };
    }

    void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TelegramSettingsViewModel.BotToken) || synchronizingToken) return;
        synchronizingToken = true;
        TokenBox.Password = viewModel.BotToken;
        synchronizingToken = false;
    }

    void TokenBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (synchronizingToken) return;
        viewModel.BotToken = TokenBox.Password;
    }

    async void Test_Click(object sender, RoutedEventArgs e) => await viewModel.TestConnectionAsync(lifetime.Token);

    async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (await viewModel.SaveAsync(lifetime.Token)) DialogResult = true;
    }

    async void Remove_Click(object sender, RoutedEventArgs e)
    {
        var confirmed = MessageBox.Show(this,
            "Remove the saved Telegram bot configuration? The bot will be disabled.",
            "Remove Telegram configuration", MessageBoxButton.YesNo, MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;
        if (!confirmed) return;
        if (await viewModel.RemoveConfirmedAsync(lifetime.Token)) TokenBox.Clear();
    }

    void Window_Closing(object? sender, CancelEventArgs e)
    {
        lifetime.Cancel();
        viewModel.Dispose();
        viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        TokenBox.Clear();
        viewModel.ClearSecret();
        lifetime.Dispose();
    }
}
