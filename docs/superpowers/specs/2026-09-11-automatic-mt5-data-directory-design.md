# Automatic MT5 Data Directory Detection Design

## Goal

Automatically resolve the local data directory for discovered MT5 installations so storage usage can be inspected and cleanup can become available without manual registration, while preserving the rule that cleanup only operates on a uniquely verified directory.

## Resolution order

The resolver evaluates evidence from strongest to weakest:

1. An explicit data-directory argument in a running process or shortcut. The existing command-line parser remains authoritative.
2. Portable mode. When launch arguments explicitly contain `/portable`, the executable directory is accepted only if it has the expected MT5 data structure.
3. Standard per-user storage below `%APPDATA%\MetaQuotes\Terminal`. Candidate directories are matched to the executable installation using MT5 metadata such as `origin.txt`, with canonical, case-insensitive path comparison.

Modification time, directory name, enumeration order, and “most recently used” heuristics are never evidence.

## Verification rules

A candidate is verified only when all conditions hold:

- its path is canonical, local, exists, and is not the filesystem root;
- the expected MT5 directory structure is present;
- portable candidates are backed by an explicit `/portable` argument;
- AppData candidates contain installation metadata that resolves to the discovered executable directory;
- exactly one distinct candidate matches.

Zero matches leave the existing terminal registration unverified. Multiple matches are treated as ambiguous and also remain unverified. Neither case enables cleanup.

## Architecture

Add a focused infrastructure resolver responsible for mapping a terminal candidate to a verified data directory. Discovery sources continue to report observable executable paths and arguments. `TerminalDiscovery` enriches unverified candidates through the resolver before normalization, merge, persistence, and display. Candidates that already carry a verified explicit directory are unchanged.

The resolver receives the AppData terminal root through constructor injection for deterministic tests. Production wiring uses `%APPDATA%\MetaQuotes\Terminal`.

## Merge and persistence

Enrichment occurs before terminal identity is calculated so an unverified discovery result does not remain duplicated beside its newly verified equivalent. Existing verified manual registrations keep their higher source priority. A uniquely enriched result is persisted through the existing registry and is available on subsequent scans.

## Failure handling

Inaccessible files, malformed metadata, nonexistent paths, reparse points, and I/O failures reject only the affected candidate. Discovery continues and leaves the terminal visible but unverified. No exception from best-effort automatic resolution may hide otherwise discoverable terminals.

## User-visible behavior

On the next scan, a uniquely matched terminal changes from `Storage not inspected` to its Logs/Ticks/History usage summary. `Cleanup data…` becomes enabled when no operation is busy. An unresolved or ambiguous terminal remains locked and can still be registered manually.

## Tests

Use temporary isolated directories. Cover explicit directory preservation, unique `origin.txt` match, path case/trailing separator normalization, portable mode, no match, ambiguous matches, malformed/inaccessible candidate isolation, missing MT5 structure, and discovery persistence without duplicates. Existing lifecycle and cleanup suites must remain green. Runtime inspection is deferred at the user's request.
