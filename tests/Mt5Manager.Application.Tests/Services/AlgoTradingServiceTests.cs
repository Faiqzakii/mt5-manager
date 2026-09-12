using FluentAssertions;
using Mt5Manager.Application.Abstractions;
using Mt5Manager.Application.Services;
using Mt5Manager.Domain.Models;

namespace Mt5Manager.Application.Tests.Services;

public sealed class AlgoTradingServiceTests
{
    [Fact]
    public async Task Queued_request_reloads_registry_inside_gate_and_resolves_updated_terminal_by_id()
    {
        var blockingId = Guid.NewGuid();
        var requestedId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var original = Registration(requestedId, "Original name");
        var updated = Registration(requestedId, "Updated name");
        var registry = new Registry(
            Registration(blockingId, "Blocking"),
            Registration(otherId, "Other"),
            original);
        var controller = new BlockingController();
        var service = Service(registry, controller, new Audit());
        var firstCall = service.SetAsync(new(blockingId, true, AlgoOperationSource.Wpf));
        Task<AlgoTradingOperationResult>? queuedCall = null;

        try
        {
            await controller.WaitForEntryAsync();
            queuedCall = service.SetAsync(new(requestedId, false, AlgoOperationSource.Telegram));
            registry.Items = [Registration(otherId, "Other renamed"), updated, Registration(blockingId, "Blocking")];

            await Task.Delay(100);
            registry.LoadCalls.Should().Be(1, "the queued request cannot load until it enters the gate");
        }
        finally
        {
            controller.Release();
            if (queuedCall is not null)
                await Task.WhenAll(firstCall, queuedCall).WaitAsync(TimeSpan.FromSeconds(5));
            else
                await firstCall.WaitAsync(TimeSpan.FromSeconds(5));
        }

        registry.LoadCalls.Should().Be(2);
        controller.Terminals.Should().HaveCount(2);
        controller.Terminals[1].Should().BeSameAs(updated);
        queuedCall!.Result.TerminalName.Should().Be("Updated name");
    }

    [Fact]
    public async Task Unknown_terminal_is_rejected_without_calling_controller_and_is_audited_once()
    {
        var id = Guid.NewGuid();
        var controller = new Controller(new(true, "unused", null));
        var audit = new Audit();

        var result = await Service(new Registry(), controller, audit)
            .SetAsync(new(id, false, AlgoOperationSource.Telegram));

        result.TerminalId.Should().Be(id);
        result.TerminalName.Should().Be("Unknown terminal");
        result.Enable.Should().BeFalse();
        result.Result.Success.Should().BeFalse();
        result.Result.Message.Should().Contain(id.ToString());
        controller.Terminals.Should().BeEmpty();
        var record = audit.Records.Should().ContainSingle().Subject;
        record.Outcome.Should().Be(AuditOutcome.Rejected);
        record.Operation.Should().Be("Disable Algo");
        record.Source.Should().Be(AlgoOperationSource.Telegram);
    }

    [Fact]
    public async Task Different_service_instances_serialize_controller_calls_process_wide()
    {
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var controller = new BlockingController();
        var first = Service(new Registry(Registration(firstId, "First")), controller, new Audit());
        var second = Service(new Registry(Registration(secondId, "Second")), controller, new Audit());
        var firstCall = first.SetAsync(new(firstId, true, AlgoOperationSource.Wpf));
        Task<AlgoTradingOperationResult>? secondCall = null;

        try
        {
            await controller.WaitForEntryAsync();
            secondCall = second.SetAsync(new(secondId, false, AlgoOperationSource.Telegram));
            await Task.Delay(100);
            controller.EntryCount.Should().Be(1);
        }
        finally
        {
            controller.Release();
            if (secondCall is not null)
                await Task.WhenAll(firstCall, secondCall).WaitAsync(TimeSpan.FromSeconds(5));
            else
                await firstCall.WaitAsync(TimeSpan.FromSeconds(5));
        }

        controller.MaxConcurrent.Should().Be(1);
    }

    [Fact]
    public async Task Preserves_controller_result_and_audits_source_and_outcome_once()
    {
        var id = Guid.NewGuid();
        var snapshot = new TerminalAccountSnapshot(1, DateTimeOffset.UtcNow, "data", 1, "A", "S", "C", AccountTradeMode.Real, true, AlgoTradingState.Disabled, true, true, true);
        var controlResult = new AlgoTradingControlResult(false, "controller rejected", snapshot);
        var audit = new Audit();

        var result = await Service(new Registry(Registration(id, "Broker")), new Controller(controlResult), audit)
            .SetAsync(new(id, false, AlgoOperationSource.Telegram));

        result.Result.Should().BeSameAs(controlResult);
        var record = audit.Records.Should().ContainSingle().Subject;
        record.TerminalId.Should().Be(id);
        record.TerminalName.Should().Be("Broker");
        record.Operation.Should().Be("Disable Algo");
        record.Outcome.Should().Be(AuditOutcome.Rejected);
        record.Message.Should().Be("controller rejected");
        record.Source.Should().Be(AlgoOperationSource.Telegram);
    }

