# Telegram Algo Trading Control Design

## Goal

Allow one authorized Telegram chat to inspect and change the global Algo Trading state of multiple registered MT5 terminals through an interactive bot dashboard, while preserving the manager's exact-window targeting, verified bridge state, serialization, and audit guarantees.

## Scope

The integration controls the same terminal-wide MT5 setting represented by `TERMINAL_TRADE_ALLOWED` and the toolbar/Ctrl+E command. It does not independently enable or disable individual Expert Advisors or charts.

Telegram is an additional client of the existing manager. MT5 Manager must be running for the bot to receive commands. No webhook, public endpoint, Windows Service, or always-on companion process is introduced.

## Telegram interaction

`/start` and `/menu` create or restore an inline dashboard. Commands remain available as shortcuts, but the dashboard is the primary interface. It contains:

- Status / Refresh;
- ON Terminal;
- OFF Terminal;
- ON Semua;
- OFF Semua.

`/status`, `/algo_on`, `/algo_off`, `/on_all`, and `/off_all` provide equivalent direct entry points.

Selecting ON Terminal or OFF Terminal displays registered targets as `terminal name — account login`. The account login comes from the current verified bridge snapshot. If the account is unavailable, the terminal remains identifiable by name and is visibly marked unavailable rather than being assigned stale account data.

After selecting a terminal, the bot shows its current state, requested state, and Confirm/Cancel controls. Confirmation is required for both ON and OFF. A successful or cancelled interaction returns to a refreshed dashboard.

ON Semua and OFF Semua first display every target and its current availability/state, then require one explicit bulk confirmation. Bulk processing continues after individual failures and reports each target under Berhasil, Sudah ON/OFF, or Gagal.

## Authorization and callback safety

Configuration permits exactly one numeric Telegram Chat ID. Every message must have a matching `chat.id`; every callback must also originate from a message in that chat. Updates from all other chats are ignored without a response or terminal information disclosure.

Callback payloads contain only an internal terminal identifier and a short opaque action token. They never contain the bot token, filesystem paths, or account metadata. Pending confirmation tokens expire after two minutes, are single-use, and are invalidated after configuration changes or application restart. Duplicate, expired, malformed, or superseded callbacks perform no MT5 action and direct the authorized user to refresh the dashboard.

The last accepted Telegram update offset is persisted. Startup begins after that offset so a restart cannot replay an acknowledged command. The offset advances only after an update has been safely classified and handled; an authorized confirmed operation records its durable outcome before acknowledgement advances.

## WPF settings

A Telegram Bot settings surface provides:

- integration enabled state;
- masked Bot Token input;
- one numeric Allowed Chat ID;
- Test Connection;
- Save;
- Remove Configuration;
- connection status.

Connection states are Nonaktif, Menghubungkan, Aktif sebagai `@bot_name`, Token/Chat ID tidak valid, and Gangguan koneksi — mencoba kembali.

Test Connection validates the token with Telegram `getMe` and sends a test message to the configured Chat ID. It does not persist values or start long polling. Save validates first, stops the previous polling session, atomically persists the new settings, and starts a new session only when enabled. Remove Configuration requires confirmation, stops polling, and deletes the stored secret and Chat ID.

The bot token is encrypted with Windows DPAPI for the current Windows user before persistence. Plaintext token values must not enter logs, audit records, exception messages, callback data, or UI status text. Settings use versioned JSON and temporary-file atomic replacement under `%LocalAppData%\Mt5Manager`, matching the current-user secret scope and preventing another Windows user from accessing or deleting the encrypted token.

## Architecture

`ITerminalAlgoTradingService` becomes the application-level desired-state operation shared by WPF and Telegram. It resolves a terminal registration, calls `ITerminalAlgoTradingController.SetAsync`, and records one audit outcome. WPF view models no longer construct a separate control/audit path. Telegram never calls WPF view models.

The integration consists of:

- `TelegramSettings`, containing enabled state, encrypted token representation, Allowed Chat ID, and update offset;
- `ITelegramSettingsStore`, providing validated atomic load/save/remove operations;
- a DPAPI-backed token protector at the infrastructure boundary;
- a Telegram Bot API client boundary for `getMe`, `getUpdates`, send, edit, and callback acknowledgement operations;
- `TelegramBotService`, owning explicit start/stop lifetime, long polling, authorization, routing, retry policy, and callback session state;
- a dashboard renderer that creates messages and inline keyboards without executing MT5 operations;
- a WPF settings view model and view bound to the service lifecycle and connection state.

The WPF composition root registers these services. Application startup starts polling only for a valid enabled configuration. Window/application shutdown cancels polling and waits for its controlled stop through the existing WPF lifetime; a Generic Host is not required.

## Serialization and state consistency

All UI and Telegram mutations pass through the shared application service and one process-wide operation queue. Operations execute serially because MT5 control acquires foreground focus and sends Ctrl+E. Bulk operations capture the ordered set of registered terminal identifiers at confirmation time and enqueue/process them sequentially.

The controller re-reads the bridge immediately before acting. If the requested state is already observed, it returns an idempotent Already ON/OFF result and does not send Ctrl+E. Success is reported only after the bridge verifies the desired `TERMINAL_TRADE_ALLOWED` state.

Status refresh may read snapshots while mutations are queued or active, but it cannot initiate another mutation. Dashboard text is refreshed after an operation. If editing the existing Telegram message fails because it is missing or no longer editable, the bot sends a new dashboard and treats it as current.

## Audit

Every attempted target operation creates an audit record through the shared application service with source WPF or Telegram, terminal identity, requested state, timestamp, outcome, and a safe failure description. A bulk request therefore creates one record per terminal. Bot tokens, opaque callback tokens, filesystem paths, and raw Telegram update payloads are excluded.

## Failure handling

Telegram timeouts and transient transport failures retry with bounded exponential backoff. HTTP 429 honors `retry_after`; HTTP 5xx retries. HTTP 401 stops polling and exposes an invalid-token state instead of retrying indefinitely.

MT5 offline state, stale or missing bridge snapshots, unknown account data, ambiguous windows, focus/input failure, and verification timeout fail only the affected terminal. Bulk execution continues and reports partial results.

Telegram callback queries are acknowledged promptly before potentially long MT5 work, and the dashboard shows an in-progress state. Telegram message output is split at terminal-result boundaries when it would exceed API limits; result content is never silently truncated.

Application shutdown cancels polling and prevents acceptance of new operations. An operation already executing receives the application cancellation token and must not be reported successful unless the bridge verified its target state.

## Verification

Permanent tests cover externally observable risk boundaries:

- a non-matching Chat ID receives no response and cannot execute a callback;
- expired, duplicate, and malformed confirmation tokens perform no operation;
- desired-state idempotency does not send Ctrl+E;
- UI and Telegram mutations share serial execution;
- bulk execution continues after a target failure and reports per-target outcomes;
- persisted update offsets prevent acknowledged update replay;
- plaintext bot tokens are absent from persisted settings and diagnostic/audit output.

A real smoke scenario with a test bot and non-production terminals exercises dashboard creation, refresh, cancellation, one-terminal ON/OFF, bulk control with at least one unavailable target, restart without update replay, and clean polling shutdown.