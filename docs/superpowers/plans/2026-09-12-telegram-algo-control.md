# Telegram Algo Trading Control Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a Chat-ID-restricted Telegram dashboard that safely controls Algo Trading for one or all registered MT5 terminals while MT5 Manager is running.

**Architecture:** Extract the existing WPF algo action into one serialized application service that resolves registrations, invokes the existing fail-closed controller, and audits outcomes. Add an infrastructure-owned Telegram Bot API adapter, DPAPI-protected atomic settings store, and explicit long-polling service; expose configuration through a modal WPF dialog and start/stop polling from application lifetime.

**Tech Stack:** .NET 8, C# 12, WPF, CommunityToolkit.Mvvm, `HttpClient`, `System.Text.Json`, Windows DPAPI, xUnit, FluentAssertions.

---

## Locked file structure

**Create**

- `src/Mt5Manager.Application/Abstractions/IAlgoTradingService.cs` — cross-client request/result/source contract.
- `src/Mt5Manager.Application/Services/AlgoTradingService.cs` — registration resolution, global serialization, controller call, and audit.
- `src/Mt5Manager.Application/Telegram/TelegramContracts.cs` — settings, connection state, transport DTOs, dashboard actions, and service ports.
- `src/Mt5Manager.Application/Telegram/TelegramDashboard.cs` — pure dashboard/list/confirmation/result rendering.
- `src/Mt5Manager.Application/Telegram/TelegramBotService.cs` — authorization, polling, offsets, callback sessions, bulk orchestration, retry, lifecycle.
- `src/Mt5Manager.Infrastructure/Persistence/JsonTelegramSettingsStore.cs` — versioned atomic settings and removal.
- `src/Mt5Manager.Infrastructure/Security/WindowsUserSecretProtector.cs` — DPAPI boundary.
- `src/Mt5Manager.Infrastructure/Telegram/TelegramBotApiClient.cs` — Bot API HTTP/JSON adapter.
- `src/Mt5Manager.Wpf/Views/TelegramSettingsDialog.xaml` and `.xaml.cs` — modal settings view.
- `src/Mt5Manager.Wpf/ViewModels/TelegramSettingsViewModel.cs` — validation/test/save/remove workflow.
- focused test files mirroring each component.

**Modify**

- `src/Mt5Manager.Application/Abstractions/IAuditLogger.cs` — add explicit operation source without altering cleanup semantics.
- `src/Mt5Manager.Wpf/ViewModels/MainViewModels.cs` — route row algo actions through the shared service.
- `src/Mt5Manager.Wpf/App.xaml.cs` — DI and bot lifecycle.
- `src/Mt5Manager.Wpf/MainWindow.xaml` and `.xaml.cs` — Telegram settings entry point.
- project/test `.csproj` files only where framework/package support is required.

No Telegram SDK package is needed; the required Bot API surface is small and `HttpClient` avoids a second abstraction beside existing conventions.

### Task 1: Shared serialized Algo Trading operation

**Files:**
- Create: `src/Mt5Manager.Application/Abstractions/IAlgoTradingService.cs`
- Create: `src/Mt5Manager.Application/Services/AlgoTradingService.cs`
- Modify: `src/Mt5Manager.Application/Abstractions/IAuditLogger.cs`
- Test: `tests/Mt5Manager.Application.Tests/Services/AlgoTradingServiceTests.cs`

- [ ] **Step 1: Write failing service tests**

Cover terminal resolution by immutable ID, unknown-terminal rejection, serialized simultaneous calls, controller result preservation, cancellation, and one audit record per request. Assert source is `Wpf` or `Telegram` and never infer it from operation text.

