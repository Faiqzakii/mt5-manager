# Terminal Master–Detail UI Design

## Problem

The main window renders every terminal as a full-height control card inside one `ListView`. Account, Algo Trading, storage, and cleanup controls multiply the card height. The resulting scroll surface clips lower content and can leave buttons unreachable.

## Decision

Replace the stacked cards with a desktop master–detail layout. The left pane occupies roughly 32% of available width and contains the terminal list. The right pane occupies roughly 68% and contains controls for one selected terminal. A `GridSplitter` allows bounded resizing without letting either pane become unusable.

## Left pane

The left pane owns terminal navigation and its vertical scrollbar. Each compact row shows:

- display name;
- running state and PID;
- account summary when the bridge snapshot exists;
- `EA bridge unavailable` when no snapshot exists;
- concise global Algo Trading state.

Rows use explicit selected, keyboard-focus, hover, and disabled states. Long values trim with a tooltip rather than increasing row height without limit.

## Right pane

The detail pane binds to `MainViewModel.SelectedTerminal`. It contains:

1. terminal name, state, and PID;
2. Start, Stop, and Restart actions;
3. active account and complete Algo Trading permission summary;
4. Enable Algo and Disable Algo actions;
5. executable and verified data-directory paths;
6. storage summary and Cleanup action;
7. last operation result and inline error.

The pane is not part of the terminal list. A single internal vertical `ScrollViewer` is allowed only for constrained window heights, ensuring every action remains reachable. Horizontal scrolling is disabled.

## Selection behavior

- After discovery, preserve selection by terminal registration ID when that terminal remains visible.
- Otherwise select the first visible terminal.
- Filtering preserves the current selection if it remains in the filtered collection; otherwise select the first result.
- An empty filtered collection sets selection to null and shows a clear empty state in the detail pane.
- Background state refresh updates existing selected row data without changing selection.

## Header and commands

Search, Scan terminals, and Register terminal remain above both panes. Cleanup opens for the selected row through the existing row-bound button data context. Existing command enablement and safety rules remain unchanged.

## Layout constraints

- Window minimum remains usable at 820 × 560.
- Left pane minimum width: 250 device-independent pixels.
- Right pane minimum width: 430 device-independent pixels.
- Splitter width: 6 device-independent pixels.
- Default ratio: approximately 32:68.
- Only the list and constrained detail content scroll; no nested vertical scrolling.

## Verification

- View-model tests cover initial selection, selection preservation after refresh, and filter fallback.
- Existing account/Algo/terminal command tests remain green.
- WPF window is launched against fixture terminal data and visually inspected at default and minimum window sizes.
- Keyboard selection and access to Start, Stop, Restart, Algo, and Cleanup controls are exercised without touching a production MT5 process.
