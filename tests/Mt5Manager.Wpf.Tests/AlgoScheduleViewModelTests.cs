using FluentAssertions;
using Mt5Manager.Application.Abstractions;
using Mt5Manager.Domain.Models;
using Mt5Manager.Wpf.ViewModels;

namespace Mt5Manager.Wpf.Tests;

public sealed class AlgoScheduleViewModelTests
{
    [Fact]
    public async Task Load_projects_schedule_targets_days_runtime_and_history()
    {
        var terminal = Terminal("Broker");
        var schedule = Schedule(terminal.Id);
        var execution = Execution(schedule, terminal);
        var scheduler = new Scheduler
        {
            Snapshot = new([schedule], [execution], [terminal], true,
                new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero), null, "UTC")
        };
        var viewModel = new AlgoScheduleViewModel(scheduler);

        await viewModel.LoadAsync();

        viewModel.SelectedSchedule!.Schedule.Should().Be(schedule);
        viewModel.Name.Should().Be("London off");
        viewModel.TimeText.Should().Be("08:30");
        viewModel.Disable.Should().BeTrue();
        viewModel.Days.Single(day => day.Day == AlgoScheduleDays.Wednesday).IsSelected.Should().BeTrue();
        viewModel.Terminals.Should().ContainSingle().Which.IsSelected.Should().BeTrue();
        viewModel.History.Should().ContainSingle().Which.Notification.Should().Be("Sent");
        viewModel.Status.Should().ContainAll("running", "Last checked");
        viewModel.TimeZoneDisplayName.Should().Be("UTC");
    }

    [Fact]
    public async Task New_schedule_defaults_to_weekdays_and_requires_a_terminal()
    {
        var terminal = Terminal("Broker");
        var viewModel = new AlgoScheduleViewModel(new Scheduler
        {
            Snapshot = new([], [], [terminal], true, null, null, "UTC")
        });
        await viewModel.LoadAsync();
        viewModel.NewSchedule();
        viewModel.Name = "Asia on";
        viewModel.TimeText = "00:00";

        viewModel.Days.Where(day => day.IsSelected).Select(day => day.Day)
            .Should().BeEquivalentTo([
                AlgoScheduleDays.Monday,
                AlgoScheduleDays.Tuesday,
                AlgoScheduleDays.Wednesday,
                AlgoScheduleDays.Thursday,
                AlgoScheduleDays.Friday]);
        viewModel.CanSave.Should().BeFalse();
        viewModel.Terminals[0].IsSelected = true;
        viewModel.CanSave.Should().BeTrue();
    }

    [Fact]
    public async Task Save_projects_explicit_time_days_action_and_multiple_terminals()
    {
        var first = Terminal("First");
        var second = Terminal("Second");
        var scheduler = new Scheduler
        {
            Snapshot = new([], [], [first, second], true, null, null, "UTC")
        };
        var viewModel = new AlgoScheduleViewModel(scheduler);
        await viewModel.LoadAsync();
        viewModel.NewSchedule();
        viewModel.Name = "US off";
        viewModel.TimeText = "21:15";
        viewModel.Enable = false;
        foreach (var day in viewModel.Days) day.IsSelected = day.Day is AlgoScheduleDays.Monday or AlgoScheduleDays.Friday;
        foreach (var terminal in viewModel.Terminals) terminal.IsSelected = true;

        await viewModel.SaveAsync();

        var saved = scheduler.Saved.Should().ContainSingle().Subject;
        saved.Name.Should().Be("US off");
        saved.LocalTime.Should().Be(new TimeOnly(21, 15));
        saved.Enable.Should().BeFalse();
        saved.Days.Should().Be(AlgoScheduleDays.Monday | AlgoScheduleDays.Friday);
        saved.TerminalIds.Should().BeEquivalentTo([first.Id, second.Id]);
    }

    [Fact]
    public async Task Invalid_time_is_rejected_before_calling_scheduler()
    {
        var terminal = Terminal("Broker");
        var scheduler = new Scheduler { Snapshot = new([], [], [terminal], true, null, null, "UTC") };
        var viewModel = new AlgoScheduleViewModel(scheduler);
        await viewModel.LoadAsync();
        viewModel.Name = "Broken";
        viewModel.TimeText = "25:99";
        viewModel.Terminals[0].IsSelected = true;

        await viewModel.SaveAsync();

        scheduler.Saved.Should().BeEmpty();
        viewModel.Error.Should().Contain("HH:mm");
    }

    [Fact]
    public async Task Scheduler_conflict_is_exposed_without_losing_editor_state()
    {
        var terminal = Terminal("Broker");
        var scheduler = new Scheduler
        {
            Snapshot = new([], [], [terminal], true, null, null, "UTC"),
            SaveError = new InvalidOperationException("Schedules conflict")
        };
        var viewModel = new AlgoScheduleViewModel(scheduler);
        await viewModel.LoadAsync();
        viewModel.Name = "London off";
        viewModel.TimeText = "08:00";
        viewModel.Terminals[0].IsSelected = true;

        await viewModel.SaveAsync();

        viewModel.Error.Should().Be("Schedules conflict");
        viewModel.Name.Should().Be("London off");
    }

    private static TerminalRegistration Terminal(string name) => new(
        Guid.NewGuid(), name, $@"C:\{name}\terminal64.exe", $@"C:\{name}\Data", $@"C:\{name}", [], DiscoverySource.Manual, true);

    private static AlgoSchedule Schedule(Guid terminalId) => new(
        Guid.NewGuid(), "London off", new TimeOnly(8, 30), AlgoScheduleDays.Wednesday, false,
        [terminalId], true, new DateTimeOffset(2026, 9, 20, 0, 0, 0, TimeSpan.Zero), 1);

    private static AlgoScheduleExecution Execution(AlgoSchedule schedule, TerminalRegistration terminal) => new(
        Guid.NewGuid(), schedule.Id, schedule.Revision, schedule.Name, new DateOnly(2026, 9, 23),
        new DateTimeOffset(2026, 9, 23, 8, 30, 0, TimeSpan.Zero), terminal.Id, terminal.DisplayName,
        false, 2, new DateTimeOffset(2026, 9, 23, 8, 35, 0, TimeSpan.Zero),
        AlgoScheduleExecutionOutcome.Failed, "bridge unavailable", true, true, "sent");

    private sealed class Scheduler : IAlgoScheduler
    {
        public AlgoSchedulerSnapshot Snapshot { get; set; } = new([], [], [], true, null, null, "UTC");
        public Exception? SaveError { get; init; }
        public List<AlgoSchedule> Saved { get; } = [];
        public bool IsRunning => Snapshot.IsRunning;
        public DateTimeOffset? LastEvaluationAt => Snapshot.LastEvaluationAt;
        public string? LastError => Snapshot.LastError;
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<AlgoSchedulerSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) => Task.FromResult(Snapshot);
        public Task<AlgoSchedule> SaveAsync(AlgoSchedule schedule, CancellationToken cancellationToken = default)
        {
            if (SaveError is not null) throw SaveError;
            var saved = schedule with { Revision = schedule.Revision + 1, EffectiveFrom = DateTimeOffset.UtcNow };
            Saved.Add(saved);
            Snapshot = Snapshot with { Schedules = Snapshot.Schedules.Where(item => item.Id != saved.Id).Append(saved).ToArray() };
            return Task.FromResult(saved);
        }
        public Task RemoveAsync(Guid scheduleId, CancellationToken cancellationToken = default)
        {
            Snapshot = Snapshot with { Schedules = Snapshot.Schedules.Where(item => item.Id != scheduleId).ToArray() };
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