```csharp
[Fact]
public async Task Concurrent_requests_are_serialized_and_audited()
{
    var controller = new BlockingAlgoController();
    var audit = new RecordingAuditLogger();
    var service = new AlgoTradingService(new FakeRegistry(Terminal()), controller, audit);
    var first = service.SetAsync(new(TerminalId, true, AlgoOperationSource.Wpf));
    await controller.FirstEntered.Task;
    var second = service.SetAsync(new(TerminalId, false, AlgoOperationSource.Telegram));
    controller.MaxConcurrent.Should().Be(1);
    controller.Release();
    (await Task.WhenAll(first, second)).Should().OnlyContain(x => x.Result.Success);
    audit.Records.Select(x => x.Source).Should().Equal(AlgoOperationSource.Wpf, AlgoOperationSource.Telegram);
}
```

- [ ] **Step 2: Run the focused tests and verify RED**

Run: `dotnet test tests/Mt5Manager.Application.Tests/Mt5Manager.Application.Tests.csproj --filter FullyQualifiedName~AlgoTradingServiceTests`

Expected: FAIL because service contracts do not exist.

- [ ] **Step 3: Define the application contract and audit source**

```csharp
public enum AlgoOperationSource { Wpf, Telegram }
public sealed record AlgoTradingRequest(Guid TerminalId, bool Enable, AlgoOperationSource Source);
public sealed record AlgoTradingOperationResult(
    Guid TerminalId, string TerminalName, bool Enable,
    AlgoTradingControlResult Result);
public interface IAlgoTradingService
{
    Task<AlgoTradingOperationResult> SetAsync(AlgoTradingRequest request, CancellationToken cancellationToken = default);
}
```

Add `AlgoOperationSource? Source` to `AuditRecord` as its final positional member with default `null`; cleanup callers remain source-neutral. `AlgoTradingService` owns a single `SemaphoreSlim`, reloads the registry after entering it, rejects unknown IDs without invoking the controller, calls `ITerminalAlgoTradingController.SetAsync`, and appends `Enable Algo`/`Disable Algo` with Completed or Rejected outcome.

- [ ] **Step 4: Run application and affected WPF tests**

Run: `dotnet test tests/Mt5Manager.Application.Tests/Mt5Manager.Application.Tests.csproj --filter FullyQualifiedName~AlgoTradingServiceTests`

Expected: PASS.

Run: `dotnet test tests/Mt5Manager.Wpf.Tests/Mt5Manager.Wpf.Tests.csproj --filter FullyQualifiedName~ViewModelTests`

Expected: existing tests compile and pass after adding the optional audit member.

- [ ] **Step 5: Commit**

```bash
git add src/Mt5Manager.Application tests/Mt5Manager.Application.Tests src/Mt5Manager.Application/Abstractions/IAuditLogger.cs
git commit -m "feat: centralize algo trading operations"
```

### Task 2: Route WPF through the shared service

**Files:**
- Modify: `src/Mt5Manager.Wpf/ViewModels/MainViewModels.cs`
- Modify: `src/Mt5Manager.Wpf/App.xaml.cs`
- Modify: `tests/Mt5Manager.Wpf.Tests/ViewModelTests.cs`

- [ ] **Step 1: Replace the row test with an observable shared-service test**

```csharp
[Fact]
public async Task Algo_command_uses_shared_service_and_projects_verified_snapshot()
{
    var service = new RecordingAlgoService(new(true, "Algo Trading was enabled.", NewSnapshot(AlgoTradingState.Enabled)));
    var row = new TerminalRowViewModel(T(), new Process(), new Runtime { Snapshot = NewSnapshot(AlgoTradingState.Disabled) }, service);
    await row.RefreshStateAsync();
    await row.EnableAlgoAsync();
    service.Requests.Should().ContainSingle().Which.Source.Should().Be(AlgoOperationSource.Wpf);
    row.AlgoSummary.Should().Contain("Global Enabled");
}
```

Also assert a failed shared result is surfaced once and the row no longer writes a duplicate algo audit record.

- [ ] **Step 2: Run the focused tests and verify RED**

Run: `dotnet test tests/Mt5Manager.Wpf.Tests/Mt5Manager.Wpf.Tests.csproj --filter "FullyQualifiedName~Algo_command"`

Expected: FAIL because `TerminalRowViewModel` still accepts the controller.

- [ ] **Step 3: Cut over the row and composition root**

