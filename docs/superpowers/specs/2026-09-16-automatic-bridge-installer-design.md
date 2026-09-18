# Automatic MT5 Bridge Installer

## Goal

Install the bundled `Mt5ManagerBridge.mq5` and a matching compiled `Mt5ManagerBridge.ex5` into a registered terminal without requiring manual file copying or MetaEditor interaction. Attaching the EA to a chart remains an explicit user action.

## Safety boundary

The installer writes only beneath `<verified data directory>\MQL5\Experts`. A missing or unverified data directory, a missing `MQL5` root, or a missing bundled source fails closed before any directory or file is created. Status inspection is read-only.

The bundled source is the source of truth. Source freshness is determined by byte equality rather than a parsed version field. A compiled expert is current only when it exists and its last-write timestamp is at least the installed source timestamp.

## Installation flow

The terminal detail surface displays one of four states: not installed, source outdated, not compiled, or installed and compiled. **Install bridge** performs an explicit per-terminal operation; background refresh only inspects status and never launches MetaEditor.

Installation atomically replaces the source through a temporary sibling file and then compiles it in place. Reinstalling an already-current source and compiled expert is an idempotent no-op. Updating the application-provided source replaces and recompiles the installed bridge.

The compiler is resolved as `MetaEditor64.exe` beside the registered `terminal64.exe`, ensuring that the build matches the target terminal. When MetaEditor is absent, installing the source is still a successful degraded outcome and the UI asks the user to compile it with F7. Compile errors, missing output after a successful compile result, and file-system failures are reported as failures and added to the existing per-terminal operation audit.

## MetaEditor contract

MetaEditor requires a raw command line with individually quoted `/compile:` and `/log:` paths. `ProcessStartInfo.ArgumentList` is not used because its escaping causes MetaEditor to silently skip paths containing spaces.

Exit codes are not a success signal: observed MetaEditor builds return `1` after a successful compile and `0` after a failed compile. The installer instead parses the UTF-16LE compile log and accepts only a `Result: 0 errors` summary plus the resulting `.ex5` file. Compile logs live under `%TEMP%\Mt5Manager\bridge-compile` and are removed after parsing. A 90-second timeout kills the complete compiler process tree.

Compiles are serialized by the singleton installer. This avoids collisions in the shared temporary log area and prevents concurrent MetaEditor launches from racing.

## Integration

- Domain: immutable installation state, status, and result models.
- Application: `IBridgeInstaller` inspection and installation boundary.
- Infrastructure: fail-closed file deployment and the MetaEditor compiler adapter.
- WPF: per-terminal status, install command, result/error projection, and audit integration.

No Telegram command or automatic chart attachment is added.

## Verification

Infrastructure tests cover all state transitions, byte-identical source deployment, atomic-write cleanup, stale compiled output, missing MetaEditor fallback, compile failures, absent output, serialization, and verified-directory refusal. Parser tests use real captured UTF-16LE MetaEditor success and failure logs, plus UTF-8 and BOM-less UTF-16 inputs. WPF tests cover status projection, command eligibility, success/failure auditing, rendered status/button behavior, and DI composition.

The end-to-end smoke installs and compiles in the real registered terminal data directory, verifies idempotent reinstall, compiles successfully through a path containing spaces, and confirms that an unverified directory receives no bridge files.
