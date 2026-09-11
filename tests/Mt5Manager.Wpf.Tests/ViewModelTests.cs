using FluentAssertions;
using Mt5Manager.Application.Abstractions;
using Mt5Manager.Application.Services;
using Mt5Manager.Domain.Models;
using Mt5Manager.Wpf.ViewModels;

namespace Mt5Manager.Wpf.Tests;

public sealed class ViewModelTests
{
    static TerminalRegistration T(string name="Alpha", bool verified=true) => new(Guid.NewGuid(),name,@"C:\terminal64.exe",@"C:\Data",@"C:\",[],DiscoverySource.Manual,verified);

    [Fact] public async Task Main_filters_and_scan_replaces_results()
    {
        var discovery=new Discovery([T("Alpha"),T("Beta")]); var vm=new MainViewModel(discovery,new Registry(),new Process());
        await vm.RefreshAsync(); vm.SearchText="bet";
        vm.Terminals.Should().ContainSingle(x=>x.DisplayName=="Beta"); discovery.Items=[T("Gamma")]; vm.SearchText=""; await vm.RefreshAsync();
        vm.Terminals.Should().ContainSingle(x=>x.DisplayName=="Gamma");
    }

    [Fact] public async Task Row_commands_recover_after_exception_and_track_state()
    {
        var process=new Process { State=new(TerminalState.Stopped,null,null), ThrowStart=true }; var row=new TerminalRowViewModel(T(),process);
        await row.RefreshStateAsync(); row.StartCommand.CanExecute(null).Should().BeTrue(); await row.StartAsync();
        row.IsBusy.Should().BeFalse(); row.Error.Should().NotBeNullOrWhiteSpace(); row.StartCommand.CanExecute(null).Should().BeTrue();
        process.ThrowStart=false; await row.StartAsync(); row.State.Should().Be(TerminalState.Running); row.StopCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact] public async Task Row_exposes_pid_sizes_restart_and_independent_cleanup_busy_state()
    {
        var process=new Process { State=new(TerminalState.Running,42,null) }; var row=new TerminalRowViewModel(T(),process,new Inspector());
        await row.RefreshStateAsync();
        row.ProcessId.Should().Be(42); row.StorageSummary.Should().Contain("Logs"); row.RestartCommand.CanExecute(null).Should().BeTrue(); row.CanCleanup.Should().BeTrue();
        await row.RestartAsync(); row.LastResult.Should().Contain("restarted");
    }

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

    sealed class Discovery(IReadOnlyList<TerminalRegistration> items):ITerminalDiscovery { public IReadOnlyList<TerminalRegistration> Items=items; public Task<IReadOnlyList<TerminalRegistration>> DiscoverAsync(CancellationToken c=default)=>Task.FromResult(Items); }
    sealed class Registry:ITerminalRegistry { public IReadOnlyList<TerminalRegistration> Items=[]; public Task<IReadOnlyList<TerminalRegistration>> LoadAsync(CancellationToken c=default)=>Task.FromResult(Items); public Task SaveAsync(IReadOnlyList<TerminalRegistration> t,CancellationToken c=default){Items=t;return Task.CompletedTask;} }
    sealed class Process:ITerminalProcessController { public TerminalRuntimeState State=new(TerminalState.Running,1,null); public bool ThrowStart; public bool Timeout; public StopOutcome? StopOutcome; public int StartCalls; public Task<TerminalRuntimeState> GetStateAsync(TerminalRegistration t,CancellationToken c)=>Task.FromResult(State); public Task<int> StartAsync(TerminalRegistration t,CancellationToken c){StartCalls++;if(ThrowStart)throw new InvalidOperationException("boom");State=new(TerminalState.Running,1,null);return Task.FromResult(1);} public Task<StopResult> StopAsync(TerminalRegistration t,TimeSpan x,bool force,CancellationToken c){var outcome=force?Mt5Manager.Application.Abstractions.StopOutcome.ForceTerminated:StopOutcome??(Timeout?Mt5Manager.Application.Abstractions.StopOutcome.TimedOut:Mt5Manager.Application.Abstractions.StopOutcome.ExitedGracefully);return Task.FromResult(new StopResult(outcome,outcome==Mt5Manager.Application.Abstractions.StopOutcome.Failed?"stop failed":null));} }
    sealed class Inspector:ITerminalStorageInspector { public Task<IReadOnlyList<CategoryUsage>> InspectAsync(TerminalRegistration t,CancellationToken c)=>Task.FromResult<IReadOnlyList<CategoryUsage>>([new(CleanupCategory.Logs,12,1200)]); }
    sealed class Cleaner:ITerminalCleanupService { public Task<IReadOnlyList<CleanupCategoryResult>> CleanAsync(TerminalRegistration t,IReadOnlySet<CleanupCategory> c,CancellationToken x)=>Task.FromResult<IReadOnlyList<CleanupCategoryResult>>([new(CleanupCategory.Logs,11,1100,[new("locked","denied")])]); }
    sealed class Audit:IAuditLogger { public Task AppendAsync(AuditRecord r,CancellationToken c=default)=>Task.CompletedTask; }
    static TerminalOperationCoordinator CoordinatorFor(TerminalRegistration t,bool timeout=true){var r=new Registry{Items=[t]};return new(r,new Process{Timeout=timeout},new Cleaner(),new Audit());}
}