Replace the row's `ITerminalAlgoTradingController` dependency with `IAlgoTradingService`. Its private action becomes:

```csharp
var operation = await algo!.SetAsync(
    new AlgoTradingRequest(terminal.Id, enable, AlgoOperationSource.Wpf), cancellationToken);
var result = operation.Result;
```

Preserve projection of `result.Snapshot`, error display, and operation-history reload. Prevent `Run` from writing another audit for algo calls because the service owns that record. Register singleton `IAlgoTradingService, AlgoTradingService` and inject it into `MainViewModel`.

- [ ] **Step 4: Run WPF view-model tests**

Run: `dotnet test tests/Mt5Manager.Wpf.Tests/Mt5Manager.Wpf.Tests.csproj --filter FullyQualifiedName~ViewModelTests`

Expected: PASS; controller fake is replaced only for row algo tests.

- [ ] **Step 5: Commit**

```bash
git add src/Mt5Manager.Wpf tests/Mt5Manager.Wpf.Tests
git commit -m "refactor: share algo command path"
```

### Task 3: Telegram contracts and pure dashboard renderer

**Files:**
- Create: `src/Mt5Manager.Application/Telegram/TelegramContracts.cs`
- Create: `src/Mt5Manager.Application/Telegram/TelegramDashboard.cs`
- Test: `tests/Mt5Manager.Application.Tests/Telegram/TelegramDashboardTests.cs`

- [ ] **Step 1: Write renderer tests**

Assert the main panel contains Status/Refresh, ON Terminal, OFF Terminal, ON Semua, OFF Semua; terminal buttons use `DisplayName — Login`; unavailable accounts are marked unavailable; confirmations include current/requested state and Confirm/Cancel; bulk results group Success/Already/Failed; messages split only between terminal result lines and remain within 4096 characters.

- [ ] **Step 2: Run tests and verify RED**

Run: `dotnet test tests/Mt5Manager.Application.Tests/Mt5Manager.Application.Tests.csproj --filter FullyQualifiedName~TelegramDashboardTests`

Expected: FAIL because Telegram contracts do not exist.

- [ ] **Step 3: Add transport-neutral contracts**

Define `TelegramSettings(bool Enabled, string ProtectedBotToken, long AllowedChatId, long LastUpdateOffset)`, `TelegramConnectionState`, `TelegramUpdate`, `TelegramCallback`, `TelegramMessage`, `InlineButton`, `InlineKeyboard`, and ports:

```csharp
public interface ITelegramSettingsStore
{
    Task<TelegramSettings?> LoadAsync(CancellationToken token = default);
    Task SaveAsync(TelegramSettings settings, CancellationToken token = default);
    Task RemoveAsync(CancellationToken token = default);
}
public interface ISecretProtector { string Protect(string value); string Unprotect(string value); }
public interface ITelegramBotApiClient
{
    Task<TelegramBotIdentity> GetMeAsync(string token, CancellationToken ct);
    Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(string token, long offset, CancellationToken ct);
    Task SendAsync(string token, long chatId, TelegramMessage message, CancellationToken ct);
    Task EditAsync(string token, long chatId, long messageId, TelegramMessage message, CancellationToken ct);
    Task AnswerCallbackAsync(string token, string callbackId, string? text, CancellationToken ct);
}
```

Keep API JSON DTOs out of these public application contracts.

- [ ] **Step 4: Implement deterministic rendering and run tests**

Callback values use compact verbs plus an opaque session token, for example `pick_on:<token>:<terminal-id>` and `confirm:<token>`. The renderer receives generated tokens; it does not generate or store sessions.

