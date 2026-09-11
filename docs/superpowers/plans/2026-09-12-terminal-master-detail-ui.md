# Terminal Master–Detail UI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace stacked terminal control cards with a compact terminal navigator and a dominant selected-terminal control pane.

**Architecture:** `MainViewModel` owns stable selected-terminal state while continuing to expose filtered rows. `MainWindow.xaml` renders `Terminals` as a compact left `ListBox` and binds a separate right detail surface to `SelectedTerminal`; only those two bounded panes scroll vertically.

**Tech Stack:** .NET 8, C#, WPF/XAML, CommunityToolkit.Mvvm, xUnit, FluentAssertions

---

### Task 1: Stable terminal selection

**Files:**
- Modify: `tests/Mt5Manager.Wpf.Tests/ViewModelTests.cs`
- Modify: `src/Mt5Manager.Wpf/ViewModels/MainViewModels.cs`

- [ ] Add a failing test that refresh selects the first terminal, preserves selection by registration ID after rescan, falls back to the first visible row when filtering excludes it, and clears selection for an empty result.
- [ ] Run `dotnet test tests/Mt5Manager.Wpf.Tests/Mt5Manager.Wpf.Tests.csproj -c Release --filter FullyQualifiedName~Main_selection` and verify failure because `SelectedTerminal` does not exist.
- [ ] Add `[ObservableProperty] TerminalRowViewModel? selectedTerminal`; preserve the previous `Terminal.Id` across discovery and normalize selection in `ApplyFilter`.
- [ ] Run the focused selection tests and verify they pass.

### Task 2: Master–detail window

**Files:**
- Modify: `src/Mt5Manager.Wpf/MainWindow.xaml`
- Verify: `src/Mt5Manager.Wpf/MainWindow.xaml.cs`

- [ ] Replace the full-card `ListView` with a two-column `Grid`: left `ListBox` bound to `Terminals` and `SelectedTerminal`, 250px minimum; 6px splitter; dominant right column with 430px minimum.
- [ ] Render compact left rows with display name, state/PID, `AccountSummary`, and `AlgoSummary`; keep only this list's scrollbar.
- [ ] Move all operational controls into a right detail `ContentControl` bound to `SelectedTerminal`, retaining all existing commands, paths, storage, cleanup, results, errors, and bridge guidance.
- [ ] Add a null-selection empty state and one vertical detail `ScrollViewer` with horizontal scrolling disabled.
- [ ] Keep `Cleanup_Click` row-bound through the detail content's data context.

### Task 3: Verification

**Files:**
- Verify: `Mt5Manager.sln`
- Publish: `artifacts/publish/`

- [ ] Run `dotnet test Mt5Manager.sln -c Release` with zero failures.
- [ ] Run `dotnet build Mt5Manager.sln -c Release --no-restore` successfully.
- [ ] Run `dotnet publish src/Mt5Manager.Wpf/Mt5Manager.Wpf.csproj -p:PublishProfile=win-x64` successfully.
- [ ] Launch a fixture-backed WPF harness at 1180 × 760 and 820 × 560; inspect screenshots and exercise list selection plus visible terminal actions without touching production MT5.
- [ ] Confirm no clipped or unreachable Start, Stop, Restart, Algo, or Cleanup controls and no nested list-card scrollbar.
- [ ] Remove temporary harness artifacts and commit the verified change.
