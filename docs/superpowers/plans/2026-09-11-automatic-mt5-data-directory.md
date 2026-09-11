# Automatic MT5 Data Directory Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Resolve a discovered MT5 executable to exactly one evidence-backed data directory and persist the enriched terminal registration.

**Architecture:** A focused `Mt5DataDirectoryResolver` validates explicit, portable, and AppData metadata candidates. `TerminalDiscovery` invokes it only for unverified candidates before normalization and identity merging; dependency injection supplies the production resolver.

**Tech Stack:** .NET 8, C#, Windows filesystem conventions, xUnit, FluentAssertions

---

### Task 1: Conservative resolver

**Files:**
- Create: `src/Mt5Manager.Infrastructure/Discovery/Mt5DataDirectoryResolver.cs`
- Create: `tests/Mt5Manager.Infrastructure.Tests/Discovery/Mt5DataDirectoryResolverTests.cs`

- [ ] Write failing tests for unique origin metadata, explicit portable mode, missing structure, and ambiguous metadata.
- [ ] Run the focused tests and confirm failures are caused by the missing resolver.
- [ ] Implement canonical local-path validation, structural validation, metadata matching, and exactly-one semantics.
- [ ] Run focused tests and confirm they pass.

### Task 2: Discovery enrichment

**Files:**
- Modify: `src/Mt5Manager.Infrastructure/Discovery/TerminalDiscovery.cs`
- Modify: `src/Mt5Manager.Wpf/App.xaml.cs`
- Modify: `tests/Mt5Manager.Infrastructure.Tests/Discovery/TerminalDiscoveryTests.cs`

- [ ] Write a failing test proving an unverified terminal is enriched before deduplication and saved once.
- [ ] Run the focused test and confirm it fails because discovery does not call the resolver.
- [ ] Inject the resolver into `TerminalDiscovery`, enrich unverified candidates before merge, and register it in WPF DI.
- [ ] Run all discovery tests and confirm they pass.

### Task 3: Verification

**Files:**
- Verify: `Mt5Manager.sln`
- Publish: `artifacts/publish/Mt5Manager.Wpf.exe`

- [ ] Run the full Release test suite and require zero failures.
- [ ] Run the Release build with zero warnings and errors.
- [ ] Publish the win-x64 profile.
- [ ] Commit the implementation.
