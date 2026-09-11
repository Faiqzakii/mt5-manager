using FluentAssertions;
using Mt5Manager.Application.Abstractions;
using Mt5Manager.Application.Services;
using Mt5Manager.Domain.Models;

namespace Mt5Manager.Application.Tests.Services;

public sealed class TerminalOperationCoordinatorTests
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromMilliseconds(50);

    [Fact]
    public async Task Stopped_terminal_cleans_the_selected_categories_and_stays_stopped()
    {
        var terminalId = Guid.NewGuid();
        var controller = new FakeProcessController();
        var cleanup = new FakeCleanupService();
        var audit = new RecordingAuditLogger();
        var coordinator = Coordinator(new FakeRegistry(Registration(terminalId)), controller, cleanup, audit);
        var request = Request(terminalId, CleanupCategory.Logs, CleanupCategory.Ticks);

        var preparation = await coordinator.PrepareCleanupAsync(request);

        preparation.Status.Should().Be(CleanupPreparationStatus.Ready);
        preparation.WasRunning.Should().BeFalse();
        controller.StopForces.Should().BeEmpty();
        controller.StateCalls.Should().Be(1);

        var outcome = await coordinator.ContinueCleanupAsync(preparation, forceApproved: false);

        outcome.Status.Should().Be(CleanupOutcomeStatus.Completed);
        outcome.Result.WasRunning.Should().BeFalse();
        outcome.Result.Restarted.Should().BeFalse();
        outcome.Result.RestartError.Should().BeNull();
        outcome.Result.Categories.Select(result => result.Category)
            .Should().BeEquivalentTo([CleanupCategory.Logs, CleanupCategory.Ticks]);
        controller.StartedTerminals.Should().BeEmpty();

        cleanup.Requests.Should().ContainSingle();
        cleanup.Requests[0].Should().BeEquivalentTo(
            new HashSet<CleanupCategory> { CleanupCategory.Logs, CleanupCategory.Ticks });

        var record = audit.Records.Should().ContainSingle().Subject;
        record.TerminalId.Should().Be(terminalId);
        record.TerminalName.Should().Be("Terminal");
        record.Operation.Should().Be(TerminalOperationCoordinator.CleanupOperation);
        record.WasRunning.Should().BeFalse();
        record.Restarted.Should().BeFalse();
        record.RestartError.Should().BeNull();
        record.Message.Should().BeNull();
        record.ShutdownMethod.Should().Be(ShutdownMethod.None);
        record.Outcome.Should().Be(AuditOutcome.Completed);
        record.Categories.Should().BeEquivalentTo([CleanupCategory.Logs, CleanupCategory.Ticks]);
        record.CategoryOutcomes.Select(item => item.Category)
            .Should().BeEquivalentTo([CleanupCategory.Logs, CleanupCategory.Ticks]);
        record.Timestamp.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Running_terminal_is_stopped_before_deletion_and_restarted_afterwards()
    {
        var terminalId = Guid.NewGuid();
        var controller = new FakeProcessController
        {
            State = new TerminalRuntimeState(TerminalState.Running, 4242, null)
        };
        var cleanup = new FakeCleanupService();
        var audit = new RecordingAuditLogger();
        var coordinator = Coordinator(new FakeRegistry(Registration(terminalId)), controller, cleanup, audit);
        var request = Request(terminalId, CleanupCategory.Logs);

        var preparation = await coordinator.PrepareCleanupAsync(request);

        preparation.Status.Should().Be(CleanupPreparationStatus.Ready);
        preparation.WasRunning.Should().BeTrue();
        preparation.Message.Should().BeNull();
        controller.StopForces.Should().Equal(false);
        controller.StopTimeouts.Should().Equal(StopTimeout);

        var outcome = await coordinator.ContinueCleanupAsync(preparation, forceApproved: false);

        outcome.Status.Should().Be(CleanupOutcomeStatus.Completed);
        outcome.Result.WasRunning.Should().BeTrue();
        outcome.Result.Restarted.Should().BeTrue();
        outcome.Result.RestartError.Should().BeNull();
        controller.StateCalls.Should().Be(1, "the pre-operation running state is captured once");
        controller.StartedTerminals.Should().Equal(terminalId);
        cleanup.Requests.Should().ContainSingle();

        var record = audit.Records.Should().ContainSingle().Subject;
        record.Outcome.Should().Be(AuditOutcome.Completed);
        record.WasRunning.Should().BeTrue();
        record.ShutdownMethod.Should().Be(ShutdownMethod.Graceful);
        record.Restarted.Should().BeTrue();
        record.RestartError.Should().BeNull();
    }

    [Fact]
    public async Task Stop_timeout_requires_force_confirmation_without_deleting_anything()
    {
        var terminalId = Guid.NewGuid();
        var controller = new FakeProcessController
        {
            State = new TerminalRuntimeState(TerminalState.Running, 4242, null),
            StopResultFor = _ => new StopResult(StopOutcome.TimedOut, null)
        };
        var cleanup = new FakeCleanupService();
        var audit = new RecordingAuditLogger();
        var coordinator = Coordinator(new FakeRegistry(Registration(terminalId)), controller, cleanup, audit);

        var preparation = await coordinator.PrepareCleanupAsync(Request(terminalId, CleanupCategory.Logs));

        preparation.Status.Should().Be(CleanupPreparationStatus.RequiresForceConfirmation);
        preparation.WasRunning.Should().BeTrue();
        preparation.Message.Should().NotBeNullOrWhiteSpace();
        controller.StopForces.Should().Equal(false);
        cleanup.Requests.Should().BeEmpty();
        controller.StartedTerminals.Should().BeEmpty();
        audit.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task Declined_force_leaves_files_untouched_and_records_the_decline()
    {
        var terminalId = Guid.NewGuid();
        var controller = new FakeProcessController
        {
            State = new TerminalRuntimeState(TerminalState.Running, 4242, null),
            StopResultFor = _ => new StopResult(StopOutcome.TimedOut, null)
        };
        var cleanup = new FakeCleanupService();
        var audit = new RecordingAuditLogger();
        var coordinator = Coordinator(new FakeRegistry(Registration(terminalId)), controller, cleanup, audit);

        var preparation = await coordinator.PrepareCleanupAsync(Request(terminalId, CleanupCategory.Logs));
        var outcome = await coordinator.ContinueCleanupAsync(preparation, forceApproved: false);

        outcome.Status.Should().Be(CleanupOutcomeStatus.ForceDeclined);
        outcome.Result.WasRunning.Should().BeTrue();
        outcome.Result.Restarted.Should().BeFalse();
        outcome.Result.Categories.Should().BeEmpty();
        cleanup.Requests.Should().BeEmpty();
        controller.StopForces.Should().Equal(false);
        controller.StartedTerminals.Should().BeEmpty();

        var record = audit.Records.Should().ContainSingle().Subject;
        record.Outcome.Should().Be(AuditOutcome.ForceDeclined);
        record.WasRunning.Should().BeTrue();
        record.Restarted.Should().BeFalse();
        record.CategoryOutcomes.Should().BeEmpty();
        record.Categories.Should().Equal(CleanupCategory.Logs);
    }

    [Fact]
    public async Task Approved_force_terminates_then_cleans_and_restarts()
    {
        var terminalId = Guid.NewGuid();
        var controller = new FakeProcessController
        {
            State = new TerminalRuntimeState(TerminalState.Running, 4242, null),
            StopResultFor = force => force
                ? new StopResult(StopOutcome.ForceTerminated, null)
                : new StopResult(StopOutcome.TimedOut, null)
        };
        var cleanup = new FakeCleanupService();
        var audit = new RecordingAuditLogger();
        var coordinator = Coordinator(new FakeRegistry(Registration(terminalId)), controller, cleanup, audit);

        var preparation = await coordinator.PrepareCleanupAsync(Request(terminalId, CleanupCategory.History));
        preparation.Status.Should().Be(CleanupPreparationStatus.RequiresForceConfirmation);

        var outcome = await coordinator.ContinueCleanupAsync(preparation, forceApproved: true);

        outcome.Status.Should().Be(CleanupOutcomeStatus.Completed);
        outcome.Result.Restarted.Should().BeTrue();
        controller.StopForces.Should().Equal(false, true);
        cleanup.Requests.Should().ContainSingle();
        controller.StartedTerminals.Should().Equal(terminalId);

        var record = audit.Records.Should().ContainSingle().Subject;
        record.Outcome.Should().Be(AuditOutcome.Completed);
        record.ShutdownMethod.Should().Be(ShutdownMethod.Force);
        record.Restarted.Should().BeTrue();
    }

    [Fact]
    public async Task Partial_cleanup_failures_are_reported_and_restart_still_happens()
    {
        var terminalId = Guid.NewGuid();
        var failure = new FileFailure(@"C:\Data\Logs\locked.log", "Access denied.");
        var controller = new FakeProcessController
        {
            State = new TerminalRuntimeState(TerminalState.Running, 4242, null)
        };
        var cleanup = new FakeCleanupService
        {
            ResultFactory = (_, categories) => categories
                .Select(category => category == CleanupCategory.Logs
                    ? new CleanupCategoryResult(category, 2, 2048, [failure])
                    : new CleanupCategoryResult(category, 5, 5120, []))
                .ToArray()
        };
        var audit = new RecordingAuditLogger();
        var coordinator = Coordinator(new FakeRegistry(Registration(terminalId)), controller, cleanup, audit);

        var preparation = await coordinator.PrepareCleanupAsync(Request(terminalId, CleanupCategory.Logs, CleanupCategory.Ticks));
        var outcome = await coordinator.ContinueCleanupAsync(preparation, forceApproved: false);

        outcome.Status.Should().Be(CleanupOutcomeStatus.Completed);
        outcome.Result.Restarted.Should().BeTrue();
        outcome.Result.RestartError.Should().BeNull();
        controller.StartedTerminals.Should().Equal(terminalId);
        outcome.Result.Categories.Should().Contain(result => result.Category == CleanupCategory.Logs)
            .Which.Failures.Should().ContainSingle().Which.Should().Be(failure);
        outcome.Result.Categories.Should().Contain(result => result.Category == CleanupCategory.Ticks)
            .Which.DeletedFiles.Should().Be(5);

        var record = audit.Records.Should().ContainSingle().Subject;
        record.Outcome.Should().Be(AuditOutcome.Completed);
        record.Restarted.Should().BeTrue();
        record.CategoryOutcomes.Should().Contain(item => item.Category == CleanupCategory.Logs)
            .Which.Failures.Should().ContainSingle().Which.Should().Be(failure);
    }

    [Fact]
    public async Task Restart_failure_is_distinct_from_a_successful_cleanup()
    {
        var terminalId = Guid.NewGuid();
        var controller = new FakeProcessController
        {
            State = new TerminalRuntimeState(TerminalState.Running, 4242, null),
            StartException = new InvalidOperationException("The terminal executable is missing.")
        };
        var cleanup = new FakeCleanupService
        {
            ResultFactory = (_, categories) => categories
                .Select(category => new CleanupCategoryResult(category, 3, 600, []))
                .ToArray()
        };
        var audit = new RecordingAuditLogger();
        var coordinator = Coordinator(new FakeRegistry(Registration(terminalId)), controller, cleanup, audit);

        var preparation = await coordinator.PrepareCleanupAsync(Request(terminalId, CleanupCategory.Logs));
        var outcome = await coordinator.ContinueCleanupAsync(preparation, forceApproved: false);

        outcome.Status.Should().Be(CleanupOutcomeStatus.Completed);
        outcome.Result.Restarted.Should().BeFalse();
        outcome.Result.RestartError.Should().Be("The terminal executable is missing.");
        outcome.Result.Categories.Should().ContainSingle().Which.DeletedFiles.Should().Be(3);

        var record = audit.Records.Should().ContainSingle().Subject;
        record.Outcome.Should().Be(AuditOutcome.Completed);
        record.Restarted.Should().BeFalse();
        record.RestartError.Should().Be("The terminal executable is missing.");
        record.CategoryOutcomes.Should().ContainSingle().Which.DeletedFiles.Should().Be(3);
    }

    [Fact]
    public async Task Unverified_data_directory_is_rejected_without_touching_the_terminal()
    {
        var terminalId = Guid.NewGuid();
        var controller = new FakeProcessController();
        var cleanup = new FakeCleanupService();
        var audit = new RecordingAuditLogger();
        var coordinator = Coordinator(new FakeRegistry(Registration(terminalId, verified: false)), controller, cleanup, audit);

        var preparation = await coordinator.PrepareCleanupAsync(Request(terminalId, CleanupCategory.Logs));

        preparation.Status.Should().Be(CleanupPreparationStatus.Rejected);
        preparation.Message.Should().NotBeNullOrWhiteSpace();
        controller.StateCalls.Should().Be(0);

        var outcome = await coordinator.ContinueCleanupAsync(preparation, forceApproved: true);

        outcome.Status.Should().Be(CleanupOutcomeStatus.Rejected);
        controller.StopForces.Should().BeEmpty();
        controller.StartedTerminals.Should().BeEmpty();
        cleanup.Requests.Should().BeEmpty();
        audit.Records.Should().ContainSingle().Which.Outcome.Should().Be(AuditOutcome.Rejected);
        audit.Records[0].TerminalId.Should().Be(terminalId);
        outcome.Message.Should().Be(preparation.Message);
        audit.Records[0].Message.Should().Be(preparation.Message);
    }

    [Fact]
    public async Task Unknown_terminal_rejection_preserves_reason_in_audit()
    {
        var controller = new FakeProcessController();
        var cleanup = new FakeCleanupService();
        var audit = new RecordingAuditLogger();
        var coordinator = Coordinator(new FakeRegistry(Registration(Guid.NewGuid())), controller, cleanup, audit);

        var preparation = await coordinator.PrepareCleanupAsync(Request(Guid.NewGuid(), CleanupCategory.Logs));
        var outcome = await coordinator.ContinueCleanupAsync(preparation, forceApproved: true);

        preparation.Status.Should().Be(CleanupPreparationStatus.Rejected);
        outcome.Message.Should().Be(preparation.Message);
        controller.StateCalls.Should().Be(0);
        cleanup.Requests.Should().BeEmpty();
        audit.Records.Should().ContainSingle().Which.Message.Should().Be(preparation.Message);
    }

    [Fact]
    public async Task Forged_rejected_preparation_cannot_inject_an_audit_reason()
    {
        var terminalId = Guid.NewGuid();
        var terminal = Registration(terminalId);
        var audit = new RecordingAuditLogger();
        var coordinator = Coordinator(new FakeRegistry(terminal), new FakeProcessController(), new FakeCleanupService(), audit);
        var forged = new CleanupPreparation(CleanupPreparationStatus.Rejected,
            Request(terminalId, CleanupCategory.Logs), false, "attacker supplied", terminal, Guid.Empty);

        var outcome = await coordinator.ContinueCleanupAsync(forged, forceApproved: true);

        outcome.Status.Should().Be(CleanupOutcomeStatus.Rejected);
        outcome.Message.Should().Be("This cleanup preparation is no longer active.");
        audit.Records.Should().ContainSingle().Which.Message.Should().Be(outcome.Message);
    }

    [Fact]
    public async Task Unknown_terminal_is_rejected_without_side_effects()
    {
        var controller = new FakeProcessController();
        var cleanup = new FakeCleanupService();
        var audit = new RecordingAuditLogger();
        var coordinator = Coordinator(new FakeRegistry(Registration(Guid.NewGuid())), controller, cleanup, audit);

        var preparation = await coordinator.PrepareCleanupAsync(Request(Guid.NewGuid(), CleanupCategory.Logs));

        preparation.Status.Should().Be(CleanupPreparationStatus.Rejected);
        controller.StateCalls.Should().Be(0);
        cleanup.Requests.Should().BeEmpty();
        audit.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task Undetermined_terminal_state_is_rejected_before_cleanup()
    {
        var terminalId = Guid.NewGuid();
        var controller = new FakeProcessController
        {
            State = new TerminalRuntimeState(TerminalState.Error, null, "Access is denied.")
        };
        var cleanup = new FakeCleanupService();
        var audit = new RecordingAuditLogger();
        var coordinator = Coordinator(new FakeRegistry(Registration(terminalId)), controller, cleanup, audit);

        var preparation = await coordinator.PrepareCleanupAsync(Request(terminalId, CleanupCategory.Logs));

        preparation.Status.Should().Be(CleanupPreparationStatus.Rejected);
        preparation.Message.Should().Contain("Access is denied.");
        cleanup.Requests.Should().BeEmpty();
        controller.StartedTerminals.Should().BeEmpty();
        audit.Records.Should().BeEmpty();
    }
    [Fact]
    public async Task Prepared_operation_reserves_terminal_until_it_is_continued()
    {
        var terminalId = Guid.NewGuid();
        var controller = new FakeProcessController();
        var coordinator = Coordinator(new FakeRegistry(Registration(terminalId)), controller, new FakeCleanupService(), new RecordingAuditLogger());

        var first = await coordinator.PrepareCleanupAsync(Request(terminalId, CleanupCategory.Logs));
        var interleaved = await coordinator.PrepareCleanupAsync(Request(terminalId, CleanupCategory.Ticks));

        first.Status.Should().Be(CleanupPreparationStatus.Ready);
        interleaved.Status.Should().Be(CleanupPreparationStatus.Rejected);
        interleaved.Message.Should().Contain("operation");
        controller.StateCalls.Should().Be(1);

        (await coordinator.ContinueCleanupAsync(first, forceApproved: false)).Status.Should().Be(CleanupOutcomeStatus.Completed);
        (await coordinator.PrepareCleanupAsync(Request(terminalId, CleanupCategory.Ticks))).Status.Should().Be(CleanupPreparationStatus.Ready);
    }

    [Fact]
    public async Task Cancelled_preparation_releases_the_terminal_reservation()
    {
        var terminalId = Guid.NewGuid();
        var coordinator = Coordinator(new FakeRegistry(Registration(terminalId)), new FakeProcessController(), new FakeCleanupService(), new RecordingAuditLogger());
        var preparation = await coordinator.PrepareCleanupAsync(Request(terminalId, CleanupCategory.Logs));

        await coordinator.CancelPreparationAsync(preparation);

        (await coordinator.PrepareCleanupAsync(Request(terminalId, CleanupCategory.Ticks))).Status
            .Should().Be(CleanupPreparationStatus.Ready);
    }

    [Fact]
    public async Task Disappeared_terminal_is_rejected_and_audited_from_the_preparation_snapshot()
    {
        var terminalId = Guid.NewGuid();
        var registry = new FakeRegistry(Registration(terminalId));
        var audit = new RecordingAuditLogger();
        var coordinator = Coordinator(registry, new FakeProcessController(), new FakeCleanupService(), audit);
        var preparation = await coordinator.PrepareCleanupAsync(Request(terminalId, CleanupCategory.Logs));
        await registry.SaveAsync([]);

        var outcome = await coordinator.ContinueCleanupAsync(preparation, forceApproved: false);

        outcome.Status.Should().Be(CleanupOutcomeStatus.Rejected);
        var record = audit.Records.Should().ContainSingle().Subject;
        record.Outcome.Should().Be(AuditOutcome.Rejected);
        record.TerminalId.Should().Be(terminalId);
        record.TerminalName.Should().Be("Terminal");
    }

    [Theory]
    [InlineData("categories")]
    [InlineData("status")]
    [InlineData("running")]
    [InlineData("snapshot")]
    public async Task Tampered_preparation_is_rejected_without_consuming_the_original(string field)
    {
        var terminalId = Guid.NewGuid();
        var cleanup = new FakeCleanupService();
        var audit = new RecordingAuditLogger();
        var coordinator = Coordinator(new FakeRegistry(Registration(terminalId)), new FakeProcessController(), cleanup, audit);
        var original = await coordinator.PrepareCleanupAsync(Request(terminalId, CleanupCategory.Logs));
        var tampered = field switch
        {
            "categories" => original with { Request = Request(terminalId, CleanupCategory.Ticks) },
            "status" => original with { Status = CleanupPreparationStatus.RequiresForceConfirmation },
            "running" => original with { WasRunning = !original.WasRunning },
            _ => original with { TerminalSnapshot = original.TerminalSnapshot with { DisplayName = "Forged" } }
        };

        var rejected = await coordinator.ContinueCleanupAsync(tampered, forceApproved: true);
        var completed = await coordinator.ContinueCleanupAsync(original, forceApproved: false);

        rejected.Status.Should().Be(CleanupOutcomeStatus.Rejected);
        rejected.Message.Should().Be("This cleanup preparation is no longer active.");
        completed.Status.Should().Be(CleanupOutcomeStatus.Completed);
        cleanup.Requests.Should().ContainSingle();
        audit.Records.Should().HaveCount(2);
    }

    [Fact]
    public async Task Replaying_a_consumed_preparation_rejects_safely_and_audits_the_attempt_once()
    {
        var terminalId = Guid.NewGuid();
        var cleanup = new FakeCleanupService();
        var audit = new RecordingAuditLogger();
        var coordinator = Coordinator(new FakeRegistry(Registration(terminalId)), new FakeProcessController(), cleanup, audit);
        var preparation = await coordinator.PrepareCleanupAsync(Request(terminalId, CleanupCategory.Logs));

        (await coordinator.ContinueCleanupAsync(preparation, forceApproved: false)).Status.Should().Be(CleanupOutcomeStatus.Completed);
        var replay = await coordinator.ContinueCleanupAsync(preparation, forceApproved: false);

        replay.Status.Should().Be(CleanupOutcomeStatus.Rejected);
        cleanup.Requests.Should().ContainSingle();
        audit.Records.Should().HaveCount(2);
        audit.Records[1].Outcome.Should().Be(AuditOutcome.Rejected);
    }


    [Fact]
    public async Task Mutating_request_categories_after_prepare_cannot_expand_cleanup_scope()
    {
        var terminalId = Guid.NewGuid();
        var categories = new HashSet<CleanupCategory> { CleanupCategory.Logs };
        var request = new CleanupRequest(terminalId, categories);
        var cleanup = new FakeCleanupService();
        var coordinator = Coordinator(new FakeRegistry(Registration(terminalId)), new FakeProcessController(), cleanup, new RecordingAuditLogger());
        var preparation = await coordinator.PrepareCleanupAsync(request);

        categories.Add(CleanupCategory.Ticks);
        var outcome = await coordinator.ContinueCleanupAsync(preparation, forceApproved: false);

        outcome.Status.Should().Be(CleanupOutcomeStatus.Completed);
        cleanup.Requests.Should().ContainSingle();
        cleanup.Requests[0].Should().Equal(CleanupCategory.Logs);
    }

    [Fact]
    public async Task Operations_on_the_same_terminal_serialize()
    {
        var terminalId = Guid.NewGuid();
        var registry = new FakeRegistry(Registration(terminalId));
        var controller = new BlockingProcessController();
        var coordinator = Coordinator(registry, controller, new FakeCleanupService(), new RecordingAuditLogger());

        var first = coordinator.PrepareCleanupAsync(Request(terminalId, CleanupCategory.Logs));
        await controller.WaitForEntriesAsync(1);

        var second = coordinator.PrepareCleanupAsync(Request(terminalId, CleanupCategory.Logs));
        await Task.Delay(250);

        controller.EntryCount.Should().Be(1, "the second operation waits for the per-terminal lock");
        controller.MaxConcurrent.Should().Be(1);

        controller.ReleaseAll();
        var preparations = await Task.WhenAll(first, second);

        preparations[0].Status.Should().Be(CleanupPreparationStatus.Ready);
        preparations[1].Status.Should().Be(CleanupPreparationStatus.Rejected);
        controller.MaxConcurrent.Should().Be(1);
    }

    [Fact]
    public async Task Operations_on_different_terminals_do_not_share_a_lock()
    {
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var registry = new FakeRegistry(Registration(firstId), Registration(secondId));
        var controller = new BlockingProcessController();
        var coordinator = Coordinator(registry, controller, new FakeCleanupService(), new RecordingAuditLogger());

        var first = coordinator.PrepareCleanupAsync(Request(firstId, CleanupCategory.Logs));
        var second = coordinator.PrepareCleanupAsync(Request(secondId, CleanupCategory.Logs));
        await controller.WaitForEntriesAsync(2);

        controller.MaxConcurrent.Should().Be(2);

        controller.ReleaseAll();
        var preparations = await Task.WhenAll(first, second);

        preparations.Should().OnlyContain(preparation => preparation.Status == CleanupPreparationStatus.Ready);
    }

    private static TerminalOperationCoordinator Coordinator(
        ITerminalRegistry registry,
        ITerminalProcessController controller,
        ITerminalCleanupService cleanup,
        IAuditLogger audit) => new(registry, controller, cleanup, audit, StopTimeout);

    private static CleanupRequest Request(Guid terminalId, params CleanupCategory[] categories) =>
        new(terminalId, new HashSet<CleanupCategory>(categories));

    private static TerminalRegistration Registration(Guid id, bool verified = true) =>
        new(id, "Terminal", @"C:\MT5\terminal64.exe", @"C:\MT5\data", @"C:\MT5", [], DiscoverySource.Manual, verified);

    private sealed class FakeRegistry(params TerminalRegistration[] terminals) : ITerminalRegistry
    {
        private readonly List<TerminalRegistration> _terminals = [.. terminals];

        public Task<IReadOnlyList<TerminalRegistration>> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TerminalRegistration>>(_terminals);

        public Task SaveAsync(IReadOnlyList<TerminalRegistration> terminals, CancellationToken cancellationToken = default)
        {
            _terminals.Clear();
            _terminals.AddRange(terminals);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeProcessController : ITerminalProcessController
    {
        public TerminalRuntimeState State { get; set; } = new(TerminalState.Stopped, null, null);
        public Func<bool, StopResult> StopResultFor { get; set; } = _ => new StopResult(StopOutcome.ExitedGracefully, null);
        public Exception? StartException { get; set; }
        public int StateCalls { get; private set; }
        public List<bool> StopForces { get; } = [];
        public List<TimeSpan> StopTimeouts { get; } = [];
        public List<Guid> StartedTerminals { get; } = [];

        public Task<TerminalRuntimeState> GetStateAsync(TerminalRegistration terminal, CancellationToken cancellationToken)
        {
            StateCalls++;
            return Task.FromResult(State);
        }

        public Task<int> StartAsync(TerminalRegistration terminal, CancellationToken cancellationToken)
        {
            StartedTerminals.Add(terminal.Id);
            return StartException is null ? Task.FromResult(4242) : Task.FromException<int>(StartException);
        }

        public Task<StopResult> StopAsync(TerminalRegistration terminal, TimeSpan timeout, bool force, CancellationToken cancellationToken)
        {
            StopForces.Add(force);
            StopTimeouts.Add(timeout);
            return Task.FromResult(StopResultFor(force));
        }
    }

    private sealed class BlockingProcessController : ITerminalProcessController
    {
        private readonly SemaphoreSlim _entries = new(0);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _entryCount;
        private int _concurrent;
        private int _maxConcurrent;

        public int EntryCount => Volatile.Read(ref _entryCount);
        public int MaxConcurrent => Volatile.Read(ref _maxConcurrent);

        public async Task<TerminalRuntimeState> GetStateAsync(TerminalRegistration terminal, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _entryCount);
            var concurrent = Interlocked.Increment(ref _concurrent);
            var observed = Volatile.Read(ref _maxConcurrent);
            while (concurrent > observed &&
                   Interlocked.CompareExchange(ref _maxConcurrent, concurrent, observed) != observed)
                observed = Volatile.Read(ref _maxConcurrent);

            _entries.Release();
            try
            {
                await _release.Task.WaitAsync(cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _concurrent);
            }

            return new TerminalRuntimeState(TerminalState.Stopped, null, null);
        }

        public Task<int> StartAsync(TerminalRegistration terminal, CancellationToken cancellationToken) => Task.FromResult(4242);

        public Task<StopResult> StopAsync(TerminalRegistration terminal, TimeSpan timeout, bool force, CancellationToken cancellationToken) =>
            Task.FromResult(new StopResult(StopOutcome.AlreadyStopped, null));

        public async Task WaitForEntriesAsync(int count)
        {
            for (var index = 0; index < count; index++)
                if (!await _entries.WaitAsync(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException($"Only {index} operations entered the process controller.");
        }

        public void ReleaseAll() => _release.TrySetResult();
    }

    private sealed class FakeCleanupService : ITerminalCleanupService
    {
        public List<IReadOnlySet<CleanupCategory>> Requests { get; } = [];
        public Func<TerminalRegistration, IReadOnlySet<CleanupCategory>, IReadOnlyList<CleanupCategoryResult>>? ResultFactory { get; set; }

        public Task<IReadOnlyList<CleanupCategoryResult>> CleanAsync(
            TerminalRegistration terminal,
            IReadOnlySet<CleanupCategory> categories,
            CancellationToken cancellationToken)
        {
            Requests.Add(categories);
            var results = ResultFactory?.Invoke(terminal, categories) ??
                categories.Select(category => new CleanupCategoryResult(category, 0, 0, [])).ToArray();
            return Task.FromResult<IReadOnlyList<CleanupCategoryResult>>(results);
        }
    }

    private sealed class RecordingAuditLogger : IAuditLogger
    {
        private readonly List<AuditRecord> _records = [];

        public IReadOnlyList<AuditRecord> Records
        {
            get { lock (_records) return _records.ToArray(); }
        }

        public Task AppendAsync(AuditRecord record, CancellationToken cancellationToken = default)
        {
            lock (_records) _records.Add(record);
            return Task.CompletedTask;
        }
    }
}
