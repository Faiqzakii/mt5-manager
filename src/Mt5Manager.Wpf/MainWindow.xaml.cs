using System.Windows;
using System.Windows.Threading;
using Mt5Manager.Application.Abstractions;
using Mt5Manager.Application.Services;
using Mt5Manager.Wpf.ViewModels;
using Mt5Manager.Wpf.Views;
namespace Mt5Manager.Wpf;
public partial class MainWindow:Window
{
 readonly MainViewModel viewModel; readonly ITerminalStorageInspector inspector; readonly TerminalOperationCoordinator coordinator;
 readonly DispatcherTimer backgroundRefresh=new(){Interval=TimeSpan.FromSeconds(15)}; readonly CancellationTokenSource lifetime=new();
 public MainWindow(MainViewModel viewModel,ITerminalStorageInspector inspector,TerminalOperationCoordinator coordinator){InitializeComponent();this.viewModel=viewModel;this.inspector=inspector;this.coordinator=coordinator;DataContext=viewModel;backgroundRefresh.Tick+=(_,_)=>_=viewModel.RefreshStatesAsync(lifetime.Token);}
 async void Window_Loaded(object sender,RoutedEventArgs e){await viewModel.RefreshAsync();backgroundRefresh.Start();}
 void Window_Closed(object? sender,EventArgs e){backgroundRefresh.Stop();lifetime.Cancel();viewModel.CancelRefresh();lifetime.Dispose();}
 async void Manual_Click(object sender,RoutedEventArgs e){var dialog=new ManualRegistrationDialog{Owner=this};if(dialog.ShowDialog()==true&&dialog.Registration is not null)await viewModel.AddManualAsync(dialog.Registration);}
 void Cleanup_Click(object sender,RoutedEventArgs e){if((sender as FrameworkElement)?.DataContext is TerminalRowViewModel row)new CleanupDialog(new CleanupViewModel(row.Terminal,inspector,coordinator,row.ApplyCleanupResultAsync,row.RunStorageInspectionAsync)){Owner=this}.ShowDialog();}
}
