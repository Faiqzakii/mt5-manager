# MT5 Manager Design

## Objective

Build a native Windows desktop application that manages many MetaTrader 5 terminals in one Windows/RDP session. Each terminal is managed individually. The application discovers terminals, shows their runtime state and storage usage, starts/stops/restarts them, and safely clears logs, ticks, or history.

## Scope

### Included

- One Windows machine and one RDP session.
- Automatic discovery with manual registration fallback.
- Individual terminal operations only.
- Start, graceful stop, restart, and cleanup.
- Independently selectable Logs, Ticks, and History cleanup categories.
- Stop-clean-start workflow when cleanup targets a running terminal.
- Local configuration and local audit records.

### Excluded

- Multi-server management or network agents.
- Bulk operations across multiple terminals.
- Strategy Tester cache cleanup.
- User-defined deletion paths or cleanup patterns.
- Remote web dashboard, database server, and network API.

## Technology and Architecture

Use .NET 8 and WPF. The application runs in the same Windows session as the MT5 terminals.

The UI calls local application services through explicit interfaces:

1. **Terminal Discovery** scans running `terminal64.exe` processes, Windows shortcuts, and standard installation locations. It supports manual registration for portable or nonstandard installations.
2. **Terminal Registry** persists each terminal's display name, executable path, data directory, and startup arguments in JSON under `%ProgramData%`.
3. **Process Controller** resolves process state and handles start, graceful stop, forced termination after explicit user approval, and restart.
4. **Storage Inspector** calculates file counts and byte totals for each allowed cleanup category.
5. **Cleanup Service** validates targets and deletes only files belonging to selected allowlisted categories.
6. **Operation Coordinator** serializes operations per terminal and implements stop-clean-start behavior.
7. **Audit Logger** records operation time, terminal, requested action, affected category, result, failure details, and restart outcome.

No database, Windows service, local HTTP server, or network dependency is required.

## Terminal Identity and Discovery

A terminal record contains:

- Stable application-generated identifier.
- Display name.
- Canonical executable path.
- Canonical data-directory path.
- Startup arguments.
- Discovery source: running process, shortcut, standard scan, or manual.
- Last observed process ID and state; these are runtime state, not durable identity.

Automatic discovery must deduplicate records using canonical executable path plus canonical data-directory path. A process name alone is insufficient because many terminals may use the same executable name.

For a running terminal, discovery must resolve its executable and command-line arguments. If the data folder cannot be resolved confidently, the terminal remains visible but cleanup is disabled until the user supplies or confirms the data directory. Discovery must not guess a destructive target.

Manual registration requires selecting an executable and data folder. Both paths are validated before saving.

## User Interface

The main window contains:

- Search field.
- `Scan` action.
- `Add Manual` action.
- One row or card per terminal.

Each terminal displays:

- Display name.
- Running, stopped, busy, or error status.
- Executable path and data folder.
- Process ID when running.
- File count and size for Logs, Ticks, and History.
- Last operation result.
- Start, Stop, Restart, and Cleanup actions.

Status and storage summaries refresh periodically in the background without blocking the UI. A terminal with an active operation disables conflicting actions while other terminal rows remain usable.

## Process Operations

### Start

Start the registered executable with its stored working directory and arguments. Report process-launch errors directly. Starting an already-running terminal is rejected rather than creating an accidental duplicate.

### Stop

Request a normal Windows close and wait for a configured timeout. If the process remains alive, explain that cleanup or restart cannot continue and offer force termination. Force termination requires an explicit user action; it is not automatic.

### Restart

Perform the same graceful stop procedure, then launch the terminal with its stored executable, working directory, and arguments. If stopping fails or force termination is declined, do not launch another process.

## Cleanup Workflow

Cleanup is available only for a terminal with a verified data directory.

