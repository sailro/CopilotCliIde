# Project Context

- **Owner:** Sebastien
- **Project:** CopilotCliIde — A Visual Studio extension (VSIX) bridging GitHub Copilot CLI's /ide command with Visual Studio via MCP over named pipes. Three C# projects: CopilotCliIde (VS extension, net472), CopilotCliIde.Server (MCP server, net10.0), CopilotCliIde.Shared (contracts, netstandard2.0).
- **Stack:** C#, .NET, MSBuild, VSSDK, StreamJsonRpc, MCP, Windows named pipes
- **Created:** 2026-03-05

## Core Context

**Alignment & Protocol:** VS Code's Copilot CLI /ide implementation differs in 5 areas: (1) `get_vscode_info` response schema; (2) `get_selection` field names & nesting; (3) `get_diagnostics` structure (grouped by file, includes range/source/code); (4) `open_diff` resolution values (must be uppercase "SAVED"/"REJECTED" with trigger field); (5) `close_diff` response casing (snake_case). See `.squad/decisions.md` "MCP Tool Schemas & Compatibility" for full details. Key alignments completed: uppercase open_diff values, 200ms selection debounce, diagnostics_changed push notifications, PathUtils URI enforcement.

**Architecture Patterns:** Selection & diagnostics tracking follow callback-driven IoC pattern in `SelectionTracker.cs` and `DiagnosticTracker.cs`. Diagnostics feed from two sources: (1) `ITableDataSink` (real-time Roslyn notifications via 14-member interface), (2) Error List reader (on-demand via DTE or periodic). Both converge on `DebouncePusher` with 200ms debounce + content dedup. LogError destinations: `~/.copilot/ide/vs-error-{pid}.log` (errors) and `vs-connection-{pid}.log` (lifecycle).

**Team Rules:** (1) File URIs MUST use `PathUtils.ToVsCodeFileUrl()` + `PathUtils.ToLowerDriveLetter()`, never raw `Uri.ToString()` — enforces protocol requirement (lowercase drive, URL-encoded colon). (2) Severity mapping via `vsBuildErrorLevel.ToProtocolSeverity()` extension method (canonical, centralized). (3) Code style via `npm run format` locally + `npm run format:check` in CI (Husky pre-commit hook for verification). (4) All team members run `dotnet test` before pushing server changes — tool name tests enforce MCP schema compatibility.

**Build & Test:** Extension builds with MSBuild (not dotnet build): `msbuild src/CopilotCliIde/CopilotCliIde.csproj /p:Configuration=Debug`. Server: `dotnet build src/CopilotCliIde.Server/`. Tests: `dotnet test src/CopilotCliIde.Server.Tests/` (153 tests, xUnit v3, Central Package Management). VSIX deployment requires real MSBuild (dotnet msbuild limitation).

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->

### 2026-07-19 — Contextual Tool Result Logging in VsServiceRpc

Enhanced all 6 tool methods in `VsServiceRpc.cs` to log result details, matching the richness of push event logging in SelectionTracker and DiagnosticTracker.

**Pattern:** Single log line per tool invocation at RESULT time (not invocation time), except `open_diff` which keeps both (since it blocks for a long time). Arrow `→` prefix visually links to the tool name.

**Log formats implemented:**
- `Tool get_selection → VsServiceRpc.cs L96:1` / `→ (no editor)` — mirrors SelectionTracker line 194 format exactly
- `Tool get_diagnostics → 1 file(s), 2 diagnostic(s)` / `→ error: {msg}` — mirrors DiagnosticTracker line 160 format
- `Tool get_vscode_info → SolutionName, 3 project(s)` / `→ (no solution)`
- `Tool open_diff: tabName (file)` (invocation) + `→ SAVED (accepted_via_button)` (result) / `→ error: {msg}`
- `Tool close_diff → closed` / `→ already closed` / `→ error: {msg}`
- `Tool read_file → Program.cs (42 total, 20 returned)` / `→ error: {msg}`

**Key implementation notes:**
- `Path.GetFileName()` used for file names in logs (not full paths, matching push style)
- Selection log uses 1-based line/col (adds 1 to 0-based DTO values), matching push format
- Null-safe throughout — `?.` and `?? 0` operators prevent crashes in logging
- Error paths log with `→ error:` prefix for grep-ability

**Build:** Server compiles clean (0 errors, 0 warnings). Formatter clean.
