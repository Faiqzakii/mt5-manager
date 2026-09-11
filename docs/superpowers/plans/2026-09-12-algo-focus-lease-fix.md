# Algo Trading Focus Lease Fix Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Keep the targeted MT5 terminal in the foreground until its bridge snapshot confirms the requested global Algo Trading state, then restore the original foreground window on every exit path.

**Architecture:** Replace the one-shot input boolean with a disposable input lease. Acquiring the lease focuses the exact MT5 window and sends Ctrl+E; disposing it detaches thread input and restores the previous foreground window. The controller owns the lease for the complete snapshot-confirmation loop.

**Tech Stack:** .NET 8, C#, Win32 `SendInput`, xUnit, FluentAssertions

---

### Task 1: Reproduce focus restoration race

**Files:**
- Modify: `tests/Mt5Manager.Infrastructure.Tests/Processes/WindowsTerminalAlgoTradingControllerTests.cs`
- Modify: `src/Mt5Manager.Infrastructure/Processes/WindowsTerminalAlgoTradingController.cs`

- [ ] Add a failing controller test whose input lease records disposal and whose runtime inspector asserts the lease remains active during post-shortcut polling.
- [ ] Run `dotnet test tests/Mt5Manager.Infrastructure.Tests/Mt5Manager.Infrastructure.Tests.csproj -c Release --filter FullyQualifiedName~WindowsTerminalAlgoTradingControllerTests` and verify failure because the current input contract restores focus inside `TrySend` before polling starts.
- [ ] Change `IAlgoTradingInput.TrySend(nint)` to `IAlgoTradingInput.TryAcquire(nint, out IDisposable?)`; make the controller hold `using var lease` across the complete polling loop.
- [ ] Move `AttachThreadInput` detach and original foreground restoration into the production lease's `Dispose`; keep immediate acquisition failures fail-closed and clean up partial acquisition.
- [ ] Run the focused controller tests and verify all pass.

### Task 2: Verify and publish the fix

**Files:**
- Verify: `Mt5Manager.sln`
- Publish: `artifacts/publish/`

- [ ] Run `dotnet test Mt5Manager.sln -c Release` and verify zero failures.
- [ ] Run `dotnet build Mt5Manager.sln -c Release --no-restore` and verify success.
- [ ] Run `dotnet publish src/Mt5Manager.Wpf/Mt5Manager.Wpf.csproj -p:PublishProfile=win-x64` and verify success.
- [ ] Run an isolated controller smoke test showing the lease remains active until the desired snapshot is observed, then is disposed.
- [ ] Remove temporary smoke artifacts and commit the fix.