Run: `dotnet test tests/Mt5Manager.Application.Tests/Mt5Manager.Application.Tests.csproj --filter FullyQualifiedName~TelegramDashboardTests`

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Mt5Manager.Application/Telegram tests/Mt5Manager.Application.Tests/Telegram
git commit -m "feat: define Telegram dashboard contracts"
```

### Task 4: Protected settings persistence

**Files:**
- Create: `src/Mt5Manager.Infrastructure/Persistence/JsonTelegramSettingsStore.cs`
- Create: `src/Mt5Manager.Infrastructure/Security/WindowsUserSecretProtector.cs`
- Modify: `src/Mt5Manager.Infrastructure/Mt5Manager.Infrastructure.csproj`
- Test: `tests/Mt5Manager.Infrastructure.Tests/Persistence/JsonTelegramSettingsStoreTests.cs`
- Test: `tests/Mt5Manager.Infrastructure.Tests/Security/WindowsUserSecretProtectorTests.cs`

- [ ] **Step 1: Write persistence/security tests**

Test version validation, atomic overwrite, removal, invalid Chat ID rejection, corrupt-file preservation, and round-trip protection. Read the persisted JSON and assert the plaintext bot token is absent. Use an isolated temporary path; never write test secrets to ProgramData.

- [ ] **Step 2: Run tests and verify RED**

Run: `dotnet test tests/Mt5Manager.Infrastructure.Tests/Mt5Manager.Infrastructure.Tests.csproj --filter "FullyQualifiedName~TelegramSettings|FullyQualifiedName~SecretProtector"`

Expected: FAIL because implementations do not exist.

- [ ] **Step 3: Implement store and DPAPI protector**

Use `%ProgramData%\Mt5Manager\telegram.json`, document version 1, the same create-new/write-through/flush/move pattern as `JsonTerminalRegistry`, and `.corrupt-<UTC timestamp>` preservation. `WindowsUserSecretProtector` uses `ProtectedData.Protect/Unprotect` with `DataProtectionScope.CurrentUser`, UTF-8, and a fixed application entropy byte string. Add `System.Security.Cryptography.ProtectedData` only if the target framework does not expose it without a package.

- [ ] **Step 4: Run focused tests**

Run the command from Step 2.

Expected: PASS and persisted fixture text contains no plaintext token.

- [ ] **Step 5: Commit**

```bash
git add src/Mt5Manager.Infrastructure tests/Mt5Manager.Infrastructure.Tests
git commit -m "feat: protect Telegram bot settings"
```

### Task 5: Telegram Bot API adapter

**Files:**
- Create: `src/Mt5Manager.Infrastructure/Telegram/TelegramBotApiClient.cs`
- Test: `tests/Mt5Manager.Infrastructure.Tests/Telegram/TelegramBotApiClientTests.cs`

- [ ] **Step 1: Write HTTP contract tests**

Use a fake `HttpMessageHandler`. Verify `getMe`, long-poll `getUpdates` with offset and timeout, send/edit inline keyboard JSON, callback acknowledgement, Telegram error parsing, 401 classification, 429 `retry_after`, and that thrown messages never contain the token or raw request URI.

- [ ] **Step 2: Run tests and verify RED**

Run: `dotnet test tests/Mt5Manager.Infrastructure.Tests/Mt5Manager.Infrastructure.Tests.csproj --filter FullyQualifiedName~TelegramBotApiClientTests`

Expected: FAIL because the adapter does not exist.

- [ ] **Step 3: Implement the narrow HTTP adapter**

Construct request URIs immediately before sending and never retain/log them. Deserialize Telegram envelopes with private DTOs. `getUpdates` requests only `message` and `callback_query`, uses a server timeout below the `HttpClient` timeout, and passes cancellation through. Map HTTP/API failures to a typed `TelegramApiException(StatusCode, RetryAfter, SafeMessage)`.

- [ ] **Step 4: Run focused tests**

Run the command from Step 2.

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Mt5Manager.Infrastructure/Telegram tests/Mt5Manager.Infrastructure.Tests/Telegram
git commit -m "feat: add Telegram Bot API client"
```

### Task 6: Authorized polling, confirmations, and bulk execution

**Files:**
- Create: `src/Mt5Manager.Application/Telegram/TelegramBotService.cs`
- Test: `tests/Mt5Manager.Application.Tests/Telegram/TelegramBotServiceTests.cs`

- [ ] **Step 1: Write behavioral service tests**

