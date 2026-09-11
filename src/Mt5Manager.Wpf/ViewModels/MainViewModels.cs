using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mt5Manager.Application.Abstractions;
using Mt5Manager.Domain.Models;
namespace Mt5Manager.Wpf.ViewModels;
public sealed partial class MainViewModel(ITerminalDiscovery discovery,ITerminalRegistry registry,ITerminalProcessController process,ITerminalStorageInspector? inspector=null):ObservableObject
{
 readonly List<TerminalRowViewModel> all=[];CancellationTokenSource? refresh;
 [ObservableProperty]string searchText="";[ObservableProperty]bool isRefreshing;[ObservableProperty]string? error;
 public ObservableCollection<TerminalRowViewModel> Terminals{get;}=[];
 partial void OnSearchTextChanged(string value)=>ApplyFilter();
 [RelayCommand]public async Task RefreshAsync(){refresh?.Cancel();refresh?.Dispose();refresh=new();IsRefreshing=true;Error=null;try{var found=await discovery.DiscoverAsync(refresh.Token);var rows=found.Select(x=>new TerminalRowViewModel(x,process,inspector)).ToArray();await Task.WhenAll(rows.Select(x=>x.RefreshStateAsync(refresh.Token)));all.Clear();all.AddRange(rows);ApplyFilter();}catch(OperationCanceledException){}catch(Exception ex){Error=ex.Message;}finally{IsRefreshing=false;}}
 void ApplyFilter(){Terminals.Clear();foreach(var row in all.Where(x=>string.IsNullOrWhiteSpace(SearchText)||x.DisplayName.Contains(SearchText,StringComparison.OrdinalIgnoreCase)||x.ExecutablePath.Contains(SearchText,StringComparison.OrdinalIgnoreCase)))Terminals.Add(row);}
 public async Task AddManualAsync(TerminalRegistration terminal){var items=(await registry.LoadAsync()).Where(x=>x.Id!=terminal.Id).Append(terminal).ToArray();await registry.SaveAsync(items);await RefreshAsync();}public void CancelRefresh()=>refresh?.Cancel();
}
public sealed partial class TerminalRowViewModel(TerminalRegistration terminal,ITerminalProcessController process,ITerminalStorageInspector? inspector=null):ObservableObject
{
 public TerminalRegistration Terminal=>terminal;public string DisplayName=>terminal.DisplayName;public string ExecutablePath=>terminal.ExecutablePath;public string DataDirectory=>terminal.DataDirectory;
 [ObservableProperty][NotifyCanExecuteChangedFor(nameof(StartCommand),nameof(StopCommand),nameof(RestartCommand))]TerminalState state=TerminalState.Busy;
 [ObservableProperty][NotifyCanExecuteChangedFor(nameof(StartCommand),nameof(StopCommand),nameof(RestartCommand))][NotifyPropertyChangedFor(nameof(CanCleanup))]bool isBusy;
 [ObservableProperty]string? error;[ObservableProperty]int? processId;[ObservableProperty]string storageSummary="Storage not inspected";[ObservableProperty]string lastResult="No operations yet";
 public bool CanStart=>!IsBusy&&State is TerminalState.Stopped or TerminalState.Error;public bool CanStop=>!IsBusy&&State==TerminalState.Running;public bool CanRestart=>CanStop;public bool CanCleanup=>!IsBusy&&terminal.DataDirectoryVerified;
 [RelayCommand(CanExecute=nameof(CanStart))]public async Task StartAsync()=>await Run(async()=>{ProcessId=await process.StartAsync(terminal,CancellationToken.None);State=TerminalState.Running;LastResult="Terminal started.";});
 [RelayCommand(CanExecute=nameof(CanStop))]public async Task StopAsync()=>await Run(async()=>{var r=await process.StopAsync(terminal,TimeSpan.FromSeconds(15),false,CancellationToken.None);State=r.Outcome==StopOutcome.TimedOut?TerminalState.Running:TerminalState.Stopped;if(State==TerminalState.Stopped)ProcessId=null;LastResult=r.Outcome.ToString();if(r.Error is not null)Error=r.Error;});
 [RelayCommand(CanExecute=nameof(CanRestart))]public async Task RestartAsync()=>await Run(async()=>{var stopped=await process.StopAsync(terminal,TimeSpan.FromSeconds(15),false,CancellationToken.None);if(stopped.Outcome==StopOutcome.TimedOut)throw new InvalidOperationException("Terminal did not stop for restart.");ProcessId=await process.StartAsync(terminal,CancellationToken.None);State=TerminalState.Running;LastResult="Terminal restarted.";});
 [RelayCommand]public Task RefreshStateAsync()=>RefreshStateAsync(CancellationToken.None);
 public async Task RefreshStateAsync(CancellationToken token)=>await Run(async()=>{var s=await process.GetStateAsync(terminal,token);State=s.State;ProcessId=s.ProcessId;Error=s.Error;if(inspector is not null&&terminal.DataDirectoryVerified){var sizes=await inspector.InspectAsync(terminal,token);StorageSummary=string.Join(" · ",sizes.Select(x=>$"{x.Category}: {x.Bytes:N0} B"));}},token);
 async Task Run(Func<Task> operation,CancellationToken token=default){if(IsBusy)return;IsBusy=true;Error=null;try{await operation();}catch(OperationCanceledException)when(token.IsCancellationRequested){throw;}catch(Exception ex){Error=ex.Message;LastResult=$"Failed: {ex.Message}";}finally{IsBusy=false;}}
}
