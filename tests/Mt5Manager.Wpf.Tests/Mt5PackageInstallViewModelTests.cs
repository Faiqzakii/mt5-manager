using FluentAssertions;
using Mt5Manager.Application.Abstractions;
using Mt5Manager.Domain.Models;
using Mt5Manager.Wpf.ViewModels;

namespace Mt5Manager.Wpf.Tests;

public sealed class Mt5PackageInstallViewModelTests
{
    [Fact] public void All_scope_uses_unfiltered_registration_list()
    {
        var first=Terminal("Alpha");var second=Terminal("Beta");
        var vm=new Mt5PackageInstallViewModel(new Installer(),[first,second],first){IsAllScope=true};
        vm.ResolveTargets().Should().Equal(first,second);
    }

    [Fact] public void Selected_scope_resolves_only_selected_terminal()
    {
        var first=Terminal("Alpha");var second=Terminal("Beta");
        var vm=new Mt5PackageInstallViewModel(new Installer(),[first,second],second);
        vm.ResolveTargets().Should().Equal(second);
    }

    [Fact] public async Task Unavailable_selected_scope_reports_actionable_error()
    {
        var vm=new Mt5PackageInstallViewModel(new Installer(),[],null){SourcePath="package.ex5",IsAllScope=false,IsSelectedScope=true};
        await vm.InstallAsync();
        vm.Error.Should().Be("The selected terminal is no longer available.");
    }

    [Fact] public async Task Install_projects_aggregate_and_per_terminal_results()
    {
        var first=Terminal("Alpha");var second=Terminal("Beta");
        var installer=new Installer{Result=new("package.ex5",Mt5PackageKind.Indicator,[new(first.Id,first.DisplayName,true,"Installed",@"C:\Alpha.ex5"),new(second.Id,second.DisplayName,false,"Rejected",null)])};
        var vm=new Mt5PackageInstallViewModel(installer,[first,second],first){SourcePath="package.ex5",IsAllScope=true,IsExpertAdvisor=false};
        await vm.InstallAsync();
        vm.Summary.Should().Be("1 of 2 terminal installations succeeded.");
        vm.Outcomes.Should().HaveCount(2);installer.Kind.Should().Be(Mt5PackageKind.Indicator);
    }

    [Fact] public async Task Busy_state_disables_install_until_operation_finishes()
    {
        var terminal=Terminal("Alpha");var installer=new Installer{Block=true};var vm=new Mt5PackageInstallViewModel(installer,[terminal],terminal){SourcePath="package.ex5"};
        var operation=vm.InstallAsync();await installer.Started.Task;
        vm.IsBusy.Should().BeTrue();vm.CanInstall.Should().BeFalse();
        installer.Release.SetResult();await operation;
        vm.IsBusy.Should().BeFalse();
    }

    static TerminalRegistration Terminal(string name)=>new(Guid.NewGuid(),name,@"C:\terminal64.exe",@"C:\Data",@"C:\",[],DiscoverySource.Manual,true);
    sealed class Installer:IMt5PackageInstaller
    {
        public Mt5PackageInstallResult? Result;public Mt5PackageKind Kind;public bool Block;public TaskCompletionSource Started=new(TaskCreationOptions.RunContinuationsAsynchronously);public TaskCompletionSource Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<Mt5PackageInstallResult> InstallAsync(string sourcePath,Mt5PackageKind kind,IReadOnlyList<TerminalRegistration> terminals,CancellationToken cancellationToken=default){Kind=kind;if(Block){Started.SetResult();await Release.Task;}return Result??new(sourcePath,kind,[..terminals.Select(x=>new Mt5PackageInstallOutcome(x.Id,x.DisplayName,true,"Installed",sourcePath))]);}
    }
}