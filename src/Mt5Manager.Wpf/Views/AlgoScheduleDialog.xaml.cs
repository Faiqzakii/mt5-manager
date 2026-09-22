using System.Windows;
using Mt5Manager.Wpf.ViewModels;

namespace Mt5Manager.Wpf.Views;

public partial class AlgoScheduleDialog : Window
{
    private readonly AlgoScheduleViewModel viewModel;
    private readonly CancellationTokenSource lifetime = new();

    public AlgoScheduleDialog(AlgoScheduleViewModel viewModel)
    {
        InitializeComponent();
        this.viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = viewModel;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try { await viewModel.LoadAsync(cancellationToken: lifetime.Token); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        lifetime.Cancel();
        lifetime.Dispose();
    }
    private void SelectAll_Click(object sender, RoutedEventArgs e) => viewModel.SetAllTerminals(true);

    private void ClearTerminals_Click(object sender, RoutedEventArgs e) => viewModel.SetAllTerminals(false);


    private void New_Click(object sender, RoutedEventArgs e) => viewModel.NewSchedule();

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try { await viewModel.SaveAsync(lifetime.Token); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (viewModel.SelectedSchedule is null) return;
        var answer = MessageBox.Show(
            this,
            $"Delete schedule '{viewModel.SelectedSchedule.Name}'? Future occurrences will stop immediately.",
            "Delete schedule",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        try { await viewModel.DeleteAsync(lifetime.Token); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        try { await viewModel.LoadAsync(cancellationToken: lifetime.Token); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }
}
