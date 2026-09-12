# Memory and Package Optimization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reduce periodic allocation, disk I/O, retained resources, and deployment size while preserving MT5 Manager behavior.

**Architecture:** Separate lightweight state polling from on-demand storage inspection, serialize periodic refreshes, and reconcile stable rows. Bound synchronization resources by reference-counted leases, dispose process resources through DI, and expose safe framework-dependent and compressed self-contained publish profiles.

**Tech Stack:** .NET 8, WPF, CommunityToolkit.Mvvm, xUnit, FluentAssertions, MSBuild publish profiles.

---

### Task 1: Lightweight and serialized UI refresh

**Files:**
- Modify: `tests/Mt5Manager.Wpf.Tests/ViewModelTests.cs`
- Modify: `src/Mt5Manager.Wpf/ViewModels/MainViewModels.cs`

- [ ] Add tests proving state refresh does not inspect storage, overlapping cycles are skipped, unchanged rows retain identity, and changed registrations replace rows.
- [ ] Run the focused tests and confirm they fail for the missing behavior.
- [ ] Remove storage inspection from `TerminalRowViewModel.RefreshStateAsync`, add a non-blocking refresh-cycle gate, and reconcile discovered rows by registration equality.
- [ ] Run focused WPF tests and confirm they pass.

### Task 2: Bounded coordinator state

**Files:**
- Modify: `tests/Mt5Manager.Application.Tests/Services/TerminalOperationCoordinatorTests.cs`
- Modify: `src/Mt5Manager.Application/Services/TerminalOperationCoordinator.cs`

- [ ] Add a test-facing state-count projection through `InternalsVisibleTo` only if existing assembly conventions permit it; otherwise verify rejected preparations are not retained through observable continuation/cancellation behavior.
- [ ] Run the focused tests and confirm failure.
- [ ] Remove rejected-preparation retention and replace permanent gate entries with reference-counted gate leases that remove only after the last holder/waiter exits.
- [ ] Run coordinator tests and confirm serialization and reservation contracts pass.

### Task 3: Deterministic process resource disposal

**Files:**
- Modify: `tests/Mt5Manager.Infrastructure.Tests/Processes/WindowsTerminalProcessControllerTests.cs`
- Modify: `src/Mt5Manager.Infrastructure/Processes/WindowsTerminalProcessController.cs`

- [ ] Add a test that operations after disposal throw `ObjectDisposedException` and repeated disposal is safe.
- [ ] Run the focused test and confirm failure.
- [ ] Implement idempotent `IDisposable`, detach exit handlers, and dispose tracked processes and semaphore.
- [ ] Run process-controller tests and confirm pass.

### Task 4: Safe small publish profiles

**Files:**
- Modify: `src/Mt5Manager.Wpf/Properties/PublishProfiles/win-x64.pubxml`
- Create: `src/Mt5Manager.Wpf/Properties/PublishProfiles/win-x64-framework-dependent.pubxml`

- [ ] Enable single-file compression and exclude publish symbols in the self-contained profile without trimming.
- [ ] Add a framework-dependent win-x64 profile with Release, no trimming, and no published symbols.
- [ ] Publish both profiles into separate artifact directories.
- [ ] Record exact output sizes and smoke-launch each executable.

### Task 5: Verification and cleanup

**Files:**
- Modify only files required by actual failures.

- [ ] Run all solution tests.
- [ ] Build Release.
- [ ] Remove superseded artifacts and temporary smoke-test resources.
- [ ] Review diagnostics for affected projects and confirm no new errors.
