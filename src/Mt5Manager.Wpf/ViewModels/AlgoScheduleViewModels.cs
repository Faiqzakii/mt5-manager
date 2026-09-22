using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Mt5Manager.Application.Abstractions;
using Mt5Manager.Domain.Models;

namespace Mt5Manager.Wpf.ViewModels;

public sealed partial class AlgoScheduleTerminalChoiceViewModel(TerminalRegistration terminal) : ObservableObject
{
    public TerminalRegistration Terminal { get; } = terminal;
    public string Name => Terminal.DisplayName;
    [ObservableProperty] private bool isSelected;
}

public sealed partial class AlgoScheduleDayChoiceViewModel(AlgoScheduleDays day, string label) : ObservableObject
{
    public AlgoScheduleDays Day { get; } = day;
    public string Label { get; } = label;
    [ObservableProperty] private bool isSelected;
}

public sealed class AlgoScheduleListItemViewModel(
    AlgoSchedule schedule,
    IReadOnlyDictionary<Guid, string> terminalNames)
{
    public AlgoSchedule Schedule { get; } = schedule;
    public string Name => Schedule.Name;
    public string Time => Schedule.LocalTime.ToString("HH:mm", CultureInfo.InvariantCulture);
    public string Action => Schedule.Enable ? "ON" : "OFF";
    public string State => Schedule.IsEnabled ? "Enabled" : "Paused";
    public string Days => FormatDays(Schedule.Days);
    public string Terminals => string.Join(", ", Schedule.TerminalIds.Select(id =>
        terminalNames.TryGetValue(id, out var name) ? name : "Missing terminal"));

    private static string FormatDays(AlgoScheduleDays days)
    {
        if (days == AlgoScheduleDays.EveryDay) return "Every day";
        if (days == AlgoScheduleDays.Weekdays) return "Weekdays";
        return string.Join(", ", DayLabels.Where(item => (days & item.Day) != 0).Select(item => item.Short));
    }

    private static readonly (AlgoScheduleDays Day, string Short)[] DayLabels =
    [
        (AlgoScheduleDays.Monday, "Mon"),
        (AlgoScheduleDays.Tuesday, "Tue"),
        (AlgoScheduleDays.Wednesday, "Wed"),
        (AlgoScheduleDays.Thursday, "Thu"),
        (AlgoScheduleDays.Friday, "Fri"),
        (AlgoScheduleDays.Saturday, "Sat"),
        (AlgoScheduleDays.Sunday, "Sun")
    ];
}

public sealed class AlgoScheduleExecutionViewModel(AlgoScheduleExecution execution)
{
    public DateTimeOffset AttemptedAt => execution.AttemptedAt.ToLocalTime();
    public string Schedule => execution.ScheduleName;
    public string Terminal => execution.TerminalName;
    public string Action => execution.Enable ? "ON" : "OFF";
    public int Attempt => execution.Attempt;
    public bool Succeeded => execution.Outcome == AlgoScheduleExecutionOutcome.Succeeded;
    public string Outcome => Succeeded ? "Succeeded" : "Failed";
    public string Message => execution.Message;
    public string Notification => !execution.TelegramNotificationAttempted
        ? "—"
        : execution.TelegramNotificationSent ? "Sent" : execution.TelegramNotificationMessage ?? "Not sent";
}

public sealed partial class AlgoScheduleViewModel : ObservableObject
{
    private readonly IAlgoScheduler scheduler;
    private bool loadingEditor;

    public AlgoScheduleViewModel(IAlgoScheduler scheduler)
    {
        this.scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        Days =
        [
            new(AlgoScheduleDays.Monday, "Mon"),
            new(AlgoScheduleDays.Tuesday, "Tue"),
            new(AlgoScheduleDays.Wednesday, "Wed"),
            new(AlgoScheduleDays.Thursday, "Thu"),
            new(AlgoScheduleDays.Friday, "Fri"),
            new(AlgoScheduleDays.Saturday, "Sat"),
            new(AlgoScheduleDays.Sunday, "Sun")
        ];
        foreach (var day in Days) day.PropertyChanged += (_, _) => OnPropertyChanged(nameof(CanSave));
    }

    public ObservableCollection<AlgoScheduleListItemViewModel> Schedules { get; } = [];
    public ObservableCollection<AlgoScheduleTerminalChoiceViewModel> Terminals { get; } = [];
    public ObservableCollection<AlgoScheduleDayChoiceViewModel> Days { get; }
    public ObservableCollection<AlgoScheduleExecutionViewModel> History { get; } = [];