    [Fact]
    public async Task Successful_controller_result_is_audited_as_completed()
    {
        var id = Guid.NewGuid();
        var audit = new Audit();
        await Service(new Registry(Registration(id, "Broker")), new Controller(new(true, "done", null)), audit)
            .SetAsync(new(id, true, AlgoOperationSource.Wpf));

        audit.Records.Should().ContainSingle().Which.Outcome.Should().Be(AuditOutcome.Completed);
        audit.Records[0].Operation.Should().Be("Enable Algo");
        audit.Records[0].Source.Should().Be(AlgoOperationSource.Wpf);
    }

    [Fact]
    public async Task Audit_append_failure_after_controller_result_is_not_retried_as_rejection()
    {
        var id = Guid.NewGuid();
        var audit = new Audit { Exception = new IOException("audit unavailable") };
        var service = Service(new Registry(Registration(id, "Broker")),
            new Controller(new(true, "done", null)), audit);

        var act = () => service.SetAsync(new(id, true, AlgoOperationSource.Wpf));

        await act.Should().ThrowAsync<IOException>().WithMessage("audit unavailable");
        audit.Attempts.Should().Be(1);
    }

    [Fact]
    public async Task Cancellation_propagates_to_controller_without_rejection_audit()
    {
        var id = Guid.NewGuid();
        var audit = new Audit();
        using var cancellation = new CancellationTokenSource();
        var controller = new CancellingController(cancellation);

        var act = () => Service(new Registry(Registration(id, "Broker")), controller, audit)
            .SetAsync(new(id, true, AlgoOperationSource.Telegram), cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        controller.Token.Should().Be(cancellation.Token);
        audit.Records.Should().BeEmpty();
    }

    private static AlgoTradingService Service(ITerminalRegistry registry, ITerminalAlgoTradingController controller, IAuditLogger audit) => new(registry, controller, audit);
    private static TerminalRegistration Registration(Guid id, string name) => new(id, name, "terminal.exe", "data", ".", [], DiscoverySource.Manual, true);

    private sealed class Registry(params TerminalRegistration[] terminals) : ITerminalRegistry
    {
        public IReadOnlyList<TerminalRegistration> Items { get; set; } = terminals;
        public int LoadCalls { get; private set; }
        public Task<IReadOnlyList<TerminalRegistration>> LoadAsync(CancellationToken cancellationToken = default) { LoadCalls++; return Task.FromResult(Items); }
        public Task SaveAsync(IReadOnlyList<TerminalRegistration> terminals, CancellationToken cancellationToken = default) { Items = terminals; return Task.CompletedTask; }
    }

    private sealed class Controller(AlgoTradingControlResult result) : ITerminalAlgoTradingController
    {
        public AlgoTradingControlResult Result { get; } = result;
        public List<TerminalRegistration> Terminals { get; } = [];
        public Task<AlgoTradingControlResult> SetAsync(TerminalRegistration terminal, bool enable, CancellationToken cancellationToken = default) { Terminals.Add(terminal); return Task.FromResult(Result); }
    }

    private sealed class CancellingController(CancellationTokenSource cancellation) : ITerminalAlgoTradingController
    {
        public CancellationToken Token { get; private set; }
        public Task<AlgoTradingControlResult> SetAsync(TerminalRegistration terminal, bool enable, CancellationToken cancellationToken = default)
        {
            Token = cancellationToken;
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException();
        }
    }

    private sealed class BlockingController : ITerminalAlgoTradingController
    {
        private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int concurrent;
        public int EntryCount;
        public int MaxConcurrent;
        public List<TerminalRegistration> Terminals { get; } = [];
        public async Task<AlgoTradingControlResult> SetAsync(TerminalRegistration terminal, bool enable, CancellationToken cancellationToken = default)
        {
            lock (Terminals) Terminals.Add(terminal);
            Interlocked.Increment(ref EntryCount);
            var active = Interlocked.Increment(ref concurrent);
            MaxConcurrent = Math.Max(MaxConcurrent, active);
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            Interlocked.Decrement(ref concurrent);
            return new(true, "done", null);
        }
        public Task WaitForEntryAsync() => entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        public void Release() => release.TrySetResult();
    }

    private sealed class Audit : IAuditLogger
    {
        public List<AuditRecord> Records { get; } = [];
        public Exception? Exception { get; init; }
        public int Attempts { get; private set; }
        public Task AppendAsync(AuditRecord record, CancellationToken cancellationToken = default)
        {
            Attempts++;
            if (Exception is not null) throw Exception;
            Records.Add(record);
            return Task.CompletedTask;
        }
    }
}
