# Memory and Package Optimization Design

## Objective

Reduce idle and sustained memory, allocation, CPU, and disk-I/O pressure when MT5 Manager runs beside other applications on a small-memory Windows server. Also provide a substantially smaller deployment option without introducing WPF trimming risk.

## Decisions

- Keep the 15-second automatic refresh for process and MT5 runtime state.
- Remove recursive storage inspection from automatic row refresh. Storage is inspected only from the cleanup dialog when requested.
- Prevent overlapping background refresh cycles.
- Reuse `TerminalRowViewModel` instances across discovery refreshes when the registration is unchanged.
- Bound coordinator-owned per-terminal synchronization state without weakening cleanup serialization.
- Dispose tracked process resources deterministically when the application exits.
- Publish two Release packages: framework-dependent and compressed self-contained. Do not enable trimming.

## Runtime Design

### Lightweight periodic refresh

`TerminalRowViewModel.RefreshStateAsync` will read only process state and the small MT5 runtime snapshot. It will not call `ITerminalStorageInspector`. `StorageSummary` remains an explicit not-inspected message in the main list. `CleanupViewModel.LoadPreviewAsync` remains the sole storage scan path.

`MainViewModel.RefreshStatesAsync` will serialize cycles with a non-blocking gate: if a cycle is already running, the next timer tick returns immediately. This prevents queued cycles and duplicate work when a refresh exceeds 15 seconds.

### Stable row identity

Discovery refresh reconciles registrations by `TerminalRegistration.Id`. An existing row is retained when its full registration value is equal; changed or new registrations receive a new row. Removed registrations are dropped. This preserves selection and avoids rebuilding every row on every discovery scan without allowing stale terminal paths or arguments.

### Bounded coordinator state

Replace permanent `Guid -> SemaphoreSlim` entries with reference-counted gate leases. A gate remains in the dictionary while held or awaited and is removed and disposed after the last lease exits. Active cleanup preparations and reservations retain their existing explicit continue/cancel lifetime because automatic expiry during a destructive flow could violate serialization. Rejected preparations no longer need coordinator retention: continuing any rejected preparation can safely reject and audit its immutable snapshot directly, while forged active preparations remain rejected by the active-token validation.

### Process lifecycle

`WindowsTerminalProcessController` implements `IDisposable`. Disposal prevents new operations, removes event handlers, disposes tracked `Process` instances and the controller semaphore. The DI provider already disposes singleton services at application exit.

## Distribution Design

Add two publish profiles:

- `win-x64-framework-dependent.pubxml`: framework-dependent Windows x64 package for servers with .NET 8 Desktop Runtime installed.
- `win-x64-self-contained.pubxml`: compressed self-contained Windows x64 single-file package for portable deployment.

Both use Release, exclude PDB publication, preserve `Mt5ManagerBridge.mq5`, and avoid trimming and ReadyToRun. The existing `win-x64.pubxml` becomes the self-contained compressed profile for compatibility with the current publish command.

## Compatibility and Errors

- Storage preview and cleanup behavior remain unchanged; only automatic scanning is removed.
- A skipped overlapping refresh is not an error and does not alter current state.
- Registration changes replace the affected row so commands always use current paths and arguments.
- Controller calls after disposal throw `ObjectDisposedException` rather than touching disposed handles.
- Framework-dependent output requires the matching .NET 8 Desktop Runtime; self-contained output has no runtime prerequisite.

## Verification

- Unit contracts prove periodic row refresh never invokes storage inspection, concurrent refresh cycles do not overlap, unchanged registrations reuse rows, changed registrations replace rows, and coordinator gates return to zero retained entries after operations.
- Existing cleanup, process, discovery, persistence, and view-model tests remain green.
- Publish both profiles and record output byte totals.
- Launch both published executables and confirm startup. The framework-dependent smoke requires .NET 8 Desktop Runtime on the host.
- Compare the new self-contained size against the existing 154.7 MB executable.
