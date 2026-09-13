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

    [Theory]
    [InlineData(10u, 20u, 30u, new uint[] { 20, 30 })]
    [InlineData(10u, 20u, 20u, new uint[] { 20 })]
    [InlineData(10u, 10u, 30u, new uint[] { 30 })]
    [InlineData(10u, 10u, 10u, new uint[] { })]
    public void Input_acquisition_attaches_to_target_and_foreground_threads(uint current, uint target, uint foreground, uint[] expected)
    {
        WindowsTerminalAlgoTradingController.RequiredInputAttachments(current, target, foreground).Should().Equal(expected);
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
        input.Minimized.Should().BeEmpty("an unchanged terminal state must not be minimized");
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
        input.Minimized.Should().Equal([4242]);
        input.LeaseDisposalOrder.Should().Equal(["Minimized", "Disposed"],
            "the terminal must be minimized before focus is returned");
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
        input.Minimized.Should().BeEmpty("an unconfirmed state change must not minimize the terminal");
    }

    [Fact]
    public async Task Set_reports_the_specific_input_acquisition_failure()
    {
        var input = new FakeInput { Failure = AlgoTradingInputFailure.ForegroundActivation };
        var inspector = new FakeInspector(Snapshot(AlgoTradingState.Disabled));

        var result = await new WindowsTerminalAlgoTradingController(inspector, RunningProcess(42), _ => [4242], input, FakeClock()).SetAsync(terminal, true);

        result.Success.Should().BeFalse();
        result.Message.Should().Be("The MetaTrader 5 window could not be brought to the foreground.");
        input.Sent.Should().BeEmpty("Ctrl+E must not be injected unless the target window owns the foreground");
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
        public List<nint> Minimized { get; } = [];
        public List<string> LeaseDisposalOrder { get; } = [];
        public bool LeaseDisposed { get; private set; }
        public AlgoTradingInputFailure Failure { get; init; }
        public AlgoTradingInputFailure TryAcquire(nint window, out IAlgoTradingLease? lease)
        {
            lease = null;
            if (Failure != AlgoTradingInputFailure.None) return Failure;
            Sent.Add((window, true));
            lease = new CallbackLease(
                () => { Minimized.Add(window); LeaseDisposalOrder.Add("Minimized"); },
                () => { LeaseDisposed = true; LeaseDisposalOrder.Add("Disposed"); });
            return AlgoTradingInputFailure.None;
        }
    }

    private sealed class CallbackLease(Action onMinimize, Action onDispose) : IAlgoTradingLease
    {
        public bool TryMinimize()
        {
            onMinimize();
            return true;
        }

        public void Dispose() => onDispose();
    }

    private sealed class FakeProcess(TerminalRuntimeState state) : ITerminalProcessController
    {
        public Task<TerminalRuntimeState> GetStateAsync(TerminalRegistration terminal, CancellationToken cancellationToken) => Task.FromResult(state);
        public Task<int> StartAsync(TerminalRegistration terminal, CancellationToken cancellationToken) => Task.FromResult(state.ProcessId!.Value);
        public Task<StopResult> StopAsync(TerminalRegistration terminal, TimeSpan timeout, bool force, CancellationToken cancellationToken) => Task.FromResult(new StopResult(StopOutcome.ExitedGracefully, null));
    }
}