Test: foreign chat gets no response/action; `/start` sends dashboard; callbacks are additionally checked against message chat; single-target ON/OFF requires a valid two-minute one-use confirmation; double click invokes algo once; bulk captures terminal IDs at confirmation and continues after failure; callbacks are acknowledged before blocked MT5 work; edit failure sends a replacement dashboard; 401 stops; 429 honors retry delay; transient errors back off; cancellation stops polling; persisted offset prevents replay.

Inject `TimeProvider` and `IDelay` so expiry/backoff tests are deterministic.

- [ ] **Step 2: Run tests and verify RED**

Run: `dotnet test tests/Mt5Manager.Application.Tests/Mt5Manager.Application.Tests.csproj --filter FullyQualifiedName~TelegramBotServiceTests`

Expected: FAIL because the service does not exist.

- [ ] **Step 3: Implement explicit lifecycle and update routing**

Expose:

```csharp
public interface ITelegramBotService : IAsyncDisposable
{
    TelegramConnectionState State { get; }
    event EventHandler? StateChanged;
    Task StartAsync(CancellationToken token = default);
    Task StopAsync(CancellationToken token = default);
    Task ApplySettingsAsync(TelegramSettings? settings, CancellationToken token = default);
}
```

Use one private polling task and CTS guarded by a lifecycle semaphore. Default-deny authorization before rendering or dispatch. Keep pending confirmations in-memory keyed by cryptographically random compact tokens; store chat ID, action, immutable terminal-ID array, created/expiry time, and consumed flag. Consume atomically before executing. Process bulk IDs sequentially through `IAlgoTradingService`; classify success with already-desired messages separately from changed success. Persist the next update offset only after safe handling and durable operation/audit completion.

- [ ] **Step 4: Run focused tests**

Run the command from Step 2.

Expected: PASS, including foreign-chat and duplicate-callback tests with zero algo calls.

- [ ] **Step 5: Commit**

```bash
git add src/Mt5Manager.Application/Telegram tests/Mt5Manager.Application.Tests/Telegram
git commit -m "feat: orchestrate Telegram algo controls"
```

### Task 7: Telegram settings dialog

**Files:**
- Create: `src/Mt5Manager.Wpf/ViewModels/TelegramSettingsViewModel.cs`
- Create: `src/Mt5Manager.Wpf/Views/TelegramSettingsDialog.xaml`
- Create: `src/Mt5Manager.Wpf/Views/TelegramSettingsDialog.xaml.cs`
- Modify: `src/Mt5Manager.Wpf/MainWindow.xaml`
- Modify: `src/Mt5Manager.Wpf/MainWindow.xaml.cs`
- Test: `tests/Mt5Manager.Wpf.Tests/TelegramSettingsViewModelTests.cs`

- [ ] **Step 1: Write view-model tests**

Assert numeric positive Chat ID validation; token required when enabled; load decrypts only into the masked editor; Test Connection calls `getMe` then sends a test message but does not save/start; Save protects and persists before applying settings; failed validation changes neither store nor service; Remove requires an explicit confirmed method call and clears store/service; plaintext token never appears in status/error projection.

- [ ] **Step 2: Run tests and verify RED**

Run: `dotnet test tests/Mt5Manager.Wpf.Tests/Mt5Manager.Wpf.Tests.csproj --filter FullyQualifiedName~TelegramSettingsViewModelTests`

Expected: FAIL because the view model does not exist.

- [ ] **Step 3: Implement view model and modal view**

Follow existing dialog styling. Use `PasswordBox` in code-behind to synchronize the token because WPF does not safely bind `Password`; clear it on close. Fields: enabled checkbox, Bot Token, Allowed Chat ID, connection status, Test Connection, Save, Remove Configuration, Cancel. Disable mutating controls while busy. `Remove Configuration` uses a WPF confirmation prompt before calling the confirmed view-model operation.

Add a `Telegram Bot` secondary button beside `Register terminal` in the main header and open the DI-created dialog from `MainWindow.xaml.cs`.

