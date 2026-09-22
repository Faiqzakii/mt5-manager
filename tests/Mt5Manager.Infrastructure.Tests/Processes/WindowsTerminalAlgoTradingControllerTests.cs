using FluentAssertions;
using Mt5Manager.Application.Abstractions;
using Mt5Manager.Domain.Models;
using Mt5Manager.Infrastructure.Processes;

namespace Mt5Manager.Infrastructure.Tests.Processes;

public sealed class WindowsTerminalAlgoTradingControllerTests
{
    private readonly TerminalRegistration terminal = new(Guid.NewGuid(), "Broker", @"C:\Apps\Broker\terminal64.exe", @"C:\Data", @"C:\Apps\Broker", [], DiscoverySource.Manual, true);

    [Fact]
    public async Task Set_is_no_op_when_desired_state_is_already_current()
    {
        var commands = new FakeCommands();
        var inspector = new FakeInspector(Snapshot(AlgoTradingState.Enabled));

        var result = await Controller(inspector, commands).SetAsync(terminal, true);

        result.Success.Should().BeTrue();
        result.Message.Should().Be("Algo Trading is already enabled.");
        commands.Sent.Should().BeEmpty("an unchanged terminal state must not toggle the command");
    }

    [Fact]
    public async Task Set_refuses_when_no_unique_terminal_window_exists()
    {
        var commands = new FakeCommands();
        var inspector = new FakeInspector(Snapshot(AlgoTradingState.Disabled));

        var result = await Controller(inspector, commands, _ => []).SetAsync(terminal, true);

        result.Success.Should().BeFalse();
        result.Message.Should().Be("No matching MetaTrader 5 window was found.");
        commands.Sent.Should().BeEmpty();
    }
    [Fact]
    public async Task Set_ignores_owned_popup_and_posts_to_the_terminal_frame()
    {
        var commands = new FakeCommands();
        var inspector = new FakeInspector(Snapshot(AlgoTradingState.Enabled), Snapshot(AlgoTradingState.Disabled));

        var result = await Controller(
            inspector,
            commands,
            _ => [4242, 4343],
            ownerFinder: window => window == 4343 ? 4242 : nint.Zero).SetAsync(terminal, false);

        result.Success.Should().BeTrue();
        commands.Sent.Should().Equal([(4242, WindowsTerminalAlgoTradingController.AlgoTradingCommandId)]);
    }

