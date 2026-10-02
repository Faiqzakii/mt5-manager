using System.Windows;
using System.Windows.Threading;
using Mt5Manager.Application.Abstractions;
using Mt5Manager.Application.Services;
using Mt5Manager.Wpf.ViewModels;
using Mt5Manager.Wpf.Views;
namespace Mt5Manager.Wpf;
public partial class MainWindow:Window
{
 readonly MainViewModel viewModel; readonly ITerminalStorageInspector inspector; readonly TerminalOperationCoordinator coordinator; readonly Func<TelegramSettingsDialog>? createTelegramDialog; readonly Func<IReadOnlyList<Mt5Manager.Domain.Models.TerminalRegistration>,Mt5Manager.Domain.Models.TerminalRegistration?,Mt5PackageInstallDialog>? createPackageDialog; readonly Func<AlgoScheduleDialog>? createScheduleDialog; readonly TelegramApplicationLifetime? applicationLifetime;
 readonly DispatcherTimer backgroundRefresh=new(){Interval=TimeSpan.FromSeconds(15)}; readonly CancellationTokenSource lifetime=new();
 public MainWindow(MainViewModel viewModel,ITerminalStorageInspector inspector,TerminalOperationCoordinator coordinator,Func<TelegramSettingsDialog>? createTelegramDialog=null,TelegramApplicationLifetime? applicationLifetime=null,Func<IReadOnlyList<Mt5Manager.Domain.Models.TerminalRegistration>,Mt5Manager.Domain.Models.TerminalRegistration?,Mt5PackageInstallDialog>? createPackageDialog=null,Func<AlgoScheduleDialog>? createScheduleDialog=null){InitializeComponent();this.viewModel=viewModel;this.inspector=inspector;this.coordinator=coordinator;this.createTelegramDialog=createTelegramDialog;this.applicationLifetime=applicationLifetime;this.createPackageDialog=createPackageDialog;this.createScheduleDialog=createScheduleDialog;DataContext=viewModel;backgroundRefresh.Tick+=(_,_)=>_=viewModel.RefreshStatesAsync(lifetime.Token);}
 async void Window_Loaded(object sender,RoutedEventArgs e){try{await viewModel.RefreshAsync();backgroundRefresh.Start();}catch(Exception exception){viewModel.Error=exception.Message;backgroundRefresh.Start();}}
 void Window_Closed(object? sender,EventArgs e){backgroundRefresh.Stop();lifetime.Cancel();applicationLifetime?.CancelOperations();viewModel.CancelRefresh();}
 async void Manual_Click(object sender,RoutedEventArgs e){var dialog=new ManualRegistrationDialog{Owner=this};if(dialog.ShowDialog()==true&&dialog.Registration is not null)await viewModel.AddManualAsync(dialog.Registration);}
 void Telegram_Click(object sender,RoutedEventArgs e)
 {
  if(createTelegramDialog is null){MessageBox.Show(this,"Telegram services are not available.","Telegram Bot",MessageBoxButton.OK,MessageBoxImage.Information);return;}
  var dialog=createTelegramDialog();dialog.Owner=this;dialog.ShowDialog();
 }
 void Scheduler_Click(object sender,RoutedEventArgs e)
 {
  if(createScheduleDialog is null){MessageBox.Show(this,"Algo scheduler is not available.","Algo Scheduler",MessageBoxButton.OK,MessageBoxImage.Information);return;}
  var dialog=createScheduleDialog();dialog.Owner=this;dialog.ShowDialog();
 }
 void PackageInstall_Click(object sender,RoutedEventArgs e)
 {
  if(createPackageDialog is null){MessageBox.Show(this,"Package installation is not available.","Install EA / Indicator",MessageBoxButton.OK,MessageBoxImage.Information);return;}
  var dialog=createPackageDialog(viewModel.AllRegistrations,viewModel.SelectedTerminal?.Terminal);dialog.Owner=this;dialog.ShowDialog();
 }
 void GettingStarted_Click(object sender,RoutedEventArgs e){if(FindName("GettingStartedTab") is System.Windows.Controls.TabItem tab){tab.IsSelected=true;tab.Focus();}}
 void Cleanup_Click(object sender,RoutedEventArgs e){if((sender as FrameworkElement)?.DataContext is TerminalRowViewModel row)new CleanupDialog(new CleanupViewModel(row.Terminal,inspector,coordinator,row.ApplyCleanupResultAsync,row.RunStorageInspectionAsync)){Owner=this}.ShowDialog();}
 async void Batch_Click(object sender,RoutedEventArgs e){if(viewModel.IsBatchBusy||viewModel.IsRefreshing)return;if((sender as FrameworkElement)?.Tag is not string operation)return;var targets=viewModel.SnapshotSelection();if(targets.Length==0){MessageBox.Show(this,"Select at least one terminal first.","No terminals selected",MessageBoxButton.OK,MessageBoxImage.Information);return;}var interrupting=operation is "Stop" or "Restart" or "Update";if(interrupting){var names=string.Join(Environment.NewLine,targets.Select(x=>$"• {x.DisplayName} ({x.ExecutablePath})"));var impact=operation=="Update"?"Only running verified targets will be updated, one at a time. EAs will be down during graceful shutdown/restart. Defaults: stop timeout 90 seconds (never force), settle 90 seconds, gap 30 seconds, maximum 3 passes on update activity/version change. This does not prove the latest version or no pending update.":"Running sessions will be interrupted; expert advisors may stop receiving ticks and may not finish their current work.";if(MessageBox.Show(this,$"{operation} these terminals?\n\n{names}\n\n{impact}","Confirm session operation",MessageBoxButton.OKCancel,MessageBoxImage.Warning)!=MessageBoxResult.OK)return;}if(operation=="Update")await viewModel.RunUpdateAsync(targets,lifetime.Token);else await viewModel.RunBatchAsync(operation,targets);}
}
