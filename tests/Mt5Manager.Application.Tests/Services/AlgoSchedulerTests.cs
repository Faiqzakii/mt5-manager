using FluentAssertions;
using Mt5Manager.Application.Abstractions;
using Mt5Manager.Application.Services;
using Mt5Manager.Domain.Models;

namespace Mt5Manager.Application.Tests.Services;

public sealed class AlgoSchedulerTests
{
    private static readonly DateTimeOffset WednesdayNoon = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Startup_reconciliation_applies_only_the_latest_rule_to_each_terminal()
    {
        var first = Terminal("First");
        var second = Terminal("Second");
        var store = new Store(new([
            Schedule("Asia on", 0, true, [first.Id, second.Id]),
            Schedule("London off", 8, false, [first.Id, second.Id])
        ], []));
        var algo = new Algo();
        var scheduler = Scheduler(store, [first, second], algo);

        await scheduler.RunDueAsync();

        algo.Requests.Should().BeEquivalentTo([
            new AlgoTradingRequest(first.Id, false, AlgoOperationSource.Scheduler),
            new AlgoTradingRequest(second.Id, false, AlgoOperationSource.Scheduler)
        ]);
        store.State.Executions.Should().OnlyContain(item => item.ScheduleName == "London off" && item.Attempt == 1);
    }

    [Fact]
    public async Task Failure_retries_once_after_five_minutes_and_success_stops_notification()
    {
        var terminal = Terminal("Broker");
        var clock = new Clock(WednesdayNoon);
        var algo = new Algo(
            new(false, "bridge unavailable", null),
            new(true, "Algo Trading was disabled.", null));
        var notifier = new Notifier();
        var store = new Store(new([Schedule("London off", 8, false, [terminal.Id])], []));
        var scheduler = Scheduler(store, [terminal], algo, notifier, clock);

        await scheduler.RunDueAsync();
        clock.Now = WednesdayNoon.AddMinutes(4);
        await scheduler.RunDueAsync();
        algo.Requests.Should().ContainSingle();

        clock.Now = WednesdayNoon.AddMinutes(5);
        await scheduler.RunDueAsync();

        algo.Requests.Should().HaveCount(2);
        store.State.Executions.Select(item => item.Attempt).Should().Equal(1, 2);
        store.State.Executions[^1].Outcome.Should().Be(AlgoScheduleExecutionOutcome.Succeeded);
        notifier.Notifications.Should().BeEmpty();
    }

    [Fact]
    public async Task Second_failure_notifies_Telegram_once_and_persists_notification_result()
    {
        var terminal = Terminal("Broker");
        var clock = new Clock(WednesdayNoon);
        var algo = new Algo(new(false, "first failure", null), new(false, "second failure", null));
        var notifier = new Notifier(new(true, "sent"));
        var store = new Store(new([Schedule("US off", 8, false, [terminal.Id])], []));
        var scheduler = Scheduler(store, [terminal], algo, notifier, clock);

        await scheduler.RunDueAsync();
        clock.Now = WednesdayNoon.AddMinutes(5);
        await scheduler.RunDueAsync();
        await scheduler.RunDueAsync();

        notifier.Notifications.Should().ContainSingle().Which.Error.Should().Be("second failure");
        var final = store.State.Executions.OrderBy(item => item.Attempt).Last();
        final.TelegramNotificationAttempted.Should().BeTrue();
        final.TelegramNotificationSent.Should().BeTrue();
        final.TelegramNotificationMessage.Should().Be("sent");
    }

    [Fact]
    public async Task Newer_occurrence_supersedes_a_pending_retry()
    {
        var terminal = Terminal("Broker");
        var clock = new Clock(new(2026, 9, 23, 9, 0, 0, TimeSpan.Zero));
        var algo = new Algo(new(false, "on failed", null), new(true, "off applied", null));
        var store = new Store(new([
            Schedule("Asia on", 8, true, [terminal.Id]),
            Schedule("London off", 9, false, [terminal.Id], minute: 3)
        ], []));
        var scheduler = Scheduler(store, [terminal], algo, clock: clock);

        await scheduler.RunDueAsync();
        clock.Now = new(2026, 9, 23, 9, 4, 0, TimeSpan.Zero);
        await scheduler.RunDueAsync();

        algo.Requests.Select(request => request.Enable).Should().Equal(true, false);
        store.State.Executions.Should().HaveCount(2);
        store.State.Executions.Should().ContainSingle(item => item.ScheduleName == "Asia on" && item.Attempt == 1);
    }

    [Fact]
    public async Task Persisted_success_prevents_duplicate_execution_after_service_restart()
    {
        var terminal = Terminal("Broker");
        var store = new Store(new([Schedule("Midnight on", 0, true, [terminal.Id])], []));
        var algo = new Algo();

        await Scheduler(store, [terminal], algo).RunDueAsync();
        await Scheduler(store, [terminal], algo).RunDueAsync();

        algo.Requests.Should().ContainSingle();
        store.State.Executions.Should().ContainSingle();
    }

