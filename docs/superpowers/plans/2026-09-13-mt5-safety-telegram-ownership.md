# MT5 Safety and Telegram Ownership Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fail closed for destructive terminal control, disclose Global Algo Trading impact, stop duplicate Telegram consumers, and add optional public IPv4 dashboard metadata.

**Architecture:** Keep safety enforcement at both WPF command availability and the process-controller boundary. Extend Telegram’s typed error/state model for HTTP 409, and inject a small cached IPv4 provider into the bot service so external lookup failure cannot affect dashboard delivery.

**Tech Stack:** .NET 8, C#, WPF, xUnit, FluentAssertions, `HttpClient`.

---

### Task 1: Destructive terminal identity guard

**Files:**
- Modify: `tests/Mt5Manager.Infrastructure.Tests/Processes/WindowsTerminalProcessControllerTests.cs`
- Modify: `src/Mt5Manager.Infrastructure/Processes/WindowsTerminalProcessController.cs`
- Modify: `tests/Mt5Manager.Wpf.Tests/ViewModelTests.cs`
- Modify: `src/Mt5Manager.Wpf/ViewModels/MainViewModels.cs`

- [ ] Add a controller regression test that starts a fixture process, changes its registration to `DataDirectoryVerified = false`, invokes `StopAsync`, and asserts `StopOutcome.Failed` while the process remains running.
- [ ] Run the focused infrastructure test and confirm it fails because stop currently accepts executable-only identity.
- [ ] Add a WPF test that refreshes an unverified running row and asserts Stop, Restart, and Cleanup are unavailable.
- [ ] Run the focused WPF test and confirm Stop/Restart currently fail the assertion.
- [ ] Make `StopAsync` reject missing/unverified data directories before scanning; separate destructive exact matching from non-destructive state discovery and remove destructive sole-candidate fallback.
- [ ] Require `terminal.DataDirectoryVerified` in `CanStop`; retain the existing Cleanup requirement and let Restart inherit Stop availability.
- [ ] Run both focused test classes and confirm all pass.

### Task 2: Global Algo Trading disclosure

**Files:**
- Modify: `tests/Mt5Manager.Application.Tests/Telegram/TelegramDashboardTests.cs`
- Modify: `src/Mt5Manager.Application/Telegram/TelegramDashboard.cs`
- Modify: `tests/Mt5Manager.Wpf.Tests/ViewModelTests.cs`
- Modify: `src/Mt5Manager.Wpf/ViewModels/MainViewModels.cs`
- Modify: the existing WPF XAML view that renders terminal Algo Trading controls

- [ ] Add Telegram tests asserting terminal and bulk confirmations say Global Algo Trading affects every EA in the affected terminal(s).
- [ ] Run the focused dashboard tests and confirm they fail on missing disclosure.
- [ ] Add a WPF view-model test for a stable warning property containing the same global-impact contract.
- [ ] Run the focused WPF test and confirm the property is missing.
- [ ] Add concise warning text to both Telegram confirmation variants and expose it through the terminal row view model.
- [ ] Bind/render the warning adjacent to the existing Enable/Disable controls without adding another confirmation step.
- [ ] Run focused Application and WPF tests and confirm all pass.

### Task 3: Telegram duplicate-consumer conflict

**Files:**
- Modify: `tests/Mt5Manager.Infrastructure.Tests/Telegram/TelegramBotApiClientTests.cs`
- Modify: `src/Mt5Manager.Infrastructure/Telegram/TelegramBotApiClient.cs`
- Modify: `tests/Mt5Manager.Application.Tests/Telegram/TelegramBotServiceTests.cs`
- Modify: `src/Mt5Manager.Application/Telegram/TelegramContracts.cs`
- Modify: `src/Mt5Manager.Application/Telegram/TelegramBotService.cs`
- Modify: `tests/Mt5Manager.Wpf.Tests/TelegramSettingsViewModelTests.cs`
- Modify: `src/Mt5Manager.Wpf/ViewModels/TelegramSettingsViewModel.cs`

- [ ] Add an API-client theory row proving HTTP 409 maps to `TelegramApiErrorKind.Conflict` and `TelegramBotErrorKind.Conflict`.
- [ ] Run the focused infrastructure test and confirm it fails on the current generic API classification.
- [ ] Add a bot-service test that injects conflict, waits for `TelegramBotState.Conflict`, and asserts no retry delay/poll occurs.
- [ ] Run the focused application test and confirm the missing state/behavior fails.
- [ ] Add Conflict enum values, map HTTP 409, stop the poll loop on conflict, and preserve the state in `finally`.
- [ ] Add a WPF test for actionable conflict status, observe failure, then add the status mapping.
- [ ] Run all three focused test classes and confirm they pass.

### Task 4: Optional cached public IPv4 metadata

**Files:**
- Create: `src/Mt5Manager.Application/Telegram/IPublicIpProvider.cs`
- Create: `src/Mt5Manager.Infrastructure/Telegram/HttpPublicIpProvider.cs`
- Create: `tests/Mt5Manager.Infrastructure.Tests/Telegram/HttpPublicIpProviderTests.cs`
- Modify: `src/Mt5Manager.Application/Telegram/TelegramContracts.cs`
- Modify: `src/Mt5Manager.Application/Telegram/TelegramBotService.cs`
- Modify: `src/Mt5Manager.Application/Telegram/TelegramDashboard.cs`
- Modify: `tests/Mt5Manager.Application.Tests/Telegram/TelegramBotServiceTests.cs`
- Modify: `tests/Mt5Manager.Application.Tests/Telegram/TelegramDashboardTests.cs`
- Modify: `src/Mt5Manager.Wpf/App.xaml.cs`
- Modify: affected constructor call sites/tests

- [ ] Add provider tests for valid IPv4, IPv6 rejection, malformed/HTTP failure fallback, successful process-lifetime cache, and bounded timeout.
- [ ] Run the new provider tests and confirm they fail because the provider does not exist.
- [ ] Define `IPublicIpProvider.GetAsync` and implement HTTPS lookup, two-second linked timeout, IPv4-only validation, null fallback, and success caching.
- [ ] Run provider tests and confirm they pass.
- [ ] Add dashboard tests for a valid IPv4 and `tidak tersedia` fallback; run and observe failure.
- [ ] Extend dashboard input with optional public IPv4 and render it.
- [ ] Add bot-service tests proving `/start` sends either valid IPv4 or fallback even when lookup fails; run and observe failure.
- [ ] Inject/use the provider in `TelegramBotService`; register it in WPF composition and update fixtures/call sites.
- [ ] Run focused Application, Infrastructure, and WPF tests and confirm they pass.

### Task 5: Integration verification and cleanup

**Files:**
- Modify only files required by compiler/test failures caused by the clean cutover.

- [ ] Run all affected project test suites in Release without restore.
- [ ] Run `dotnet test Mt5Manager.sln --configuration Release --no-restore` and require zero failures.
- [ ] Launch/smoke the WPF application where the environment supports it: verify unverified Stop/Restart disabled, global warning visible, and conflict status renderable.
- [ ] Exercise the Telegram dashboard through the fake endpoint/integration surface and verify IPv4/fallback output plus conflict termination.
- [ ] Remove any throwaway scripts/artifacts and confirm no obsolete fallback or compatibility alias remains.