    [ObservableProperty] private AlgoScheduleListItemViewModel? selectedSchedule;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(CanSave))] private string name = "";
    [ObservableProperty][NotifyPropertyChangedFor(nameof(CanSave))] private string timeText = "00:00";
    [ObservableProperty] private bool enable = true;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(CanSave))] private bool isEnabled = true;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(CanSave), nameof(CanInteract))] private bool isBusy;
    [ObservableProperty] private string? error;
    [ObservableProperty] private string? status;
    [ObservableProperty] private string timeZoneDisplayName = "Local Windows time";

    public bool Disable
    {
        get => !Enable;
        set => Enable = !value;
    }

    public bool CanSave => !IsBusy &&
        !string.IsNullOrWhiteSpace(Name) &&
        TimeOnly.TryParseExact(TimeText, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) &&
        Days.Any(day => day.IsSelected) &&
        Terminals.Any(terminal => terminal.IsSelected);
    public bool CanDelete => !IsBusy && SelectedSchedule is not null;
    public bool CanInteract => !IsBusy;
    partial void OnEnableChanged(bool value) => OnPropertyChanged(nameof(Disable));
    partial void OnSelectedScheduleChanged(AlgoScheduleListItemViewModel? value)
    {
        OnPropertyChanged(nameof(CanDelete));
        if (!loadingEditor) LoadEditor(value?.Schedule);
    }
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanDelete));

    public async Task LoadAsync(Guid? selectedId = null, CancellationToken cancellationToken = default)
    {
        if (IsBusy) return;
        IsBusy = true;
        Error = null;
        try
        {
            var snapshot = await scheduler.GetSnapshotAsync(cancellationToken);
            TimeZoneDisplayName = snapshot.TimeZoneDisplayName;
            var names = snapshot.Terminals.ToDictionary(terminal => terminal.Id, terminal => terminal.DisplayName);
            var targetId = selectedId ?? SelectedSchedule?.Schedule.Id;

            loadingEditor = true;
            try
            {
                Schedules.Clear();
                foreach (var schedule in snapshot.Schedules)
                    Schedules.Add(new(schedule, names));
                SelectedSchedule = targetId is null
                    ? Schedules.FirstOrDefault()
                    : Schedules.FirstOrDefault(item => item.Schedule.Id == targetId) ?? Schedules.FirstOrDefault();

                Terminals.Clear();
                foreach (var terminal in snapshot.Terminals)
                {
                    var choice = new AlgoScheduleTerminalChoiceViewModel(terminal);
                    choice.PropertyChanged += (_, _) => OnPropertyChanged(nameof(CanSave));
                    Terminals.Add(choice);
                }

                History.Clear();
                foreach (var execution in snapshot.Executions.Take(100)) History.Add(new(execution));
            }
            finally
            {
                loadingEditor = false;
            }

            LoadEditor(SelectedSchedule?.Schedule);
            var evaluated = snapshot.LastEvaluationAt is null
                ? "No evaluation recorded yet"
                : $"Last checked {snapshot.LastEvaluationAt.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
            Status = snapshot.LastError is null
                ? $"Scheduler {(snapshot.IsRunning ? "running" : "stopped")} · {evaluated}"
                : $"Scheduler error: {snapshot.LastError}";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Error = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void NewSchedule()
    {
        if (IsBusy) return;
        loadingEditor = true;
        SelectedSchedule = null;
        loadingEditor = false;
        LoadEditor(null);
    }
    public void SetAllTerminals(bool selected)
    {
        if (IsBusy) return;
        foreach (var terminal in Terminals) terminal.IsSelected = selected;
    }


    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        if (!CanSave)
        {
            Error = "Enter a name and HH:mm time, then select at least one day and terminal.";
            return;
        }

        IsBusy = true;
        Error = null;
        try
        {
            var schedule = new AlgoSchedule(
                SelectedSchedule?.Schedule.Id ?? Guid.NewGuid(),
                Name,
                TimeOnly.ParseExact(TimeText, "HH:mm", CultureInfo.InvariantCulture),
                Days.Where(day => day.IsSelected).Aggregate(AlgoScheduleDays.None, (current, day) => current | day.Day),
                Enable,
                Terminals.Where(terminal => terminal.IsSelected).Select(terminal => terminal.Terminal.Id).ToArray(),
                IsEnabled,
                SelectedSchedule?.Schedule.EffectiveFrom ?? default,
                SelectedSchedule?.Schedule.Revision ?? 0);
            var saved = await scheduler.SaveAsync(schedule, cancellationToken);
            await ReloadAfterMutationAsync(saved.Id, cancellationToken);
            Status = $"Schedule '{saved.Name}' saved. It starts with its next occurrence.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Error = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        if (!CanDelete) return;
        var schedule = SelectedSchedule!.Schedule;
        IsBusy = true;
        Error = null;
        try
        {
            await scheduler.RemoveAsync(schedule.Id, cancellationToken);
            await ReloadAfterMutationAsync(null, cancellationToken);
            Status = $"Schedule '{schedule.Name}' deleted.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Error = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ReloadAfterMutationAsync(Guid? selectedId, CancellationToken cancellationToken)
    {
        IsBusy = false;
        await LoadAsync(selectedId, cancellationToken);
        IsBusy = true;
    }

    private void LoadEditor(AlgoSchedule? schedule)
    {
        loadingEditor = true;
        try
        {
            Name = schedule?.Name ?? "";
            TimeText = schedule?.LocalTime.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "00:00";
            Enable = schedule?.Enable ?? true;
            IsEnabled = schedule?.IsEnabled ?? true;
            foreach (var day in Days)
                day.IsSelected = schedule is null
                    ? (AlgoScheduleDays.Weekdays & day.Day) != 0
                    : (schedule.Days & day.Day) != 0;
            foreach (var terminal in Terminals)
                terminal.IsSelected = schedule?.TerminalIds.Contains(terminal.Terminal.Id) == true;
        }
        finally
        {
            loadingEditor = false;
        }
        OnPropertyChanged(nameof(CanSave));
    }
}