    [Fact]
    public void New_revision_does_not_apply_an_occurrence_from_before_its_effective_time()
    {
        var terminal = Terminal("Broker");
        var schedule = Schedule("Morning off", 8, false, [terminal.Id]) with { EffectiveFrom = WednesdayNoon };

        var current = AlgoScheduler.LatestOccurrence([schedule], terminal.Id, WednesdayNoon, TimeZoneInfo.Utc);
        var nextWeek = AlgoScheduler.LatestOccurrence([schedule], terminal.Id, WednesdayNoon.AddDays(7), TimeZoneInfo.Utc);

        current.Should().BeNull();
        nextWeek.Should().NotBeNull();
        nextWeek!.LocalDate.Should().Be(new DateOnly(2026, 9, 30));
    }
    [Fact]
    public void Weekly_rule_before_todays_time_reconciles_the_previous_week_occurrence()
    {
        var terminal = Terminal("Broker");
        var schedule = Schedule("Weekly on", 8, true, [terminal.Id]) with
        {
            EffectiveFrom = WednesdayNoon.AddDays(-14)
        };
        var beforeTodaysOccurrence = new DateTimeOffset(2026, 9, 23, 7, 0, 0, TimeSpan.Zero);

        var occurrence = AlgoScheduler.LatestOccurrence(
            [schedule], terminal.Id, beforeTodaysOccurrence, TimeZoneInfo.Utc);

        occurrence.Should().NotBeNull();
        occurrence!.LocalDate.Should().Be(new DateOnly(2026, 9, 16));
    }


    [Fact]
    public void Conflicting_enabled_rules_for_the_same_terminal_day_and_time_are_rejected()
    {
        var terminal = Terminal("Broker");
        var schedules = new[]
        {
            Schedule("Enable", 8, true, [terminal.Id]),
            Schedule("Disable", 8, false, [terminal.Id])
        };

        var action = () => AlgoScheduler.ValidateConflicts(schedules);

        action.Should().Throw<InvalidOperationException>().WithMessage("*same terminal at the same local time*");
    }

    private static AlgoScheduler Scheduler(
        Store store,
        IReadOnlyList<TerminalRegistration> terminals,
        Algo algo,
        Notifier? notifier = null,
        Clock? clock = null) => new(
            store,
            new Registry(terminals),
            algo,
            notifier ?? new Notifier(),
            clock ?? new Clock(WednesdayNoon),
            TimeZoneInfo.Utc,
            AlgoScheduler.DefaultRetryDelay,
            TimeSpan.FromHours(1));

    private static AlgoSchedule Schedule(
        string name,
        int hour,
        bool enable,
        IReadOnlyList<Guid> terminalIds,
        int minute = 0) => new(
            Guid.NewGuid(),
            name,
            new TimeOnly(hour, minute),
            AlgoScheduleDays.Wednesday,
            enable,
            terminalIds,
            true,
            WednesdayNoon.AddDays(-7),
            1);

    private static TerminalRegistration Terminal(string name) => new(
        Guid.NewGuid(), name, $@"C:\{name}\terminal64.exe", $@"C:\{name}\Data", $@"C:\{name}", [], DiscoverySource.Manual, true);

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Store(AlgoScheduleState state) : IAlgoScheduleStore
    {
        public AlgoScheduleState State { get; private set; } = state;
        public Task<AlgoScheduleState> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(State);
        public Task SaveAsync(AlgoScheduleState state, CancellationToken cancellationToken = default)
        {
            State = state;
            return Task.CompletedTask;
        }
    }

    private sealed class Registry(IReadOnlyList<TerminalRegistration> items) : ITerminalRegistry
    {
        public Task<IReadOnlyList<TerminalRegistration>> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(items);
        public Task SaveAsync(IReadOnlyList<TerminalRegistration> terminals, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class Algo(params AlgoTradingControlResult[] results) : IAlgoTradingService
    {
        private readonly Queue<AlgoTradingControlResult> results = new(results);
        public List<AlgoTradingRequest> Requests { get; } = [];
        public Task<AlgoTradingOperationResult> SetAsync(AlgoTradingRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var result = results.Count > 0 ? results.Dequeue() : new(true, "changed", null);
            return Task.FromResult(new AlgoTradingOperationResult(request.TerminalId, "Terminal", request.Enable, result));
        }
    }

    private sealed class Notifier(AlgoScheduleNotificationResult? result = null) : IAlgoScheduleFailureNotifier
    {
        public List<AlgoScheduleFailureNotification> Notifications { get; } = [];
        public Task<AlgoScheduleNotificationResult> NotifyAsync(AlgoScheduleFailureNotification notification, CancellationToken cancellationToken = default)
        {
            Notifications.Add(notification);
            return Task.FromResult(result ?? new AlgoScheduleNotificationResult(true, "sent"));
        }
    }
}
