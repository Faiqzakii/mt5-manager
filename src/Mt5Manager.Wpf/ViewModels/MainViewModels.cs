using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mt5Manager.Application.Abstractions;
using Mt5Manager.Domain.Models;
namespace Mt5Manager.Wpf.ViewModels;
public sealed partial class MainViewModel(ITerminalDiscovery discovery,ITerminalRegistry registry,ITerminalProcessController process,ITerminalStorageInspector? inspector=null,ITerminalRuntimeInspector? runtime=null,ITerminalAlgoTradingController? algo=null):ObservableObject
{
 readonly List<TerminalRowViewModel> all=[];CancellationTokenSource? refresh;
 [ObservableProperty]string searchText="";[ObservableProperty]bool isRefreshing;[ObservableProperty]string? error;
 public ObservableCollection<TerminalRowViewModel> Terminals{get;}=[];
 partial void OnSearchTextChanged(string value)=>ApplyFilter();
    [RelayCommand]public async Task RefreshAsync(){refresh?.Cancel();refresh?.Dispose();refresh=new();IsRefreshing=true;Error=null;try{var found=await discovery.DiscoverAsync(refresh.Token);var rows=found.Select(x=>new TerminalRowViewModel(x,process,inspector,runtime,algo)).ToArray();await Task.WhenAll(rows.Select(x=>x.RefreshStateAsync(refresh.Token)));all.Clear();all.AddRange(rows);ApplyFilter();}catch(OperationCanceledException){}catch(Exception ex){Error=ex.Message;}finally{IsRefreshing=false;}}
 void ApplyFilter(){Terminals.Clear();foreach(var row in all.Where(x=>string.IsNullOrWhiteSpace(SearchText)||x.DisplayName.Contains(SearchText,StringComparison.OrdinalIgnoreCase)||x.ExecutablePath.Contains(SearchText,StringComparison.OrdinalIgnoreCase)))Terminals.Add(row);}
 public async Task AddManualAsync(TerminalRegistration terminal){try{var items=(await registry.LoadAsync()).Where(x=>x.Id!=terminal.Id).Append(terminal).ToArray();await registry.SaveAsync(items);}catch(Exception ex){Error=$"The terminal could not be registered: {ex.Message}";return;}await RefreshAsync();}public void CancelRefresh()=>refresh?.Cancel();
 public async Task RefreshStatesAsync(CancellationToken token=default){foreach(var row in all.ToArray()){if(token.IsCancellationRequested)return;try{await row.RefreshStateAsync(token);}catch(OperationCanceledException)when(token.IsCancellationRequested){return;}}}
}
public sealed partial class TerminalRowViewModel(TerminalRegistration terminal,ITerminalProcessController process,ITerminalStorageInspector? inspector=null,ITerminalRuntimeInspector? runtime=null,ITerminalAlgoTradingController? algo=null):ObservableObject
{
    public TerminalRegistration Terminal=>terminal;public string DisplayName=>terminal.DisplayName;public string ExecutablePath=>terminal.ExecutablePath;public string DataDirectory=>terminal.DataDirectory;
    [ObservableProperty][NotifyCanExecuteChangedFor(nameof(StartCommand),nameof(StopCommand),nameof(RestartCommand),nameof(EnableAlgoCommand),nameof(DisableAlgoCommand))]TerminalState state=TerminalState.Busy;
    [ObservableProperty][NotifyCanExecuteChangedFor(nameof(StartCommand),nameof(StopCommand),nameof(RestartCommand),nameof(EnableAlgoCommand),nameof(DisableAlgoCommand))][NotifyPropertyChangedFor(nameof(CanCleanup),nameof(CanEnableAlgo),nameof(CanDisableAlgo))]bool isBusy;
    [ObservableProperty][NotifyCanExecuteChangedFor(nameof(EnableAlgoCommand),nameof(DisableAlgoCommand))]TerminalAccountSnapshot? account;
    [ObservableProperty]string? error;[ObservableProperty]int? processId;[ObservableProperty]string storageSummary="Storage not inspected";[ObservableProperty]string lastResult="No operations yet";
    [ObservableProperty]string accountSummary="Bridge unavailable";[ObservableProperty]string algoSummary="Algo Trading unknown";
    public bool CanStart=>!IsBusy&&State is TerminalState.Stopped or TerminalState.Error;public bool CanStop=>!IsBusy&&State==TerminalState.Running;public bool CanRestart=>CanStop;public bool CanCleanup=>!IsBusy&&terminal.DataDirectoryVerified;
    public bool CanEnableAlgo=>CanControlAlgo&&Account?.GlobalAlgoTrading is AlgoTradingState.Disabled;
    public bool CanDisableAlgo=>CanControlAlgo&&Account?.GlobalAlgoTrading is AlgoTradingState.Enabled;
    bool CanControlAlgo=>!IsBusy&&State==TerminalState.Running&&runtime is not null&&algo is not null&&terminal.DataDirectoryVerified&&Account is not null&&Account.GlobalAlgoTrading!=AlgoTradingState.Unknown;
    [RelayCommand(CanExecute=nameof(CanStart))]public async Task StartAsync()=>await Run(async()=>{ProcessId=await process.StartAsync(terminal,CancellationToken.None);State=TerminalState.Running;LastResult="Terminal started.";});
    [RelayCommand(CanExecute=nameof(CanStop))]public async Task StopAsync()=>await Run(async()=>{var r=await process.StopAsync(terminal,TimeSpan.FromSeconds(15),false,CancellationToken.None);if(r.Outcome is StopOutcome.ExitedGracefully or StopOutcome.AlreadyStopped or StopOutcome.ForceTerminated){State=TerminalState.Stopped;ProcessId=null;LastResult=r.Outcome.ToString();return;}await RefreshActualStateAsync();throw new InvalidOperationException(r.Error??$"Terminal stop {r.Outcome.ToString().ToLowerInvariant()}.");});
    [RelayCommand(CanExecute=nameof(CanRestart))]public async Task RestartAsync()=>await Run(async()=>{var stopped=await process.StopAsync(terminal,TimeSpan.FromSeconds(15),false,CancellationToken.None);if(stopped.Outcome is not (StopOutcome.ExitedGracefully or StopOutcome.AlreadyStopped or StopOutcome.ForceTerminated)){await RefreshActualStateAsync();throw new InvalidOperationException(stopped.Error??$"Terminal stop {stopped.Outcome.ToString().ToLowerInvariant()}; restart canceled.");}ProcessId=await process.StartAsync(terminal,CancellationToken.None);State=TerminalState.Running;LastResult="Terminal restarted.";});
    [RelayCommand(CanExecute=nameof(CanEnableAlgo))]public async Task EnableAlgoAsync()=>await SetAlgoAsync(true);
    [RelayCommand(CanExecute=nameof(CanDisableAlgo))]public async Task DisableAlgoAsync()=>await SetAlgoAsync(false);
    [RelayCommand]public Task RefreshStateAsync()=>RefreshStateAsync(CancellationToken.None);
    public async Task RefreshStateAsync(CancellationToken token)=>await Run(async()=>{var s=await process.GetStateAsync(terminal,token);State=s.State;ProcessId=s.ProcessId;Error=s.Error;if(runtime is not null){Account=await runtime.ReadAsync(terminal,token);ProjectRuntimeState();}if(inspector is not null&&terminal.DataDirectoryVerified){var sizes=await inspector.InspectAsync(terminal,token);StorageSummary=string.Join(" · ",sizes.Select(x=>$"{x.Category}: {x.Bytes:N0} B"));};},token);
    async Task SetAlgoAsync(bool enable)=>await Run(async()=>{var result=await algo!.SetAsync(terminal,enable,CancellationToken.None);if(result.Snapshot is not null){Account=result.Snapshot;ProjectRuntimeState();}if(!result.Success)throw new InvalidOperationException(result.Message);LastResult=result.Message;});
    void ProjectRuntimeState(){var a=Account;AccountSummary=a is null?"Bridge unavailable":$"{a.Login} · {a.AccountName} · {a.Server} · {a.TradeMode}";AlgoSummary=a is null?"Algo Trading unknown":$"Global {a.GlobalAlgoTrading} · EA {(a.EaTradingAllowed?"allowed":"denied")} · Account {(a.AccountTradingAllowed?"allowed":"denied")} · Expert {(a.AccountExpertAllowed?"allowed":"denied")} · {(a.Connected?"connected":"disconnected")}";}
    async Task RefreshActualStateAsync(){var current=await process.GetStateAsync(terminal,CancellationToken.None);State=current.State;ProcessId=current.ProcessId;}
    async Task Run(Func<Task> operation,CancellationToken token=default){if(IsBusy)return;IsBusy=true;Error=null;try{await operation();}catch(OperationCanceledException)when(token.IsCancellationRequested){throw;}catch(Exception ex){Error=ex.Message;LastResult=$"Failed: {ex.Message}";}finally{IsBusy=false;}}
}