- [ ] **Step 4: Run focused tests and compile WPF**

Run the test command from Step 2.

Run: `dotnet build src/Mt5Manager.Wpf/Mt5Manager.Wpf.csproj --configuration Debug`

Expected: PASS/build succeeds with no XAML errors.

- [ ] **Step 5: Commit**

```bash
git add src/Mt5Manager.Wpf tests/Mt5Manager.Wpf.Tests
git commit -m "feat: add Telegram bot settings UI"
```

### Task 8: Composition, lifecycle, and end-to-end verification

**Files:**
- Modify: `src/Mt5Manager.Wpf/App.xaml.cs`
- Modify: `src/Mt5Manager.Wpf/MainWindow.xaml.cs`
- Modify: relevant `.csproj` files if DI factories require no new package
- Test: existing focused suites

- [ ] **Step 1: Register concrete services and `HttpClient`**

Register singleton settings store, protector, dashboard, algo service, Bot API client built from one app-owned `HttpClient`, bot service, and settings view-model/dialog factory. Do not add Generic Host. Keep token values out of DI diagnostics.

- [ ] **Step 2: Wire explicit application lifetime**

After provider creation, await `ITelegramBotService.StartAsync` without blocking the dispatcher indefinitely; invalid/disabled settings leave it stopped and visible through state. In `OnExit`, synchronously bridge only the bounded `StopAsync`/dispose path before provider disposal, then dispose `HttpClient`. Ensure window close stops accepting new bot operations through the application CTS.

- [ ] **Step 3: Run all focused project suites**

Run:

```bash
dotnet test tests/Mt5Manager.Application.Tests/Mt5Manager.Application.Tests.csproj
dotnet test tests/Mt5Manager.Infrastructure.Tests/Mt5Manager.Infrastructure.Tests.csproj
dotnet test tests/Mt5Manager.Wpf.Tests/Mt5Manager.Wpf.Tests.csproj
```

Expected: all pass.

- [ ] **Step 4: Run a real non-production smoke scenario**

Launch `src/Mt5Manager.Wpf`, configure a test bot and the authorized private Chat ID, then observe:

1. `/start` creates the five-button dashboard.
2. Refresh shows each `terminal name — account login` or unavailable marker.
3. Cancel performs zero MT5 actions.
4. ON/OFF confirmation for one non-production terminal changes only that exact terminal and reports the bridge-verified state.
5. ON Semua/OFF Semua continues when one terminal is unavailable and groups Changed/Already/Failed results.
6. Re-clicking the consumed callback performs zero additional actions.
7. Restarting MT5 Manager does not replay the last update.
8. Closing MT5 Manager stops bot responses.

Do not use production trading accounts for this smoke test. If real Telegram credentials or non-production MT5 terminals are unavailable, run the app and verify settings save/start/stop with the fake HTTP handler harness, then report the unavailable external prerequisites explicitly rather than claiming the network scenario passed.

- [ ] **Step 5: Cleanup and final checks**

Remove any throwaway bot credentials, fake handlers, or local settings generated for smoke verification. Confirm `%ProgramData%\Mt5Manager\telegram.json` contains only protected token material. Update existing user-facing help text in the settings dialog if the observed workflow differs; do not create additional documentation unless requested.

Run: `dotnet build Mt5Manager.sln --configuration Release`

Expected: build succeeds.

- [ ] **Step 6: Commit**

```bash
git add src tests
git commit -m "feat: integrate Telegram bot lifecycle"
```

## Plan self-review result

- Spec coverage: interactive dashboard, command fallbacks, one Chat ID, per-target and bulk confirmation, partial failure, DPAPI, atomic settings, update offset, serialization, audit source, retry, shutdown, UI settings, and real smoke verification are each assigned above.
- Type consistency: WPF and Telegram both depend on `IAlgoTradingService`; the controller remains infrastructure-only; settings carry protected—not plaintext—token data.
- Scope: one cohesive feature with sequential dependency boundaries; no webhook, Generic Host migration, Windows Service, multi-user roles, or Telegram SDK abstraction.
