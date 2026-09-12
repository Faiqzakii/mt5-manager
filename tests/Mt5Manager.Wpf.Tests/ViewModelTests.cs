using FluentAssertions;
using Mt5Manager.Application.Abstractions;
using Mt5Manager.Application.Services;
using Mt5Manager.Domain.Models;
using Mt5Manager.Wpf.ViewModels;

namespace Mt5Manager.Wpf.Tests;

public sealed class ViewModelTests
{

    [Fact] public async Task Main_refreshes_row_states_without_rescanning()
    {
        var discovery=new Discovery([T("Alpha")]); var process=new Process{State=new(TerminalState.Stopped,null,null)};
        var vm=new MainViewModel(discovery,new Registry(),process); await vm.RefreshAsync();
        vm.Terminals.Should().ContainSingle(); vm.Terminals[0].State.Should().Be(TerminalState.Stopped);
        discovery.Items=[]; process.State=new(TerminalState.Running,77,null); await vm.RefreshStatesAsync();
        vm.Terminals.Should().ContainSingle(); vm.Terminals[0].State.Should().Be(TerminalState.Running); vm.Terminals[0].ProcessId.Should().Be(77);
    }

    [Fact] public async Task State_refresh_leaves_storage_uninspected()
    {
        var row=new TerminalRowViewModel(T(),new Process());
        await row.RefreshStateAsync();
        row.StorageSummary.Should().Be("Storage not inspected");
    }

    [Fact] public async Task Explicit_storage_inspection_updates_storage_without_changing_last_operation()
    {
        var inspector=new Inspector(); var row=new TerminalRowViewModel(T(),new Process(),storage:inspector);
        await row.InspectStorageAsync();
        row.StorageSummary.Should().Be("Logs: 12 files, 1.17 KB");
        row.LastResult.Should().Be("No operations yet");
        await row.RefreshStateAsync();
        inspector.Calls.Should().Be(1,"automatic state refresh must not scan storage");
    }

    [Theory]
    [InlineData(1023,"1023 B")]
    [InlineData(1024,"1 KB")]
    [InlineData(1048576,"1 MB")]
    [InlineData(1073741824,"1 GB")]
    [InlineData(1228,"1.2 KB")]
    public void Storage_sizes_use_binary_units(long bytes,string expected) =>
        TerminalRowViewModel.FormatStorageSize(bytes).Should().Be(expected);

    [Fact] public async Task Cleanup_preview_does_not_change_the_originating_row()
    {
        var terminal=T(); var inspector=new Inspector(); var row=new TerminalRowViewModel(terminal,new Process(),storage:inspector);
        var cleanup=new CleanupViewModel(terminal,inspector,CoordinatorFor(terminal),result=>{row.LastResult=result;return Task.CompletedTask;},row.InspectStorageAsync);
        await cleanup.LoadPreviewAsync();
        row.StorageSummary.Should().Be("Storage not inspected");
        row.LastResult.Should().Be("No operations yet");
    }

    [Fact] public async Task Completed_cleanup_refreshes_row_storage_and_last_result()
    {
        var terminal=T(); var inspector=new Inspector(); var row=new TerminalRowViewModel(terminal,new Process(),storage:inspector);
        var cleanup=new CleanupViewModel(terminal,inspector,CoordinatorFor(terminal,false),result=>{row.LastResult=result;return Task.CompletedTask;},row.InspectStorageAsync);
        await cleanup.LoadPreviewAsync(); cleanup.Categories[0].IsSelected=true; cleanup.IsDestructiveConfirmed=true;
        await cleanup.PrepareAsync(); await cleanup.ContinueAsync(false);
        inspector.Calls.Should().Be(2,"preview and completed cleanup each inspect once");
        row.StorageSummary.Should().Be("Logs: 12 files, 1.17 KB");
        row.LastResult.Should().Be(cleanup.ResultText).And.Contain("Completed");
    }

