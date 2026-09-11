using System.Runtime.InteropServices;
using FluentAssertions;
using Mt5Manager.Application.Abstractions;
using Mt5Manager.Domain.Models;
using Mt5Manager.Infrastructure.Processes;

namespace Mt5Manager.Infrastructure.Tests.Processes;

public sealed class WindowsTerminalAlgoTradingControllerTests
{
    private readonly TerminalRegistration terminal = new(Guid.NewGuid(), "Broker", @"C:\Apps\Broker\terminal64.exe", @"C:\Data", @"C:\Apps\Broker", [], DiscoverySource.Manual, true);

    [Fact]
    public void Native_input_layout_matches_win32_send_input()
    {
        Marshal.SizeOf<WindowsTerminalAlgoTradingController.NativeInput>().Should().Be(40);
        Marshal.SizeOf<WindowsTerminalAlgoTradingController.NativeKeyboardInput>().Should().Be(24);
        Marshal.OffsetOf<WindowsTerminalAlgoTradingController.NativeInput>("Keyboard").Should().Be(8);
        Marshal.OffsetOf<WindowsTerminalAlgoTradingController.NativeKeyboardInput>("VirtualKey").Should().Be(0);
    }

    [Fact]
    public async Task Set_is_no_op_when_desired_state_is_already_current()
    {
        var input = new FakeInput();
        var inspector = new FakeInspector(Snapshot(AlgoTradingState.Enabled));

        var result = await new WindowsTerminalAlgoTradingController(inspector, RunningProcess(42), _ => [4242], input, FakeClock()).SetAsync(terminal, true);

        result.Success.Should().BeTrue();
        result.Message.Should().Be("Algo Trading is already enabled.");
        input.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Set_refuses_when_no_unique_terminal_window_exists()
    {
        var input = new FakeInput();
        var inspector = new FakeInspector(Snapshot(AlgoTradingState.Disabled));

        var result = await new WindowsTerminalAlgoTradingController(inspector, RunningProcess(42), _ => [], input, FakeClock()).SetAsync(terminal, true);

        result.Success.Should().BeFalse();
        result.Message.Should().Be("No matching MetaTrader 5 window was found.");
        input.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Set_sends_ctrl_e_to_the_matched_window_and_confirms_new_state()
    {
        var input = new FakeInput();
        var inspector = new FakeInspector(Snapshot(AlgoTradingState.Disabled), Snapshot(AlgoTradingState.Enabled));

        var result = await new WindowsTerminalAlgoTradingController(inspector, RunningProcess(42), _ => [4242], input, FakeClock()).SetAsync(terminal, true);

        result.Success.Should().BeTrue();
        result.Snapshot!.GlobalAlgoTrading.Should().Be(AlgoTradingState.Enabled);
        input.Sent.Should().Equal([(4242, true)]);
    }


    [Fact]
    public async Task Set_holds_input_lease_until_desired_state_is_observed()
    {
        var input = new FakeInput();
        var inspector = new FakeInspector(
            () => Snapshot(AlgoTradingState.Disabled),
            () =>
            {
                input.LeaseDisposed.Should().BeFalse("the terminal must retain focus while confirmation is polled");
                return Snapshot(AlgoTradingState.Enabled);
            });

        var result = await new WindowsTerminalAlgoTradingController(inspector, RunningProcess(42), _ => [4242], input, FakeClock()).SetAsync(terminal, true);

        result.Success.Should().BeTrue();
        input.LeaseDisposed.Should().BeTrue("focus must be restored after confirmation completes");
    }
    [Fact]
    public async Task Set_fails_when_observed_state_does_not_change()
    {
        var input = new FakeInput();
        var inspector = new FakeInspector(Snapshot(AlgoTradingState.Disabled), Snapshot(AlgoTradingState.Disabled), Snapshot(AlgoTradingState.Disabled));

        var result = await new WindowsTerminalAlgoTradingController(inspector, RunningProcess(42), _ => [4242], input, FakeClock(), pollInterval: TimeSpan.Zero, timeout: TimeSpan.Zero).SetAsync(terminal, true);

        result.Success.Should().BeFalse();
        result.Message.Should().Be("Algo Trading did not become enabled.");
        input.Sent.Should().Equal([(4242, true)]);
    }

    [Fact]
    public async Task Set_refuses_when_runtime_snapshot_is_unavailable_or_unknown()
    {
        var input = new FakeInput();
        var inspector = new FakeInspector(Snapshot(AlgoTradingState.Unknown));

        var result = await new WindowsTerminalAlgoTradingController(inspector, RunningProcess(42), _ => [4242], input, FakeClock()).SetAsync(terminal, true);

        result.Success.Should().BeFalse();
        result.Message.Should().Be("The current Algo Trading state is unknown.");
        input.Sent.Should().BeEmpty();
    }

    private static FakeProcess RunningProcess(int pid) => new(new TerminalRuntimeState(TerminalState.Running, pid, null));
    private static Func<DateTimeOffset> FakeClock() => () => DateTimeOffset.UnixEpoch;
    private static TerminalAccountSnapshot Snapshot(AlgoTradingState state) => new(1, DateTimeOffset.UtcNow, @"C:\Data", 123, "Trader", "Server", "Company", AccountTradeMode.Real, true, state, true, true, true);

    private sealed class FakeInspector(params Func<TerminalAccountSnapshot?>[] snapshots) : ITerminalRuntimeInspector
    {
        private int call;
        public FakeInspector(params TerminalAccountSnapshot?[] snapshots) : this(snapshots.Select<TerminalAccountSnapshot?, Func<TerminalAccountSnapshot?>>(snapshot => () => snapshot).ToArray()) { }
        public Task<TerminalAccountSnapshot?> ReadAsync(TerminalRegistration terminal, CancellationToken cancellationToken = default) =>
            Task.FromResult(call < snapshots.Length ? snapshots[call++]() : null);
    }

    private sealed class FakeInput : IAlgoTradingInput
    {
        public List<(nint Window, bool Sent)> Sent { get; } = [];
        public bool LeaseDisposed { get; private set; }
        public bool TryAcquire(nint window, out IDisposable? lease)
        {
            Sent.Add((window, true));
            lease = new CallbackDisposable(() => LeaseDisposed = true);
            return true;
        }
    }

    private sealed class CallbackDisposable(Action callback) : IDisposable
    {
        public void Dispose() => callback();
    }

    private sealed class FakeProcess(TerminalRuntimeState state) : ITerminalProcessController
    {
        public Task<TerminalRuntimeState> GetStateAsync(TerminalRegistration terminal, CancellationToken cancellationToken) => Task.FromResult(state);
        public Task<int> StartAsync(TerminalRegistration terminal, CancellationToken cancellationToken) => Task.FromResult(state.ProcessId!.Value);
        public Task<StopResult> StopAsync(TerminalRegistration terminal, TimeSpan timeout, bool force, CancellationToken cancellationToken) => Task.FromResult(new StopResult(StopOutcome.ExitedGracefully, null));
    }
}