    [Fact]
    public async Task Set_refuses_when_multiple_ownerless_terminal_frames_exist()
    {
        var commands = new FakeCommands();
        var inspector = new FakeInspector(Snapshot(AlgoTradingState.Enabled));

        var result = await Controller(
            inspector,
            commands,
            _ => [4242, 4343],
            ownerFinder: _ => nint.Zero).SetAsync(terminal, false);

        result.Success.Should().BeFalse();
        result.Message.Should().Be("Several matching MetaTrader 5 windows were found.");
        commands.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Set_posts_the_algo_trading_command_and_confirms_the_new_state()
    {
        var commands = new FakeCommands();
        var inspector = new FakeInspector(Snapshot(AlgoTradingState.Disabled), Snapshot(AlgoTradingState.Enabled));

        var result = await Controller(inspector, commands).SetAsync(terminal, true);

        result.Success.Should().BeTrue();
        result.Message.Should().Be("Algo Trading was enabled.");
        result.Snapshot!.GlobalAlgoTrading.Should().Be(AlgoTradingState.Enabled);
        commands.Sent.Should().Equal([(4242, WindowsTerminalAlgoTradingController.AlgoTradingCommandId)]);
    }

    [Fact]
    public async Task Set_posts_the_command_exactly_once_because_the_command_toggles()
    {
        var commands = new FakeCommands();
        var inspector = new FakeInspector(
            Snapshot(AlgoTradingState.Disabled),
            Snapshot(AlgoTradingState.Disabled),
            Snapshot(AlgoTradingState.Disabled),
            Snapshot(AlgoTradingState.Enabled));

        var result = await Controller(inspector, commands, pollInterval: TimeSpan.FromMilliseconds(1)).SetAsync(terminal, true);

        result.Success.Should().BeTrue();
        commands.Sent.Should().HaveCount(1, "a second post would toggle the state straight back");
    }

    [Fact]
    public async Task Set_fails_without_retrying_when_the_command_cannot_be_posted()
    {
        var commands = new FakeCommands { PostResult = false };
        var inspector = new FakeInspector(Snapshot(AlgoTradingState.Disabled), Snapshot(AlgoTradingState.Enabled));

        var result = await Controller(inspector, commands).SetAsync(terminal, true);

        result.Success.Should().BeFalse();
        result.Message.Should().Be("The Algo Trading command could not be posted to the MetaTrader 5 window.");
        result.Snapshot!.GlobalAlgoTrading.Should().Be(AlgoTradingState.Disabled);
        inspector.Reads.Should().Be(1, "an undelivered command must not start confirmation polling");
    }

    [Fact]
    public async Task Set_fails_when_the_observed_state_does_not_change()
    {
        var commands = new FakeCommands();
        var inspector = new FakeInspector(Snapshot(AlgoTradingState.Disabled), Snapshot(AlgoTradingState.Disabled), Snapshot(AlgoTradingState.Disabled));

        var result = await Controller(inspector, commands, pollInterval: TimeSpan.Zero, timeout: TimeSpan.Zero).SetAsync(terminal, true);

        result.Success.Should().BeFalse();
        result.Message.Should().Be("Algo Trading did not become enabled.");
        commands.Sent.Should().HaveCount(1);
    }

    [Fact]
    public async Task Set_fails_when_the_terminal_is_not_running()
    {
        var commands = new FakeCommands();
        var inspector = new FakeInspector(Snapshot(AlgoTradingState.Disabled));

        var result = await Controller(inspector, commands, state: new TerminalRuntimeState(TerminalState.Stopped, null, null)).SetAsync(terminal, true);

        result.Success.Should().BeFalse();
        result.Message.Should().Be("The terminal must be running before Algo Trading can be changed.");
        commands.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Set_refuses_when_runtime_snapshot_is_unavailable_or_unknown()
    {
        var commands = new FakeCommands();

        var unknown = await Controller(new FakeInspector(Snapshot(AlgoTradingState.Unknown)), commands).SetAsync(terminal, true);
        unknown.Success.Should().BeFalse();
        unknown.Message.Should().Be("The current Algo Trading state is unknown.");

        var missing = await Controller(new FakeInspector((TerminalAccountSnapshot?)null), commands).SetAsync(terminal, true);
        missing.Success.Should().BeFalse();
        missing.Message.Should().Be("The MT5 Manager bridge snapshot is unavailable.");

        commands.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Set_disables_the_terminal_when_disable_is_requested()
    {
        var commands = new FakeCommands();
        var inspector = new FakeInspector(Snapshot(AlgoTradingState.Enabled), Snapshot(AlgoTradingState.Disabled));

        var result = await Controller(inspector, commands).SetAsync(terminal, false);

        result.Success.Should().BeTrue();
        result.Message.Should().Be("Algo Trading was disabled.");
        commands.Sent.Should().Equal([(4242, WindowsTerminalAlgoTradingController.AlgoTradingCommandId)]);
    }

    private static WindowsTerminalAlgoTradingController Controller(
        FakeInspector inspector,
        FakeCommands commands,
        Func<int, IReadOnlyList<nint>>? windowFinder = null,
        TerminalRuntimeState? state = null,
        TimeSpan? pollInterval = null,
        TimeSpan? timeout = null,
        Func<nint, nint>? ownerFinder = null) =>
        new(
            inspector,
            new FakeProcess(state ?? new TerminalRuntimeState(TerminalState.Running, 42, null)),
            windowFinder ?? (_ => [4242]),
            commands,
            () => DateTimeOffset.UnixEpoch,
            pollInterval,
            timeout,
            ownerFinder);

    private static TerminalAccountSnapshot Snapshot(AlgoTradingState state) => new(1, DateTimeOffset.UtcNow, @"C:\Data", 123, "Trader", "Server", "Company", AccountTradeMode.Real, true, state, true, true, true);

    private sealed class FakeInspector(params Func<TerminalAccountSnapshot?>[] snapshots) : ITerminalRuntimeInspector
    {
        private int call;

        public FakeInspector(params TerminalAccountSnapshot?[] snapshots)
            : this(snapshots.Select<TerminalAccountSnapshot?, Func<TerminalAccountSnapshot?>>(snapshot => () => snapshot).ToArray()) { }

        public int Reads => call;

        public Task<TerminalAccountSnapshot?> ReadAsync(TerminalRegistration terminal, CancellationToken cancellationToken = default) =>
            Task.FromResult(call < snapshots.Length ? snapshots[call++]() : null);
    }

    private sealed class FakeCommands : IAlgoTradingCommandSender
    {
        public List<(nint Window, int Command)> Sent { get; } = [];
        public bool PostResult { get; init; } = true;

        public bool Send(nint window, int command)
        {
            if (PostResult) Sent.Add((window, command));
            return PostResult;
        }
    }

    private sealed class FakeProcess(TerminalRuntimeState state) : ITerminalProcessController
    {
        public Task<TerminalRuntimeState> GetStateAsync(TerminalRegistration terminal, CancellationToken cancellationToken) => Task.FromResult(state);
        public Task<int> StartAsync(TerminalRegistration terminal, CancellationToken cancellationToken) => Task.FromResult(state.ProcessId!.Value);
        public Task<StopResult> StopAsync(TerminalRegistration terminal, TimeSpan timeout, bool force, CancellationToken cancellationToken) => Task.FromResult(new StopResult(StopOutcome.ExitedGracefully, null));
    }
}
