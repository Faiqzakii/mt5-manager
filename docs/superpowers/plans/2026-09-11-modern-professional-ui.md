# Modern Professional UI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Transform MT5 Manager into a modern professional-light desktop dashboard without changing its operational behavior.

**Architecture:** Define reusable semantic brushes and control styles in the existing application resource dictionary, then compose the three existing windows from those resources. Keep view-model contracts, event handlers, automation names, modal behavior, and cleanup safety flow unchanged.

**Tech Stack:** .NET 8, WPF XAML, Segoe UI, CommunityToolkit.Mvvm, xUnit, FluentAssertions

---

### Task 1: Shared visual system

**Files:**
- Modify: `src/Mt5Manager.Wpf/App.xaml`

- [ ] Replace ad-hoc styling with semantic color brushes for canvas, surfaces, borders, text, primary, success, warning, and danger roles.
- [ ] Add reusable styles for primary/secondary/danger buttons, text inputs, cards, section titles, check rows, and status panels.
- [ ] Preserve visible keyboard focus, native control semantics, and at least 44 px action height.
- [ ] Build `src/Mt5Manager.Wpf/Mt5Manager.Wpf.csproj` and resolve every XAML resource error.

### Task 2: Main dashboard

**Files:**
- Modify: `src/Mt5Manager.Wpf/MainWindow.xaml`

- [ ] Replace the flat DockPanel with a responsive grid containing product header, toolbar, global feedback, and terminal list.
- [ ] Render terminal rows as modern cards with clear identity, paths, runtime/storage metadata, operation feedback, and separated lifecycle/cleanup actions.
- [ ] Preserve `SearchText`, `RefreshCommand`, `Terminals`, row commands, `Cleanup_Click`, automation names, list virtualization, and window event handlers.
- [ ] Launch the app and confirm terminal cards resize without horizontal clipping at the minimum window size.

### Task 3: Cleanup dialog

**Files:**
- Modify: `src/Mt5Manager.Wpf/Views/CleanupDialog.xaml`

- [ ] Build visually ordered selection, safety confirmation, preview, feedback, progress, and action regions.
- [ ] Keep category bindings, destructive confirmation, busy indicator, all button conditions, click handlers, and cancel behavior unchanged.
- [ ] Confirm the force button is danger-styled and remains collapsed until `RequiresForceConfirmation` is true.

### Task 4: Registration dialog

**Files:**
- Modify: `src/Mt5Manager.Wpf/Views/ManualRegistrationDialog.xaml`

- [ ] Convert the compact two-column form to vertically labeled fields with persistent helper copy.
- [ ] Preserve element names, bindings, validation, `Register_Click`, `IsDefault`, and `IsCancel` behavior.
- [ ] Confirm all fields and actions remain reachable by keyboard in visual order.

### Task 5: Verification and delivery

**Files:**
- Verify: `Mt5Manager.sln`
- Publish: `artifacts/publish/Mt5Manager.Wpf.exe`

- [ ] Run `dotnet test "Mt5Manager.sln" -c Release`; expect 104 passed and 0 failed.
- [ ] Run `dotnet build "Mt5Manager.sln" -c Release --no-restore`; expect 0 warnings and 0 errors.
- [ ] Run `dotnet publish src/Mt5Manager.Wpf/Mt5Manager.Wpf.csproj -p:PublishProfile=win-x64`.
- [ ] Launch the published executable and inspect main window and both modal dialogs through UI Automation for visibility, bounds, clipping, focus order, and enabled states.
- [ ] Close all application processes, remove generated ProgramData smoke state, and commit the completed visual redesign.
