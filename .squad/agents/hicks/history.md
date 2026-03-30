# Project Context

- **Owner:** Sebastien
- **Project:** CopilotCliIde — A Visual Studio extension (VSIX) bridging GitHub Copilot CLI's /ide command with Visual Studio via MCP over named pipes. Three C# projects: CopilotCliIde (VS extension, net472), CopilotCliIde.Server (MCP server, net10.0), CopilotCliIde.Shared (contracts, netstandard2.0).
- **Stack:** C#, .NET, MSBuild, VSSDK, StreamJsonRpc, MCP, Windows named pipes
- **Created:** 2026-03-05

## Core Context

**Alignment & Protocol:** VS Code's Copilot CLI /ide implementation differs in 5 areas: (1) `get_vscode_info` response schema; (2) `get_selection` field names & nesting; (3) `get_diagnostics` structure (grouped by file, includes range/source/code); (4) `open_diff` resolution values (must be uppercase "SAVED"/"REJECTED" with trigger field); (5) `close_diff` response casing (snake_case). See `.squad/decisions.md` "MCP Tool Schemas & Compatibility" for full details. Key alignments completed: uppercase open_diff values, 200ms selection debounce, diagnostics_changed push notifications, PathUtils URI enforcement.

**Architecture Update (2026-03-29):** Bishop completed AspNet transport baseline refactor. MCP server switched from custom HTTP/MCP stack to ModelContextProtocol.AspNetCore using Kestrel named-pipe hosting. Extension code unchanged — RpcClient connection and callbacks work identically. See `.squad/decisions.md` "Decision: ModelContextProtocol.AspNetCore Transport Baseline" for full scope.

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

### 2026-07-19 — Log Format Consistency & DisplayColumn Fix

Fixed two inconsistencies in `VsServiceRpc.cs`:

**1. Separator character:** Changed all tool log lines from `Tool {name} → {details}` to `Tool {name}: {details}`, matching the push event format (`Push selection_changed: ...`). The `→` arrow is now reserved exclusively for position ranges (`L1:5 → L1:20`), eliminating visual ambiguity.

**2. DisplayColumn vs LineCharOffset:** `GetSelectionAsync` was using `sel.TopPoint.DisplayColumn` which expands tabs to their visual width (e.g. tab=4 columns). SelectionTracker uses buffer offsets where tab=1 character. Switched to `sel.TopPoint.LineCharOffset` (1-based character offset, tab=1) so both paths produce identical column numbers. Key DTE property differences:
- `DisplayColumn`: visual column (tabs expanded to tab-width). Good for cursor display.
- `LineCharOffset`: 1-based character position on the line (tab = 1 char). Matches buffer offset math.
- `VirtualCharOffset`: includes virtual whitespace beyond line end.

**Build:** Server compiles clean (0 errors, 0 warnings). Formatter clean.

### 2026-07-19 — Extension Code Review (CopilotCliIde)

Completed a thorough review of all 11 source files in `src/CopilotCliIde/`. Key structural observations:

**Threading model:** All VS service calls correctly switch to UI thread. `SelectionTracker` captures data on UI thread and pushes via `Task.Run` off-thread. `DebouncePusher` timer callbacks run on thread pool. `DiagnosticTracker` uses `JoinableTaskFactory.RunAsync` to marshal back to UI for snapshot collection.

**Resource lifecycle:** `StopConnection()` is the central teardown point — disposes CTS, RPC, pipe, process, lock file. Called from solution close, solution switch, and package Dispose. `VsServiceRpc` instance created by `JsonRpc.Attach` is NOT explicitly disposed — its `_activeDiffs` dictionary survives RPC teardown.

**Key files and their roles:**
- `CopilotCliIdePackage.cs`: Package lifecycle, solution events, pipe setup (245 lines)
- `SelectionTracker.cs`: IWpfTextView tracking + debounced push (195 lines)
- `VsServiceRpc.cs`: All 6 tool implementations + diff InfoBar flow (398 lines)
- `DiagnosticTracker.cs`: Error List table subscription + push (264 lines)
- `DiagnosticTableSink.cs`: ITableDataSink 14-member impl (122 lines)
- `DebouncePusher.cs`: Reusable 200ms debounce + dedup (36 lines)
- `IdeDiscovery.cs`: Lock file CRUD + stale cleanup (106 lines)
- `ServerProcessManager.cs`: Child process start/kill (56 lines)

**Active diffs are the biggest lifecycle gap:** When `StopConnection()` fires (solution switch), pending `TaskCompletionSource` instances in `VsServiceRpc._activeDiffs` are orphaned. InfoBars remain visible. Temp files are not cleaned.

### 2026-03-10 — VS Extension Improvement Scan & Formal Review

Completed comprehensive review of all 11 source files in `src/CopilotCliIde/`. Produced formal findings report with 2 HIGH, 3 MEDIUM, 9 LOW findings. Report merged to `.squad/decisions.md` "Review Findings — 2026-03-10" section.

