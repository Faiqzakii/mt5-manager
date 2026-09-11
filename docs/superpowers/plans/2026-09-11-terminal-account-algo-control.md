# Terminal Account and Algo Trading Control Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Display the live MT5 account and permission state per terminal and safely request a verified global Algo Trading state through the exact terminal window.

**Architecture:** A versioned file bridge written by a bundled MQL5 EA provides volatile runtime snapshots. A read-only inspector validates freshness and data-path identity. A Windows controller finds exactly one process/window, sends Ctrl+E, and confirms the desired state through fresh bridge snapshots. WPF projects these states and commands without persisting account data.

**Tech Stack:** .NET 8, C#, WPF, Win32 window/input APIs, MQL5, JSON, xUnit, FluentAssertions

---

### Task 1: Explicit shortcut data-directory safety

**Files:**
- Modify: `src/Mt5Manager.Infrastructure/Discovery/Mt5DataDirectoryResolver.cs`
- Modify: `tests/Mt5Manager.Infrastructure.Tests/Discovery/Mt5DataDirectoryResolverTests.cs`
- Modify: `tests/Mt5Manager.Infrastructure.Tests/Discovery/TerminalDiscoveryTests.cs`

- [ ] Add a failing regression where shortcut `/datadir:B` conflicts with valid AppData origin A.
- [ ] Confirm resolver incorrectly returns A.
- [ ] Parse existing `WindowsProcessQuery.FindDataDirectory(arguments)` first; accept valid B, and return unresolved without fallback when B is invalid.
- [ ] Run resolver and shortcut discovery tests to green.

### Task 2: Runtime snapshot contracts and reader

**Files:**
- Modify: `src/Mt5Manager.Domain/Models/TerminalModels.cs`
- Modify: `src/Mt5Manager.Application/Abstractions/ITerminalProcessController.cs`
- Create: `src/Mt5Manager.Infrastructure/Runtime/Mt5RuntimeSnapshotReader.cs`
- Create: `tests/Mt5Manager.Infrastructure.Tests/Runtime/Mt5RuntimeSnapshotReaderTests.cs`

- [ ] Add failing tests for valid snapshot, stale snapshot, malformed JSON, unsupported protocol, and mismatched data path.
- [ ] Define `TerminalRuntimeSnapshot`, trade-mode/status enums, and `ITerminalRuntimeInspector`.
- [ ] Implement strict JSON reading keyed by a SHA-256 hash of canonical data path and reject records older than the freshness window.
- [ ] Run reader tests to green.

### Task 3: Verified global Algo Trading controller

**Files:**
- Create: `src/Mt5Manager.Application/Abstractions/ITerminalAlgoTradingController.cs`
- Create: `src/Mt5Manager.Infrastructure/Processes/WindowsTerminalAlgoTradingController.cs`
- Create: `tests/Mt5Manager.Infrastructure.Tests/Processes/WindowsTerminalAlgoTradingControllerTests.cs`

- [ ] Add failing tests for already-desired no-op, ambiguous/missing window refusal, exact Ctrl+E target, observed success, and timeout.
- [ ] Define injectable process-window, foreground-input, clock/delay boundaries.
- [ ] Implement immediate precondition revalidation, exact window selection, Ctrl+E send, and snapshot polling.
- [ ] Run controller tests to green.

### Task 4: WPF account and Algo state

**Files:**
- Modify: `src/Mt5Manager.Wpf/ViewModels/MainViewModels.cs`
- Modify: `src/Mt5Manager.Wpf/MainWindow.xaml`
- Modify: `src/Mt5Manager.Wpf/App.xaml.cs`
- Modify: `tests/Mt5Manager.Wpf.Tests/ViewModels/MainViewModelTests.cs`

- [ ] Add failing view-model tests for account projection, bridge unavailable state, command enablement, no-op, success, and visible failure.
- [ ] Inject runtime inspector/controller, refresh volatile snapshots, expose account/status labels and desired-state commands.
- [ ] Add account and Algo permission panel plus Enable/Disable buttons to each terminal card.
- [ ] Register production implementations in DI and run WPF tests to green.

### Task 5: Bundled MQL5 bridge

**Files:**
- Create: `src/Mt5Manager.Mql5/Experts/Mt5ManagerBridge.mq5`
- Modify: `src/Mt5Manager.Wpf/Mt5Manager.Wpf.csproj`
- Modify: `src/Mt5Manager.Wpf/MainWindow.xaml`

- [ ] Implement timer-based versioned JSON snapshot containing canonical data path, account identity, connection, and four permission layers.
- [ ] Write through `FILE_COMMON` temporary file and replacement-safe final record; never include credentials or accept trading commands.
- [ ] Bundle source with publish output and expose concise installation instructions in UI.
- [ ] Compile-check with MetaEditor when available; otherwise verify source artifact and report the unavailable compiler explicitly.

### Task 6: Complete verification

**Files:**
- Verify: `Mt5Manager.sln`
- Publish: `artifacts/publish/Mt5Manager.Wpf.exe`

- [ ] Run focused tests for discovery, runtime reader/controller, and WPF row behavior.
- [ ] Run `dotnet test Mt5Manager.sln -c Release` with zero failures.
- [ ] Run Release build and win-x64 publish.
- [ ] Smoke the reader/controller against an isolated fake bridge/window boundary; do not touch production MT5 or cleanup.
- [ ] Commit all implementation files and verification-safe documentation.
