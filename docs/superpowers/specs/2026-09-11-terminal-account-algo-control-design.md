# Terminal Account and Algo Trading Control Design

## Goal

Show the active MT5 account for every detected terminal and provide a safe per-terminal shortcut for changing the terminal-wide Algo Trading state.

## Scope

The feature targets MetaTrader 5. “Algo Trading” means the global terminal permission represented by `TERMINAL_TRADE_ALLOWED` and the MT5 toolbar/Ctrl+E command. It does not independently enable or disable individual Expert Advisors or charts.

## Terminal-side bridge

A bundled MQL5 Expert Advisor publishes a versioned runtime snapshot containing:

- bridge protocol version and update timestamp;
- canonical `TERMINAL_DATA_PATH`;
- account login, name, server, company, and trade mode;
- `TERMINAL_CONNECTED`;
- `TERMINAL_TRADE_ALLOWED`;
- `MQL_TRADE_ALLOWED`;
- `ACCOUNT_TRADE_ALLOWED`;
- `ACCOUNT_TRADE_EXPERT`.

The bridge writes through `FILE_COMMON` using a temporary record followed by atomic replacement. Each terminal writes a distinct record keyed by a stable hash of its canonical data path. No credentials, passwords, or trading commands are stored.

The manager accepts a snapshot only when its protocol is supported, it is fresh, and its reported canonical data path exactly matches the terminal’s verified data directory. A missing, stale, malformed, or mismatched snapshot produces `Bridge unavailable` or `Unknown`; it never falls back to account files, window captions, or process memory.

## Account presentation

Each terminal card shows login, account name, server, and account type when a valid snapshot exists. Account state is volatile and is not persisted in `TerminalRegistration` or used as terminal identity. Refreshing the dashboard rereads the snapshot.

## Global Algo Trading control

The manager exposes an Enable/Disable action per running terminal. The action is enabled only when:

- the terminal has a verified data directory;
- a fresh bridge snapshot matches that directory;
- exactly one running top-level MT5 window matches the terminal process;
- the current global Algo Trading state is known;
- no operation is already in progress.

If the requested state already equals the reported state, the action is a no-op. Otherwise the manager activates the exact matched terminal window and sends the official Ctrl+E shortcut. It then polls the bridge snapshot until `TERMINAL_TRADE_ALLOWED` equals the requested state or a short timeout expires. Success is reported only after observed state change. Ambiguous windows, inability to focus/send input, stale bridge data, or timeout fail closed with a visible error.

The manager does not claim that global Algo Trading alone makes an EA able to trade. The dashboard separately displays terminal, EA, account, and connection permission indicators from the bridge.

## Explicit data-directory correction

Shortcut `/datadir` arguments are parsed before portable or AppData-origin resolution. A structurally valid explicit directory is authoritative. If an explicit target exists but is invalid, resolution stops unverified; it must not fall back to `/portable` or `origin.txt`. This prevents conflicting metadata from pairing the shortcut with another storage root.

## Components

- Domain: immutable runtime account/algo snapshot and state enums.
- Application: `ITerminalRuntimeInspector` for validated bridge reads and `ITerminalAlgoTradingController` for desired-state control.
- Infrastructure: atomic bridge snapshot reader, process/window matcher, and Ctrl+E sender.
- WPF: row state, account/status labels, and Enable/Disable commands.
- MQL5: source EA distributed with the application plus installation instructions in the UI.

## Error handling and safety

All bridge reads are best effort and isolated per terminal. Invalid records never hide terminal lifecycle/storage state. Algo commands are serialized per terminal. The controller revalidates process identity, window uniqueness, bridge freshness, and current state immediately before input. It never broadcasts Ctrl+E and never sends it to the foreground window without identity matching.

## Testing and verification

TDD covers bridge parsing/freshness/path correlation, account projection, command enablement, desired-state no-op, exact-window targeting, timeout/error behavior, and the `/datadir` conflict regression. Infrastructure tests use fake window/input boundaries; WPF tests verify observable row behavior. The MQL5 source is compile-checked when MetaEditor is available. End-to-end smoke uses an isolated fixture or a non-production terminal and never performs cleanup.