    [Fact] public async Task Completed_cleanup_result_reaches_row_when_storage_reinspection_fails()
    {
        var terminal=T(); var inspector=new FailingSecondInspector(); var row=new TerminalRowViewModel(terminal,new Process(),storage:inspector);
        var cleanup=new CleanupViewModel(terminal,inspector,CoordinatorFor(terminal,false),result=>{row.LastResult=result;return Task.CompletedTask;},row.InspectStorageAsync);
        await cleanup.LoadPreviewAsync(); cleanup.Categories[0].IsSelected=true; cleanup.IsDestructiveConfirmed=true;
        await cleanup.PrepareAsync(); await cleanup.ContinueAsync(false);
        row.LastResult.Should().Contain("Completed");
        row.Error.Should().Be("inspection failed");
    }

    [Fact] public async Task Overlapping_state_refresh_is_skipped()
    {
        var process=new BlockingProcess(); var vm=new MainViewModel(new Discovery([T("Alpha"),T("Beta")]),new Registry(),process);
        var discoveryRefresh=vm.RefreshAsync(); await process.Started.Task; process.Release.SetResult(); await discoveryRefresh;
        process.Reset(); var first=vm.RefreshStatesAsync(); await process.Started.Task;
        await vm.RefreshStatesAsync();
        process.Calls.Should().Be(1,"the overlapping cycle must not advance to the second row");
        process.Release.SetResult(); await first;
    }

    [Fact] public async Task Discovery_refresh_retains_row_for_unchanged_registration()
    {
        var terminal=T() with { Arguments=["/portable"] }; var discovery=new Discovery([terminal]); var vm=new MainViewModel(discovery,new Registry(),new Process());
        await vm.RefreshAsync(); var original=vm.Terminals.Single();
        discovery.Items=[terminal with { Arguments=new[]{"/portable"} }]; await vm.RefreshAsync();
        vm.Terminals.Single().Should().BeSameAs(original);
    }

    [Fact] public async Task Discovery_refresh_replaces_row_for_changed_registration()
    {
        var terminal=T() with { Arguments=["/portable"] }; var discovery=new Discovery([terminal]); var vm=new MainViewModel(discovery,new Registry(),new Process());
        await vm.RefreshAsync(); var original=vm.Terminals.Single();
        discovery.Items=[terminal with { Arguments=["/portable","/skipupdate"] }]; await vm.RefreshAsync();
        vm.Terminals.Single().Should().NotBeSameAs(original); vm.Terminals.Single().Terminal.Arguments.Should().Equal("/portable","/skipupdate");
    }