1. User opens Cleanup for one terminal.
2. Dialog allows independent selection of Logs, Ticks, and History.
3. Storage Inspector presents file count and total size for each selection.
4. User explicitly confirms deletion.
5. Operation Coordinator records whether the terminal was running.
6. If running, request normal close and wait for exit.
7. If it does not exit, offer force termination. Declining cancels cleanup without deleting files.
8. Confirm no managed process is using that terminal record.
9. Revalidate every cleanup target.
10. Delete files in each selected category and collect per-file failures.
11. If the terminal was running before cleanup, start it using the registered launch configuration.
12. Present deleted counts/bytes, failures, and restart status; write the same outcome to the audit log.

A terminal that was stopped before cleanup remains stopped afterward.

## Cleanup Safety Invariants

- Cleanup categories map to application-owned, fixed relative locations for Logs, Ticks, and History; users cannot supply arbitrary deletion patterns.
- The canonical target must remain beneath the canonical registered data directory.
- The application must not follow junctions, symbolic links, or reparse points that escape the data directory.
- Root directories and category directories remain intact unless MT5 behavior specifically requires recreating them; the default operation deletes contained files only.
- A failure in one category does not hide or rewrite the result of another category.
- Failed files remain reported with their operating-system error.
- Cleanup never proceeds while the associated terminal process is still running.
- Privilege elevation is requested only after an actual access denial and only for the requested operation. Normal use remains unelevated.

## Concurrency and State

Operations are serialized per terminal with an async lock. Start, stop, restart, cleanup, discovery refresh, and size calculation must not mutate the same terminal state concurrently. Locks are independent between terminals, preserving UI responsiveness even though operations are exposed individually.

Long-running filesystem and process work runs off the UI thread. Cancellation closes the dialog or cancels work only at safe boundaries; it cannot interrupt a file mutation in a way that reports success incorrectly.

## Error Handling

Errors are actionable and tied to a terminal and operation. Relevant cases include:

- Executable or data directory no longer exists.
- Process command line cannot be inspected.
- Graceful shutdown times out.
- Force termination is declined or fails.
- File is locked or access is denied.
- A target fails canonical-path or reparse-point validation.
- Restart fails after successful cleanup.
- Configuration or audit log cannot be read or written.

Partial cleanup is reported as partial, never success. A restart failure after cleanup is a distinct result: cleanup may have succeeded while the terminal remains stopped.

## Persistence and Audit

Configuration is stored as versioned JSON under `%ProgramData%` and written atomically using a temporary file plus replace. Invalid records are isolated and surfaced rather than preventing all valid terminals from loading.

Audit records are append-only local entries containing no trading credentials. Each includes timestamp, terminal identifier, operation, selected categories, deleted file count and bytes, failures, pre-operation process state, shutdown method, and restart result.

## Verification

Runtime verification must cover:

- Discovery of standard installations, shortcuts, portable terminals, and running processes.
- Deduplication of the same terminal found through multiple sources.
- Correct executable/data-folder association when many `terminal64.exe` processes exist.
- Manual registration and invalid-path rejection.
- Normal stop, stop timeout, declined force termination, and successful force termination.
- Restart preserving registered executable, working directory, and arguments.
- Cleanup of each category independently and in combinations.
- Running terminal stop-clean-start behavior.
- Previously stopped terminal remaining stopped.
- Canonical path traversal rejection and junction/symlink escape rejection.
- Locked files, access denial, partial deletion, and restart failure reporting.
- Per-terminal operation serialization and UI recovery after errors.

The destructive path should be exercised first against disposable fixture directories and a non-production MT5 installation before use on trading terminals.

## Acceptance Criteria

- The main window lists automatically discovered and manually registered MT5 terminals without duplicates.
- Each terminal accurately reports running state and category sizes.
- Users can start, gracefully stop, and restart one terminal while preserving its launch configuration.
- Users can preview and clean any combination of Logs, Ticks, and History for one terminal.
- Cleanup of a running terminal stops it first and restarts it only if it was previously running.
- No cleanup target outside the verified data directory can be deleted.
- Partial failures and restart failures remain visible and are recorded locally.
- One terminal's operation cannot race with another operation on that same terminal or freeze the application UI.
