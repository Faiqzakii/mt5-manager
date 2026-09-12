using System.Windows;
using System.Windows.Threading;
using Mt5Manager.Application.Abstractions;
using Mt5Manager.Application.Services;
using Mt5Manager.Wpf.ViewModels;
using Mt5Manager.Wpf.Views;
namespace Mt5Manager.Wpf;
public partial class MainWindow:Window
{
 readonly MainViewModel viewModel; readonly ITerminalStorageInspector inspector; readonly TerminalOperationCoordinator coordinator; readonly Func<TelegramSettingsDialog>? createTelegramDialog; readonly TelegramApplicationLifetime? applicationLifetime;
 readonly DispatcherTimer backgroundRefresh=new(){Interval=TimeSpan.FromSeconds(15)}; readonly CancellationTokenSource lifetime=new();
 public MainWindow(MainViewModel viewModel,ITerminalStorageInspector inspector,TerminalOperationCoordinator coordinator,Func<TelegramSettingsDialog>? createTelegramDialog=null,TelegramApplicationLifetime? applicationLifetime=null){InitializeComponent();this.viewModel=viewModel;this.inspector=inspector;this.coordinator=coordinator;this.createTelegramDialog=createTelegramDialog;this.applicationLifetime=applicationLifetime;DataContext=viewModel;backgroundRefresh.Tick+=(_,_)=>_=viewModel.RefreshStatesAsync(lifetime.Token);}
 async void Window_Loaded(object sender,RoutedEventArgs e){try{await viewModel.RefreshAsync();backgroundRefresh.Start();}catch(Exception exception){viewModel.Error=exception.Message;backgroundRefresh.Start();}}
 void Window_Closed(object? sender,EventArgs e){backgroundRefresh.Stop();lifetime.Cancel();applicationLifetime?.CancelOperations();viewModel.CancelRefresh();}
 async void Manual_Click(object sender,RoutedEventArgs e){var dialog=new ManualRegistrationDialog{Owner=this};if(dialog.ShowDialog()==true&&dialog.Registration is not null)await viewModel.AddManualAsync(dialog.Registration);}
 void Telegram_Click(object sender,RoutedEventArgs e)
 {
  if(createTelegramDialog is null){MessageBox.Show(this,"Telegram services are not available.","Telegram Bot",MessageBoxButton.OK,MessageBoxImage.Information);return;}
  var dialog=createTelegramDialog();dialog.Owner=this;dialog.ShowDialog();
 }
 void Cleanup_Click(object sender,RoutedEventArgs e){if((sender as FrameworkElement)?.DataContext is TerminalRowViewModel row)new CleanupDialog(new CleanupViewModel(row.Terminal,inspector,coordinator,row.ApplyCleanupResultAsync,row.RunStorageInspectionAsync)){Owner=this}.ShowDialog();}
}
