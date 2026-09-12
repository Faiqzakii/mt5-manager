using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Mt5Manager.Application.Abstractions;
using Mt5Manager.Application.Services;
using Mt5Manager.Domain.Models;

namespace Mt5Manager.Wpf.ViewModels;

public sealed partial class CleanupCategoryViewModel(CleanupCategory category):ObservableObject { public CleanupCategory Category=>category; [ObservableProperty] bool isSelected; public string Name=>category.ToString(); }
public sealed partial class CleanupViewModel:ObservableObject
{
    readonly TerminalRegistration terminal; readonly ITerminalStorageInspector inspector; readonly TerminalOperationCoordinator coordinator; readonly Action<string>? cleanupCompleted; readonly Func<Task>? refreshStorage; CleanupPreparation? preparation; CancellationTokenSource preparationCancellation=new();
    public CleanupViewModel(TerminalRegistration terminal,ITerminalStorageInspector inspector,TerminalOperationCoordinator coordinator,Action<string>? cleanupCompleted=null,Func<Task>? refreshStorage=null){this.terminal=terminal;this.inspector=inspector;this.coordinator=coordinator;this.cleanupCompleted=cleanupCompleted;this.refreshStorage=refreshStorage;Categories=[..Enum.GetValues<CleanupCategory>().Select(x=>new CleanupCategoryViewModel(x))];foreach(var c in Categories)c.PropertyChanged+=(_,_)=>OnPropertyChanged(nameof(CanPrepare));}
    public IReadOnlyList<CleanupCategoryViewModel> Categories{get;} public bool CanPrepare=>terminal.DataDirectoryVerified&&preparation is null&&Categories.Any(x=>x.IsSelected)&&IsDestructiveConfirmed&&!IsBusy; public bool CanContinue=>preparation?.Status==CleanupPreparationStatus.Ready&&!IsBusy; public bool CanForceContinue=>preparation is not null&&!IsBusy;
    [ObservableProperty] string preview="Select categories to preview cleanup."; [ObservableProperty] bool requiresForceConfirmation; [ObservableProperty] string resultText=""; [ObservableProperty] string? error;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(CanPrepare))] bool isDestructiveConfirmed; [ObservableProperty][NotifyPropertyChangedFor(nameof(CanPrepare),nameof(CanContinue),nameof(CanForceContinue))] bool isBusy;
    public async Task LoadPreviewAsync(){if(!terminal.DataDirectoryVerified){Error="Cleanup requires a verified terminal data directory.";return;}try{IsBusy=true;var usage=await inspector.InspectAsync(terminal,preparationCancellation.Token);Preview=FormatUsage(usage);}catch(OperationCanceledException){}catch(Exception ex){Error=ex.Message;}finally{IsBusy=false;}}
    public async Task PrepareAsync()
    {
        if(!CanPrepare)return;
        try
        {
            IsBusy=true;Error=null;
            var issued=await coordinator.PrepareCleanupAsync(new(terminal.Id,Categories.Where(x=>x.IsSelected).Select(x=>x.Category).ToHashSet()),preparationCancellation.Token);
            if(preparationCancellation.IsCancellationRequested){await coordinator.CancelPreparationAsync(issued);return;}
            if(issued.Status==CleanupPreparationStatus.Rejected)
            {
                // A rejected preparation is a dead end, not an operation: drop it together with its
                // reservation so the user can fix the cause and prepare again without closing the dialog.
                Error=issued.Message;
                await coordinator.CancelPreparationAsync(issued);
                return;
            }
            SetPreparation(issued);
            RequiresForceConfirmation=issued.Status==CleanupPreparationStatus.RequiresForceConfirmation;
        }
        catch(OperationCanceledException){}
        catch(Exception ex){Error=ex.Message;}
        finally{IsBusy=false;}
    }
    public async Task ContinueAsync(bool force)
    {
        if(preparation is null||IsBusy)return;
        try
        {
            IsBusy=true;
            var outcome=await coordinator.ContinueCleanupAsync(preparation,force,preparationCancellation.Token);
            var failures=outcome.Result.Categories.Sum(x=>x.Failures.Count);
            ResultText=$"{outcome.Status}: {outcome.Result.Categories.Sum(x=>x.DeletedFiles)} deleted, {failures} failed. "+(outcome.Result.Restarted?"Restarted.":outcome.Result.RestartError is null?"Not restarted.":$"Restart failed: {outcome.Result.RestartError}");
            cleanupCompleted?.Invoke(ResultText);if(refreshStorage is not null)await refreshStorage();
            Error=outcome.Message;
        }
        catch(OperationCanceledException){}
        catch(Exception ex){Error=ex.Message;}
        finally{SetPreparation(null);IsBusy=false;}
    }
    public async Task CancelPreparationAsync(){preparationCancellation.Cancel();var pending=preparation;if(pending is not null)await coordinator.CancelPreparationAsync(pending);SetPreparation(null);}
    void SetPreparation(CleanupPreparation? value){preparation=value;if(value is null)RequiresForceConfirmation=false;OnPropertyChanged(nameof(CanContinue));OnPropertyChanged(nameof(CanPrepare));OnPropertyChanged(nameof(CanForceContinue));}
    static string FormatUsage(IReadOnlyList<CategoryUsage> usage)=>string.Join(Environment.NewLine,usage.Select(x=>$"{x.Category}: {x.FileCount} files, {x.Bytes:N0} bytes"));
}

public sealed partial class ManualRegistrationViewModel(Func<string,bool>? fileExists=null,Func<string,bool>? directoryExists=null):ObservableObject
{
    readonly Func<string,bool> fileExists=fileExists??File.Exists; readonly Func<string,bool> directoryExists=directoryExists??Directory.Exists;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(IsValid),nameof(ValidationMessage))] string displayName="";
    [ObservableProperty][NotifyPropertyChangedFor(nameof(IsValid),nameof(ValidationMessage))] string executablePath="";
    [ObservableProperty][NotifyPropertyChangedFor(nameof(IsValid),nameof(ValidationMessage))] string dataDirectory="";
    public bool IsValid=>!string.IsNullOrWhiteSpace(DisplayName)&&fileExists(ExecutablePath)&&directoryExists(DataDirectory);
    public string ValidationMessage=>IsValid?"Ready to register.":"Enter a name, an existing terminal executable, and an existing data directory.";
    public TerminalRegistration CreateRegistration(){if(!IsValid)throw new InvalidOperationException(ValidationMessage);var exe=Path.GetFullPath(ExecutablePath);var data=Path.GetFullPath(DataDirectory);return new(Guid.NewGuid(),DisplayName.Trim(),exe,data,Path.GetDirectoryName(exe)!,[],DiscoverySource.Manual,true);}
}
