# MT5 Safety and Telegram Ownership Design

## Goal

Reduce destructive-operation risk, make Global Algo Trading blast radius explicit, ensure only one active Telegram long-poll consumer uses a bot token, and show the VPS/RDP public IPv4 address on Telegram dashboards when available.

## Scope

This change covers:

1. Stop, Restart, Cleanup, and Force safeguards for terminal registrations.
2. WPF and Telegram disclosure for Global Algo Trading changes.
3. Telegram HTTP 409 duplicate-consumer handling.
4. Public IPv4 lookup for `/start`, `/menu`, and `/status` dashboards.

It does not introduce a distributed database, central Telegram gateway, command coalescing, or IPv6 display.

## Destructive operation safety

A destructive operation requires a non-empty verified data directory. The WPF layer disables Stop, Restart, and Cleanup for unverified registrations. Cleanup retains its existing application-layer checks.

`WindowsTerminalProcessController.StopAsync` is the final enforcement boundary. It rejects an unverified or invalid registration before enumerating or controlling processes. Destructive process matching requires both canonical executable path and canonical data directory. A candidate whose command line or data-directory identity cannot be read is not accepted. The existing sole-candidate/executable-only fallback must not authorize destructive control.

Non-destructive state discovery may continue reporting an approximate or error state, but must not weaken destructive matching.

## Global Algo Trading disclosure

ON/OFF retains the existing Ctrl+E behavior and therefore changes MT5 Global Algo Trading. The WPF terminal surface and Telegram confirmation messages must state that the action affects every EA in the selected terminal. Bulk confirmation must state that every EA in every listed terminal is affected.

The warning is required before execution; success messages need not repeat it.

## Telegram token ownership

Telegram HTTP 409 is classified as a duplicate-consumer conflict. The polling service transitions to a dedicated `Conflict` state, exits the polling loop, and does not retry automatically. Cleanup code must preserve `Conflict` rather than overwriting it with `Stopped`.

WPF renders an actionable status: the bot token is being used by another MT5 Manager application or VPS. Applying settings or explicitly starting the service initiates a new ownership attempt.

This is reactive enforcement by Telegram, not a proactive distributed lease. Two instances can briefly contend at startup; after Telegram rejects one poller, the rejected application stops consuming updates.

## Public IPv4 dashboard metadata

A focused `IPublicIpProvider` abstraction supplies an optional IPv4 string. Its infrastructure implementation:

- uses HTTPS;
- has a short bounded lookup timeout of approximately two seconds;
- validates the response with `IPAddress.TryParse` and accepts only `AddressFamily.InterNetwork`;
- caches a successful result for the process lifetime;
- returns `null` for transport, timeout, HTTP, malformed, or IPv6-only responses;
- does not log or persist the address.

`TelegramBotService` requests this metadata while preparing `/start`, `/menu`, and `/status`. Lookup failure never blocks dashboard delivery. The dashboard displays `IP Publik VPS: tidak tersedia` when no valid IPv4 is available.

Only the configured allowed chat can reach dashboard handling, preserving the existing authorization boundary.

## Error handling

- Unverified destructive request: fail closed with a clear error and no process control.
- Unreadable candidate identity: refuse destructive control.
- Telegram conflict: stop polling and retain conflict state.
- IPv4 lookup failure: return `null`; send the dashboard with a safe fallback.
- Cancellation: propagate caller/service cancellation; lookup timeout is converted to unavailable metadata unless the caller cancellation token itself was canceled.

## Tests and verification

Tests are written first and observed failing before production changes.

Required contracts:

1. Unverified WPF rows disable Stop, Restart, and Cleanup.
2. `StopAsync` rejects unverified registrations without closing or killing a process.
3. Destructive matching refuses unreadable or ambiguous data-directory identity.
4. Terminal and bulk Telegram confirmations disclose the Global Algo Trading impact on every EA.
5. WPF exposes the same global-impact warning near its controls.
6. Telegram HTTP 409 maps to the conflict error kind.
7. The polling service enters `Conflict`, exits, and does not retry.
8. WPF maps `Conflict` to an actionable status.
9. The public-IP provider accepts IPv4, rejects IPv6/malformed responses, bounds lookup time, and caches success.
10. Dashboard commands show the IPv4 or `tidak tersedia`, and lookup failure does not suppress delivery.

After targeted red/green cycles, run the complete Release solution test suite and smoke the changed application surfaces where feasible.
