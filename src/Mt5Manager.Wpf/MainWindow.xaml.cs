using System.Windows;
using Mt5Manager.Application.Abstractions;
using Mt5Manager.Application.Services;
using Mt5Manager.Wpf.ViewModels;
using Mt5Manager.Wpf.Views;
namespace Mt5Manager.Wpf;
public partial class MainWindow:Window
{
 readonly MainViewModel viewModel; readonly ITerminalStorageInspector inspector; readonly TerminalOperationCoordinator coordinator;
 public MainWindow(MainViewModel viewModel,ITerminalStorageInspector inspector,TerminalOperationCoordinator coordinator){InitializeComponent();this.viewModel=viewModel;this.inspector=inspector;this.coordinator=coordinator;DataContext=viewModel;}
 async void Window_Loaded(object sender,RoutedEventArgs e)=>await viewModel.RefreshAsync();
 void Window_Closed(object? sender,EventArgs e)=>viewModel.CancelRefresh();
 async void Manual_Click(object sender,RoutedEventArgs e){var dialog=new ManualRegistrationDialog{Owner=this};if(dialog.ShowDialog()==true&&dialog.Registration is not null)await viewModel.AddManualAsync(dialog.Registration);}
 void Cleanup_Click(object sender,RoutedEventArgs e){if((sender as FrameworkElement)?.DataContext is TerminalRowViewModel row)new CleanupDialog(new CleanupViewModel(row.Terminal,inspector,coordinator)){Owner=this}.ShowDialog();}
}