**HIGH-priority findings:**
- **HIGH-1 (Active diff cleanup on teardown):** VsServiceRpc._activeDiffs dictionary survives when RPC is disposed during StopConnection. TCS instances orphaned. Impact: pending diffs never complete, MCP server hangs, InfoBars persist, temp files leaked.
- **HIGH-2 (DebouncePusher TOCTOU race):** Two concurrent calls to Schedule() can both see _timer==null, both create timers, one leaks. Affects DiagnosticTracker where SchedulePush is called from arbitrary threads.

**MEDIUM-priority findings:**
- CancellationTokenSource leak in OpenDiffAsync (missing dispose in catch)
- Stale lock file cleanup TOCTOU (deletes mid-write)
- GetSelectionAsync vs SelectionTracker API mismatch (DTE vs native)
- ServerProcessManager Task.Delay(200) readiness fragility

**Cross-references:** HIGH-1 aligns with Ripley's H3 (diff cleanup race). HIGH-2 = Ripley H1 (threading hazard). M3 (GetSelectionAsync) relates to Hudson's extension test coverage gaps. Multiple findings reinforce silent catch block pattern (Ripley H4).

**Decision:** Report filed. Ready for sprint planning and cross-team coordination.

### 2026-03-28 — Shared DiagnosticSeverity constants for extension mapping

Diagnostic severities used by extension diagnostics mapping are now centralized in `CopilotCliIde.Shared.Contracts` via `DiagnosticSeverity` (`Error`, `Warning`, `Information`). `DiagnosticTracker` maps `__VSERRORCATEGORY` to these constants with the same fallback behavior (`Information`) so wire values and protocol behavior stay unchanged while removing local string literals.

### 2026-07-20 — Fix stale file name when all editors close

**Bug:** When all editor tabs were closed (solution still loaded), Copilot CLI continued showing the last opened file name. `SelectionTracker.UntrackView()` nulled `_trackedView` and unsubscribed events but never pushed a "cleared" notification to the MCP server, so the server's cached push state retained the stale file name.

**Fix in `SelectionTracker.cs`:**
- Added `PushClearedSelection()` — schedules a `SelectionNotification` with all-null fields (no file, no selection) through the existing 200ms debouncer with dedup key `"cleared"`.
- Called from `TrackActiveView` when `wpfView == null` (non-editor frame becomes active, e.g., Solution Explorer).
- Called from `OnViewClosed` (editor tab closed) as belt-and-suspenders — covers timing gaps where SEID_WindowFrame may not fire immediately.
- Updated `OnDebounceElapsed` logging: cleared notifications log as `Push selection_changed: (no editor)`, matching the `get_selection` tool log format.

**Key design decisions:**
- Push goes through `DebouncePusher` (not bypassed) so rapid close+reopen doesn't spam the server.
- Dedup key `"cleared"` prevents duplicate cleared pushes when both OnViewClosed and SEID_WindowFrame fire.
- `PushClearedSelection` checks `_getCallbacks() == null` first — safe at startup when RPC isn't connected yet.
- The polled path (`VsServiceRpc.GetSelectionAsync`) was already correct — `dte.ActiveDocument` returns null when no editor is open.

**Cross-team approval (2026-03-30):** Hudson reviewed and approved the fix. Added 3 comprehensive regression tests covering: (1) pull path when no editor active, (2) push path receives cleared notification, (3) consistency between both paths. All 285 tests passing (282 existing + 3 new).

### 2026-07-20 — Fix cleared selection event timing (bad `OnViewClosed` push)

**Bug:** The previous fix (2026-07-20 above) called `PushClearedSelection()` from both `OnViewClosed` and `TrackActiveView(null)`. The `OnViewClosed` call fires for every tab close — including intermediate closes where VS is about to focus another editor. In a 3-file workspace, closing all tabs emitted 3 cleared events when only the final one was correct.

**Root cause:** `OnViewClosed` fires during the view close, *before* VS resolves the next active window. Emitting cleared here is premature — VS will fire `SEID_WindowFrame` moments later with the actual next active frame.

**Fix:** Removed `PushClearedSelection()` from `OnViewClosed`. It now only calls `UntrackView()`. The cleared event is emitted solely from `TrackActiveView` (driven by `SEID_WindowFrame`) when `wpfView == null` — meaning VS has confirmed no editor is active.

**Event ordering (verified):** When a tab closes: (1) `IWpfTextView.Closed` fires → `OnViewClosed` → untrack only. (2) VS picks next active frame → `SEID_WindowFrame` fires → `TrackActiveView` → either tracks new editor (normal selection push) or pushes cleared (last tab). This gives exactly one cleared event when the workspace becomes empty.

**Build:** Server compiles clean. All 285 tests pass.

## Cross-Agent Context — Session 2026-03-30

**From Hudson:** Approved the revised fix. Added 3 regression tests covering 3-file workflow, server transparency, and single-file edge case. All 288 tests passing.

**From Ripley:** Identified the original regression commit `3d17a6f` (2026-03-05 09:49) which removed `PushEmptySelection()` under the incorrect assumption "copilot-cli ignores empty file paths." Your fix (with dual-emit guard in OnViewClosed removed) is the correct implementation.
