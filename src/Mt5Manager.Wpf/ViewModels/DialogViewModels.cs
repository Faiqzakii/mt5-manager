using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Mt5Manager.Application.Abstractions;
using Mt5Manager.Application.Services;
using Mt5Manager.Domain.Models;

namespace Mt5Manager.Wpf.ViewModels;

public sealed partial class CleanupCategoryViewModel(CleanupCategory category):ObservableObject { public CleanupCategory Category=>category; [ObservableProperty] bool isSelected; public string Name=>category.ToString(); }
public sealed partial class CleanupViewModel:ObservableObject
{
    readonly TerminalRegistration terminal; readonly ITerminalStorageInspector inspector; readonly TerminalOperationCoordinator coordinator; CleanupPreparation? preparation; CancellationTokenSource preparationCancellation=new();
    public CleanupViewModel(TerminalRegistration terminal,ITerminalStorageInspector inspector,TerminalOperationCoordinator coordinator){this.terminal=terminal;this.inspector=inspector;this.coordinator=coordinator;Categories=[..Enum.GetValues<CleanupCategory>().Select(x=>new CleanupCategoryViewModel(x))];foreach(var c in Categories)c.PropertyChanged+=(_,_)=>OnPropertyChanged(nameof(CanPrepare));}
    public IReadOnlyList<CleanupCategoryViewModel> Categories{get;} public bool CanPrepare=>terminal.DataDirectoryVerified&&Categories.Any(x=>x.IsSelected)&&IsDestructiveConfirmed&&!IsBusy; public bool CanContinue=>preparation?.Status==CleanupPreparationStatus.Ready&&!IsBusy;
    [ObservableProperty] string preview="Select categories to preview cleanup."; [ObservableProperty] bool requiresForceConfirmation; [ObservableProperty] string resultText=""; [ObservableProperty] string? error;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(CanPrepare))] bool isDestructiveConfirmed; [ObservableProperty][NotifyPropertyChangedFor(nameof(CanPrepare))] bool isBusy;
    public async Task LoadPreviewAsync(){if(!terminal.DataDirectoryVerified){Error="Cleanup requires a verified terminal data directory.";return;}try{IsBusy=true;var usage=await inspector.InspectAsync(terminal,preparationCancellation.Token);Preview=string.Join(Environment.NewLine,usage.Select(x=>$"{x.Category}: {x.FileCount} files, {x.Bytes:N0} bytes"));}catch(Exception ex)when(ex is not OperationCanceledException){Error=ex.Message;}finally{IsBusy=false;}}
    public async Task PrepareAsync(){if(!CanPrepare)return;try{IsBusy=true;Error=null;preparation=await coordinator.PrepareCleanupAsync(new(terminal.Id,Categories.Where(x=>x.IsSelected).Select(x=>x.Category).ToHashSet()),preparationCancellation.Token);RequiresForceConfirmation=preparation.Status==CleanupPreparationStatus.RequiresForceConfirmation;if(preparation.Status==CleanupPreparationStatus.Rejected)Error=preparation.Message;}catch(Exception ex)when(ex is not OperationCanceledException){Error=ex.Message;}finally{IsBusy=false;OnPropertyChanged(nameof(CanContinue));}}
    public async Task ContinueAsync(bool force){if(preparation is null)return;try{IsBusy=true;var outcome=await coordinator.ContinueCleanupAsync(preparation,force,preparationCancellation.Token);preparation=null;var failures=outcome.Result.Categories.Sum(x=>x.Failures.Count);ResultText=$"{outcome.Status}: {outcome.Result.Categories.Sum(x=>x.DeletedFiles)} deleted, {failures} failed. "+(outcome.Result.Restarted?"Restarted.":outcome.Result.RestartError is null?"Not restarted.":$"Restart failed: {outcome.Result.RestartError}");Error=outcome.Message;}catch(Exception ex)when(ex is not OperationCanceledException){Error=ex.Message;}finally{IsBusy=false;}}
    public async Task CancelPreparationAsync(){preparationCancellation.Cancel();if(preparation is not null)await coordinator.CancelPreparationAsync(preparation);preparation=null;}
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
