# Release Rules
<!-- last-analyzed: 2026-09-18T00:00:00Z -->

## Version Sources
- `src/Mt5Manager.Wpf/Mt5Manager.Wpf.csproj`: `Version`, `AssemblyVersion`, and `FileVersion` MSBuild properties.

## Release Trigger
Manual annotated `vX.Y.Z` tag and GitHub Release creation. No CI workflow exists.

## Test Gate
Run `dotnet test Mt5Manager.sln --configuration Release` and require all projects to pass before publishing.

## Registry / Distribution
GitHub Releases with Windows x64 self-contained and framework-dependent ZIP assets.

## Release Notes Strategy
User-facing Markdown notes grouped by features, safety/reliability, and interface improvements. Conventional Commit history may supplement notes.

## CI Workflow Files
None.

## First-Time Setup Gaps
- No automated release workflow; publishing is manual through GitHub CLI.
