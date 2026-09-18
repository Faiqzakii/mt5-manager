# Terminal Account and Algo Trading Control Design

## Goal

Show the active MT5 account for every detected terminal and provide a safe per-terminal shortcut for changing the terminal-wide Algo Trading state.

## Scope

The feature targets MetaTrader 5. “Algo Trading” means the global terminal permission represented by `TERMINAL_TRADE_ALLOWED` and the MT5 toolbar/Ctrl+E command. It does not independently enable or disable individual Expert Advisors or charts. The manager drives that command by posting the toolbar command identifier `WM_COMMAND 32851` to the matched window, which is equivalent to Ctrl+E and does not require the window to be foreground or restored.

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

The bridge writes through `FILE_COMMON` using a temporary record, rotates the previous final record to `.bak`, and moves the temporary record into place. Because MQL5 does not provide a replace-existing atomic rename, the writer restores a stranded backup before the next rotation and the manager may read `.bak` only when the final record is absent or invalid. Each terminal writes a distinct record keyed by a stable hash of its canonical data path. No credentials, passwords, or trading commands are stored.

The manager applies the same full validation to either record: the protocol must be supported, the timestamp fresh, and the reported canonical data path equal to the terminal’s verified data directory. A stale, malformed, or mismatched final and backup produces `Bridge unavailable` or `Unknown`; it never falls back to account files, window captions, or process memory.

## Account presentation

Each terminal card shows login, account name, server, and account type when a valid snapshot exists. Account state is volatile and is not persisted in `TerminalRegistration` or used as terminal identity. Refreshing the dashboard rereads the snapshot.

## Global Algo Trading control

The manager exposes an Enable/Disable action per running terminal. The action is enabled only when:

- the terminal has a verified data directory;
- a fresh bridge snapshot matches that directory;
- exactly one running top-level MT5 window matches the terminal process;
- the current global Algo Trading state is known;
- no operation is already in progress.

If the requested state already equals the reported state, the action is a no-op. Otherwise the manager posts the Algo Trading command identifier to the exact matched terminal window as `WM_COMMAND 32851`, the same command the toolbar button and Ctrl+E invoke. The post does not require foreground ownership, visibility, or a restored window, so the action succeeds while the terminal is minimized, occluded, on a locked desktop, or on a disconnected RDP session. The command is posted exactly once because it is a toggle. It then polls the bridge snapshot until `TERMINAL_TRADE_ALLOWED` equals the requested state or a timeout of 10 seconds expires, exceeding the bridge's 3-second write cadence. Success is reported only after observed state change. Ambiguous windows, a failed post, stale bridge data, or timeout fail closed with a visible error; a confirmation timeout never triggers a second post.

The manager does not claim that global Algo Trading alone makes an EA able to trade. The dashboard separately displays terminal, EA, account, and connection permission indicators from the bridge.

## Explicit data-directory correction

Shortcut `/datadir` arguments are parsed before portable or AppData-origin resolution. A structurally valid explicit directory is authoritative. If an explicit target exists but is invalid, resolution stops unverified; it must not fall back to `/portable` or `origin.txt`. This prevents conflicting metadata from pairing the shortcut with another storage root.

## Components

- Domain: immutable runtime account/algo snapshot and state enums.
- Application: `ITerminalRuntimeInspector` for validated bridge reads and `ITerminalAlgoTradingController` for desired-state control.
- Infrastructure: validated bridge snapshot reader with backup recovery, process/window matcher, and `WM_COMMAND 32851` command poster.
- WPF: row state, account/status labels, and Enable/Disable commands.
- MQL5: source EA distributed with the application plus installation instructions in the UI.

## Error handling and safety

All bridge reads are best effort and isolated per terminal. Invalid records never hide terminal lifecycle/storage state. Algo commands are serialized per terminal. The controller revalidates process identity, window uniqueness, bridge freshness, and current state immediately before posting. It never broadcasts the command and never posts it to a window that was not identity-matched to the target process.

## Testing and verification

TDD covers bridge parsing/freshness/path correlation, account projection, command enablement, desired-state no-op, exact-window targeting, single-post toggle semantics, timeout/error behavior, and the `/datadir` conflict regression. Infrastructure tests use fake window/command boundaries; WPF tests verify observable row behavior. The MQL5 source is compile-checked when MetaEditor is available. End-to-end smoke uses an isolated fixture or a non-production terminal and never performs cleanup.
