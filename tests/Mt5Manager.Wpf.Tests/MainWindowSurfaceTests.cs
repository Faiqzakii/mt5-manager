using FluentAssertions;
using Mt5Manager.Application.Abstractions;
using Mt5Manager.Domain.Models;
using Mt5Manager.Wpf.ViewModels;

namespace Mt5Manager.Wpf.Tests;

public sealed class MainWindowSurfaceTests
{
    [Fact]
    public async Task Fleet_filters_preserve_hidden_batch_selection_and_snapshot()
    {
        var alpha = new TerminalRegistration(Guid.NewGuid(), "Alpha", @"C:\Alpha\terminal64.exe", @"C:\AlphaData", @"C:\Alpha", [], DiscoverySource.Manual, true);
        var beta = new TerminalRegistration(Guid.NewGuid(), "Beta", @"C:\Beta\terminal64.exe", @"C:\BetaData", @"C:\Beta", [], DiscoverySource.Manual, false);
        var vm = new MainViewModel(new Discovery([alpha, beta]), new Registry(), new Process());
        await vm.RefreshAsync();
        vm.Terminals[0].AccountSummary = "12345 — BrokerLive";
        vm.SearchText = "BrokerLive";
        vm.Terminals.Select(x => x.Terminal.Id).Should().Equal(alpha.Id);
        vm.Terminals[0].IsBatchSelected = true;
        var snapshot = vm.SnapshotSelection();
        vm.SearchText = "BetaData";
        vm.Terminals.Select(x => x.Terminal.Id).Should().Equal(beta.Id);
        vm.Terminals[0].IsBatchSelected = true;
        snapshot.Select(x => x.Terminal.Id).Should().Equal(alpha.Id);
        vm.SearchText = "does-not-exist";
        vm.Terminals.Should().BeEmpty();
        vm.TotalCount.Should().Be(2);
        vm.ClearSelectionCommand.Execute(null);
        vm.SnapshotSelection().Should().BeEmpty();
        vm.SearchText = "";
        vm.StateFilter = "Stopped";
        vm.Terminals.Should().BeEmpty();
        vm.StateFilter = "Running";
        vm.Terminals.Should().HaveCount(2);
    }

    [Fact]
    public async Task Batch_stop_skips_unverified_targets_and_reports_failures_without_starting_them()
    {
        var terminals = new[] { true, true, false }.Select((verified, index) => new TerminalRegistration(Guid.NewGuid(), $"Terminal {index}", $@"C:\T{index}\terminal64.exe", $@"C:\D{index}", $@"C:\T{index}", [], DiscoverySource.Manual, verified)).ToArray();
        var controller = new BatchProcess(terminals[1].Id);
        var vm = new MainViewModel(new Discovery(terminals), new Registry(), controller);
        await vm.RefreshAsync();
        foreach (var row in vm.Terminals) row.IsBatchSelected = true;
        await vm.RunBatchAsync("Stop", vm.SnapshotSelection());
        vm.BatchResults.Select(x => x.Outcome).Should().Equal("Succeeded", "Failed", "Skipped");
        controller.Stopped.Should().Equal(terminals[0].Id, terminals[1].Id);
        controller.Started.Should().BeEmpty();
        vm.IsBatchBusy.Should().BeFalse();
    }

    sealed class BatchProcess(Guid failureId) : ITerminalProcessController
    {
        public List<Guid> Stopped { get; } = [];
        public List<Guid> Started { get; } = [];
        public Task<TerminalRuntimeState> GetStateAsync(TerminalRegistration terminal, CancellationToken cancellationToken) => Task.FromResult(new TerminalRuntimeState(TerminalState.Running, 42, null));
        public Task<int> StartAsync(TerminalRegistration terminal, CancellationToken cancellationToken) { Started.Add(terminal.Id); return Task.FromResult(42); }
        public Task<StopResult> StopAsync(TerminalRegistration terminal, TimeSpan timeout, bool force, CancellationToken cancellationToken)
        {
            force.Should().BeFalse();
            Stopped.Add(terminal.Id);
            if (terminal.Id == failureId) throw new InvalidOperationException("Access denied");
            return Task.FromResult(new StopResult(StopOutcome.ExitedGracefully, null));
        }
    }

    sealed class Discovery(IReadOnlyList<TerminalRegistration> items) : ITerminalDiscovery
    {
        public Task<IReadOnlyList<TerminalRegistration>> DiscoverAsync(CancellationToken cancellationToken = default) => Task.FromResult(items);
    }

    sealed class Registry : ITerminalRegistry
    {
        public Task<IReadOnlyList<TerminalRegistration>> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TerminalRegistration>>([]);
        public Task SaveAsync(IReadOnlyList<TerminalRegistration> terminals, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    sealed class Process : ITerminalProcessController
    {
        public Task<TerminalRuntimeState> GetStateAsync(TerminalRegistration terminal, CancellationToken cancellationToken) => Task.FromResult(new TerminalRuntimeState(TerminalState.Running, 42, null));
        public Task<int> StartAsync(TerminalRegistration terminal, CancellationToken cancellationToken) => Task.FromResult(42);
        public Task<StopResult> StopAsync(TerminalRegistration terminal, TimeSpan timeout, bool force, CancellationToken cancellationToken) => Task.FromResult(new StopResult(StopOutcome.ExitedGracefully, null));
    }

}