    static TerminalRegistration T(string name="Alpha", bool verified=true) => new(Guid.NewGuid(),name,@"C:\terminal64.exe",@"C:\Data",@"C:\",[],DiscoverySource.Manual,verified);

    [Fact] public async Task Main_filters_and_scan_replaces_results()
    {
        var discovery=new Discovery([T("Alpha"),T("Beta")]); var vm=new MainViewModel(discovery,new Registry(),new Process());
        await vm.RefreshAsync(); vm.SearchText="bet";
        vm.Terminals.Should().ContainSingle(x=>x.DisplayName=="Beta"); discovery.Items=[T("Gamma")]; vm.SearchText=""; await vm.RefreshAsync();
        vm.Terminals.Should().ContainSingle(x=>x.DisplayName=="Gamma");
    }

    [Fact] public async Task Main_selection_survives_refresh_and_tracks_filtered_results()
    {
        var alpha=T("Alpha"); var beta=T("Beta");
        var discovery=new Discovery([alpha,beta]); var vm=new MainViewModel(discovery,new Registry(),new Process());
        await vm.RefreshAsync();
        vm.SelectedTerminal!.Terminal.Id.Should().Be(alpha.Id);

        vm.SelectedTerminal=vm.Terminals.Single(x=>x.Terminal.Id==beta.Id);
        discovery.Items=[alpha,beta]; await vm.RefreshAsync();
        vm.SelectedTerminal!.Terminal.Id.Should().Be(beta.Id);

        vm.SearchText="alp";
        vm.SelectedTerminal!.Terminal.Id.Should().Be(alpha.Id);

        vm.SearchText="missing";
        vm.SelectedTerminal.Should().BeNull();
    }

    [Fact] public async Task Row_commands_recover_after_exception_and_track_state()
    {
        var process=new Process { State=new(TerminalState.Stopped,null,null), ThrowStart=true }; var row=new TerminalRowViewModel(T(),process);
        await row.RefreshStateAsync(); row.StartCommand.CanExecute(null).Should().BeTrue(); await row.StartAsync();
        row.IsBusy.Should().BeFalse(); row.Error.Should().NotBeNullOrWhiteSpace(); row.StartCommand.CanExecute(null).Should().BeTrue();
        process.ThrowStart=false; await row.StartAsync(); row.State.Should().Be(TerminalState.Running); row.StopCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact] public async Task Row_exposes_pid_restart_and_independent_cleanup_busy_state()
    {
        var process=new Process { State=new(TerminalState.Running,42,null) }; var row=new TerminalRowViewModel(T(),process);
        await row.RefreshStateAsync();
        row.ProcessId.Should().Be(42); row.StorageSummary.Should().Be("Storage not inspected"); row.RestartCommand.CanExecute(null).Should().BeTrue(); row.CanCleanup.Should().BeTrue();
        await row.RestartAsync(); row.LastResult.Should().Contain("restarted");
    }

    [Fact] public async Task Row_projects_bridge_account_and_algo_permissions()
    {
        var process=new Process { State=new(TerminalState.Running,42,null) }; var runtime=new Runtime{Snapshot=NewSnapshot(AlgoTradingState.Enabled)};
        var row=new TerminalRowViewModel(T(),process,runtime,new Algo());
        await row.RefreshStateAsync();
        row.AccountSummary.Should().Be("12345 · Trader · Broker-Live · Real");
        row.AlgoSummary.Should().Be("Global Enabled · EA allowed · Account allowed · Expert allowed · connected");
        row.EnableAlgoCommand.CanExecute(null).Should().BeFalse("the requested global state is already active");
        row.DisableAlgoCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact] public async Task Row_hides_bridge_state_and_refuses_algo_command_without_snapshot()
    {
        var process=new Process { State=new(TerminalState.Running,42,null) }; var row=new TerminalRowViewModel(T(),process);
        await row.RefreshStateAsync();
        row.AccountSummary.Should().Be("Bridge unavailable"); row.AlgoSummary.Should().Be("Algo Trading unknown");
        row.EnableAlgoCommand.CanExecute(null).Should().BeFalse(); row.DisableAlgoCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact] public async Task Algo_command_uses_controller_and_refreshes_result()
    {
        var process=new Process { State=new(TerminalState.Running,42,null) }; var runtime=new Runtime{Snapshot=NewSnapshot(AlgoTradingState.Disabled)};
        var algo=new Algo{Result=new(true,"Algo Trading was enabled.",NewSnapshot(AlgoTradingState.Enabled))};
        var row=new TerminalRowViewModel(T(),process,runtime,algo);
        await row.RefreshStateAsync(); row.EnableAlgoCommand.CanExecute(null).Should().BeTrue();
        await row.EnableAlgoAsync();
        algo.Calls.Should().Be(1); algo.LastEnable.Should().BeTrue(); row.LastResult.Should().Be("Algo Trading was enabled."); row.AlgoSummary.Should().Contain("Global Enabled");
    }

    [Fact] public async Task Row_loads_newest_operation_history_and_records_commands()
    {
        var terminal=T(); var process=new Process{State=new(TerminalState.Stopped,null,null)}; var audit=new Audit();
        for(var index=0;index<8;index++) audit.Records.Add(OperationRecord(terminal,$"Older {index}",$"result {index}",index));
        var row=new TerminalRowViewModel(terminal,process,audit:audit);

        await row.LoadOperationHistoryAsync();
        row.OperationHistory.Should().HaveCount(8);
        row.OperationHistory[0].Operation.Should().Be("Older 7");

        await row.StartAsync();
        audit.Records.Should().Contain(x=>x.Operation=="Start"&&x.Outcome==AuditOutcome.Completed);
        row.OperationHistory[0].Operation.Should().Be("Start");
    }

    [Fact] public async Task Failed_row_operation_is_persisted_without_replacing_the_operation_error()
    {
        var terminal=T(); var process=new Process{State=new(TerminalState.Stopped,null,null),ThrowStart=true}; var audit=new Audit();
        var row=new TerminalRowViewModel(terminal,process,audit:audit);
        await row.StartAsync();
        audit.Records.Should().ContainSingle(x=>x.Operation=="Start"&&x.Outcome==AuditOutcome.Rejected);
        row.Error.Should().Be("boom");
    }

    static AuditRecord OperationRecord(TerminalRegistration terminal,string operation,string message,int minute=0)=>
        new(DateTimeOffset.UnixEpoch.AddMinutes(minute),terminal.Id,terminal.DisplayName,operation,[],false,ShutdownMethod.None,AuditOutcome.Completed,message,[],false,null);

    [Fact] public async Task Algo_command_surfaces_controller_failure()
    {
        var process=new Process { State=new(TerminalState.Running,42,null) }; var runtime=new Runtime{Snapshot=NewSnapshot(AlgoTradingState.Disabled)};
        var algo=new Algo{Result=new(false,"Several matching MetaTrader 5 windows were found.",null)};
        var row=new TerminalRowViewModel(T(),process,runtime,algo);
        await row.RefreshStateAsync(); await row.EnableAlgoAsync();
        row.Error.Should().Be("Several matching MetaTrader 5 windows were found."); row.LastResult.Should().Contain("Failed");
    }

    static TerminalAccountSnapshot NewSnapshot(AlgoTradingState state)=>new(1,DateTimeOffset.UtcNow,@"C:\Data",12345,"Trader","Broker-Live","Broker Ltd",AccountTradeMode.Real,true,state,true,true,true);

    [Fact] public async Task Cleanup_recovers_after_preparation_exception_and_requires_confirmation()
    {
        var terminal=T(); var vm=new CleanupViewModel(terminal,new Inspector(),CoordinatorFor(terminal)); vm.Categories[0].IsSelected=true; vm.IsDestructiveConfirmed=false;
        vm.CanPrepare.Should().BeFalse(); vm.IsDestructiveConfirmed=true; await vm.PrepareAsync();
        vm.IsBusy.Should().BeFalse(); vm.Error.Should().BeNull();
    }

    [Fact] public async Task Ready_cleanup_can_continue_without_force()
    {
        var terminal=T(); var vm=new CleanupViewModel(terminal,new Inspector(),CoordinatorFor(terminal,false)); vm.Categories[0].IsSelected=true; vm.IsDestructiveConfirmed=true;
        await vm.PrepareAsync(); vm.RequiresForceConfirmation.Should().BeFalse(); vm.CanContinue.Should().BeTrue(); await vm.ContinueAsync(false);
        vm.ResultText.Should().Contain("Completed");
    }

    [Fact] public async Task Continue_availability_notifies_and_disables_during_and_after_execution()
    {
        var terminal=T(); var cleaner=new BlockingCleaner(); var vm=new CleanupViewModel(terminal,new Inspector(),CoordinatorFor(terminal,false,cleaner)); vm.Categories[0].IsSelected=true; vm.IsDestructiveConfirmed=true;
        var changes=new List<string?>(); vm.PropertyChanged+=(_,e)=>changes.Add(e.PropertyName); await vm.PrepareAsync();
        vm.CanContinue.Should().BeTrue(); changes.Clear(); var continuing=vm.ContinueAsync(false); await cleaner.Started.Task;
        vm.CanContinue.Should().BeFalse(); changes.Should().Contain(nameof(vm.CanContinue)); cleaner.Release.SetResult(); await continuing;
        vm.CanContinue.Should().BeFalse();
    }

    [Fact] public async Task Failed_stop_refreshes_actual_state_and_restart_does_not_start()
    {
        var process=new Process{State=new(TerminalState.Running,7,null),StopOutcome=StopOutcome.Failed}; var row=new TerminalRowViewModel(T(),process);
        await row.RefreshStateAsync(); await row.StopAsync(); row.State.Should().Be(TerminalState.Running); row.Error.Should().NotBeNullOrWhiteSpace();
        await row.RestartAsync(); process.StartCalls.Should().Be(0); row.State.Should().Be(TerminalState.Running); row.Error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact] public async Task Cleanup_requires_verified_data_and_categories_then_displays_preview_force_and_partial_result()
    {
        var unverified=new CleanupViewModel(T(verified:false),new Inspector(),null!); unverified.CanPrepare.Should().BeFalse();
        var terminal=T(); var coordinator=CoordinatorFor(terminal); var vm=new CleanupViewModel(terminal,new Inspector(),coordinator);
        await vm.LoadPreviewAsync(); vm.Preview.Should().Contain("12 files"); vm.CanPrepare.Should().BeFalse(); vm.Categories[0].IsSelected=true; vm.IsDestructiveConfirmed=true; vm.CanPrepare.Should().BeTrue();
        await vm.PrepareAsync(); vm.RequiresForceConfirmation.Should().BeTrue(); await vm.ContinueAsync(true);
        vm.ResultText.Should().Contain("1 failed").And.Contain("Restarted");
    }

    [Fact] public void Manual_registration_validates_executable_and_data_paths()
    {
        var vm=new ManualRegistrationViewModel(_=>false,_=>false){DisplayName="Desk",ExecutablePath="bad",DataDirectory="bad"}; vm.IsValid.Should().BeFalse();
        vm=new ManualRegistrationViewModel(p=>p.EndsWith(".exe"),p=>p.EndsWith("Data")){DisplayName="Desk",ExecutablePath="terminal64.exe",DataDirectory="Data"};
        vm.IsValid.Should().BeTrue(); vm.CreateRegistration().DataDirectoryVerified.Should().BeTrue();
    }

    [Fact]
    public async Task Prepared_cleanup_blocks_a_second_preparation_until_it_is_finished()
    {
        var terminal=T(); var vm=new CleanupViewModel(terminal,new Inspector(),CoordinatorFor(terminal,false)); vm.Categories[0].IsSelected=true; vm.IsDestructiveConfirmed=true;
        await vm.PrepareAsync();
        vm.CanPrepare.Should().BeFalse("the terminal is reserved by the outstanding preparation");
        vm.CanForceContinue.Should().BeTrue();
        await vm.ContinueAsync(false);
        vm.CanPrepare.Should().BeTrue();
    }

    [Fact]
    public async Task Repeated_force_clicks_are_ignored_while_the_cleanup_is_running()
    {
        var terminal=T(); var cleaner=new BlockingCleaner(); var vm=new CleanupViewModel(terminal,new Inspector(),CoordinatorFor(terminal,true,cleaner));
        vm.Categories[0].IsSelected=true; vm.IsDestructiveConfirmed=true; await vm.PrepareAsync();
        vm.RequiresForceConfirmation.Should().BeTrue();
        var first=vm.ContinueAsync(true); await cleaner.Started.Task;
        await vm.ContinueAsync(true);
        vm.IsBusy.Should().BeTrue(); vm.Error.Should().BeNull();
        cleaner.Release.SetResult(); await first;
        cleaner.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Cancelled_dialog_neither_escapes_nor_keeps_the_reservation()
    {
        var terminal=T(); var coordinator=CoordinatorFor(terminal,false); var vm=new CleanupViewModel(terminal,new Inspector(),coordinator); vm.Categories[0].IsSelected=true; vm.IsDestructiveConfirmed=true;
        await vm.CancelPreparationAsync();
        await vm.PrepareAsync();
        vm.Error.Should().BeNull();
        (await coordinator.PrepareCleanupAsync(new(terminal.Id,new HashSet<CleanupCategory>{CleanupCategory.Logs}))).Status.Should().Be(CleanupPreparationStatus.Ready);
    }

    [Fact]
    public async Task Rejected_preparation_leaves_the_dialog_retryable()
    {
        var terminal=T(); var process=new Process{State=new(TerminalState.Error,null,"Access is denied.")};
        var coordinator=new TerminalOperationCoordinator(new Registry{Items=[terminal]},process,new Cleaner(),new Audit());
        var vm=new CleanupViewModel(terminal,new Inspector(),coordinator); vm.Categories[0].IsSelected=true; vm.IsDestructiveConfirmed=true;
        await vm.PrepareAsync();
        vm.Error.Should().Be("Access is denied.");
        vm.CanPrepare.Should().BeTrue("a rejected preparation holds nothing a retry could conflict with");
        vm.CanContinue.Should().BeFalse(); vm.CanForceContinue.Should().BeFalse();
        process.State=new(TerminalState.Stopped,null,null);
        await vm.PrepareAsync();
        vm.Error.Should().BeNull(); vm.CanContinue.Should().BeTrue();
    }

    [Fact]
    public async Task Manual_registration_surfaces_registry_write_failures()
    {
        var vm=new MainViewModel(new Discovery([]),new Registry{ThrowOnSave=true},new Process());
        await vm.AddManualAsync(T());
        vm.Error.Should().NotBeNullOrWhiteSpace();
        vm.Terminals.Should().BeEmpty();
    }

    sealed class Discovery(IReadOnlyList<TerminalRegistration> items):ITerminalDiscovery { public IReadOnlyList<TerminalRegistration> Items=items; public Task<IReadOnlyList<TerminalRegistration>> DiscoverAsync(CancellationToken c=default)=>Task.FromResult(Items); }
    sealed class Registry:ITerminalRegistry { public IReadOnlyList<TerminalRegistration> Items=[]; public bool ThrowOnSave; public Task<IReadOnlyList<TerminalRegistration>> LoadAsync(CancellationToken c=default)=>Task.FromResult(Items); public Task SaveAsync(IReadOnlyList<TerminalRegistration> t,CancellationToken c=default){if(ThrowOnSave)throw new IOException("The registry is locked.");Items=t;return Task.CompletedTask;} }
    sealed class Process:ITerminalProcessController { public TerminalRuntimeState State=new(TerminalState.Running,1,null); public bool ThrowStart; public bool Timeout; public StopOutcome? StopOutcome; public int StartCalls; public Task<TerminalRuntimeState> GetStateAsync(TerminalRegistration t,CancellationToken c)=>Task.FromResult(State); public Task<int> StartAsync(TerminalRegistration t,CancellationToken c){StartCalls++;if(ThrowStart)throw new InvalidOperationException("boom");State=new(TerminalState.Running,1,null);return Task.FromResult(1);} public Task<StopResult> StopAsync(TerminalRegistration t,TimeSpan x,bool force,CancellationToken c){var outcome=force?Mt5Manager.Application.Abstractions.StopOutcome.ForceTerminated:StopOutcome??(Timeout?Mt5Manager.Application.Abstractions.StopOutcome.TimedOut:Mt5Manager.Application.Abstractions.StopOutcome.ExitedGracefully);if(outcome is Mt5Manager.Application.Abstractions.StopOutcome.ExitedGracefully or Mt5Manager.Application.Abstractions.StopOutcome.AlreadyStopped or Mt5Manager.Application.Abstractions.StopOutcome.ForceTerminated)State=new(TerminalState.Stopped,null,null);return Task.FromResult(new StopResult(outcome,outcome==Mt5Manager.Application.Abstractions.StopOutcome.Failed?"stop failed":null));} }
    sealed class Inspector:ITerminalStorageInspector { public int Calls; public Task<IReadOnlyList<CategoryUsage>> InspectAsync(TerminalRegistration t,CancellationToken c){Calls++;return Task.FromResult<IReadOnlyList<CategoryUsage>>([new(CleanupCategory.Logs,12,1200)]);} }
    sealed class FailingSecondInspector:ITerminalStorageInspector { int calls; public Task<IReadOnlyList<CategoryUsage>> InspectAsync(TerminalRegistration t,CancellationToken c)=>++calls==1?Task.FromResult<IReadOnlyList<CategoryUsage>>([new(CleanupCategory.Logs,12,1200)]):Task.FromException<IReadOnlyList<CategoryUsage>>(new IOException("inspection failed")); }
    sealed class BlockingProcess:ITerminalProcessController { public TaskCompletionSource Started=new(TaskCreationOptions.RunContinuationsAsynchronously); public TaskCompletionSource Release=new(TaskCreationOptions.RunContinuationsAsynchronously); public int Calls; public void Reset(){Started=new(TaskCreationOptions.RunContinuationsAsynchronously);Release=new(TaskCreationOptions.RunContinuationsAsynchronously);Calls=0;} public async Task<TerminalRuntimeState> GetStateAsync(TerminalRegistration t,CancellationToken c){Calls++;Started.TrySetResult();await Release.Task;return new(TerminalState.Running,1,null);} public Task<int> StartAsync(TerminalRegistration t,CancellationToken c)=>Task.FromResult(1); public Task<StopResult> StopAsync(TerminalRegistration t,TimeSpan timeout,bool force,CancellationToken c)=>Task.FromResult(new StopResult(StopOutcome.AlreadyStopped,null)); }
    sealed class Cleaner:ITerminalCleanupService { public Task<IReadOnlyList<CleanupCategoryResult>> CleanAsync(TerminalRegistration t,IReadOnlySet<CleanupCategory> c,CancellationToken x)=>Task.FromResult<IReadOnlyList<CleanupCategoryResult>>([new(CleanupCategory.Logs,11,1100,[new("locked","denied")])]); }
    sealed class BlockingCleaner:ITerminalCleanupService { public TaskCompletionSource Started=new(TaskCreationOptions.RunContinuationsAsynchronously); public TaskCompletionSource Release=new(TaskCreationOptions.RunContinuationsAsynchronously); public int Calls; public async Task<IReadOnlyList<CleanupCategoryResult>> CleanAsync(TerminalRegistration t,IReadOnlySet<CleanupCategory> c,CancellationToken x){Calls++;Started.SetResult();await Release.Task;return [];} }
    sealed class Audit:IAuditLogger { public List<AuditRecord> Records=[]; public Task AppendAsync(AuditRecord r,CancellationToken c=default){Records.Add(r);return Task.CompletedTask;} public Task<IReadOnlyList<AuditRecord>> ReadAsync(Guid terminalId,int limit=100,CancellationToken c=default)=>Task.FromResult<IReadOnlyList<AuditRecord>>([..Records.Where(x=>x.TerminalId==terminalId).OrderByDescending(x=>x.Timestamp).Take(limit)]); }
    sealed class Runtime:ITerminalRuntimeInspector{public TerminalAccountSnapshot? Snapshot;public Task<TerminalAccountSnapshot?> ReadAsync(TerminalRegistration t,CancellationToken c=default)=>Task.FromResult(Snapshot);}
    sealed class Algo:ITerminalAlgoTradingController{public int Calls;public bool? LastEnable;public AlgoTradingControlResult Result=new(true,"ok",null);public Task<AlgoTradingControlResult> SetAsync(TerminalRegistration t,bool enable,CancellationToken c=default){Calls++;LastEnable=enable;Result=Result with{Snapshot=Result.Snapshot??null};return Task.FromResult(Result);}}
    static TerminalOperationCoordinator CoordinatorFor(TerminalRegistration t,bool timeout=true,ITerminalCleanupService? cleaner=null){var r=new Registry{Items=[t]};return new(r,new Process{Timeout=timeout},cleaner??new Cleaner(),new Audit());}
}
