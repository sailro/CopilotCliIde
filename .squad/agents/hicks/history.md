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

### 2026-04-16 — Full Team Re-Assessment Cross-Confirmation

**Cross-agent findings:** Ripley and Hudson independently confirmed Hicks's H1 regression (cleared-selection push missing) — all three agents flagged the same bug. Ripley: architecture is healthy, three-project boundary clean, RPC contract stable, threading excellent. Hudson: 294 tests passing, 5 coverage gaps (P0–P2). Bishop: zero HIGH issues, 3 MEDIUM concerns (StreamState leak, double MapMcp, error contract). Charters updated to `claude-opus-4.7` model per user directive.

**Status:** H1 re-opened for re-implementation (Hicks author, Hudson re-verify tests). Decisions merged to `decisions.md`; inbox cleared. Session log written.

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

### 2026-03-30 — Complete Extension Codebase Audit

Conducted comprehensive file-by-file audit of all 24 source files in `src/CopilotCliIde/` (~2,125 LOC total). Reviewed threading model, terminal subsystem, connection lifecycle, VsServiceRpc partials, DebouncePusher, and code quality.

**Key findings:**

**Threading model:** Excellent. All DTE/VS service calls properly switch to UI thread. SelectionTracker captures on UI, pushes via Task.Run. DiagnosticTracker uses JTF. OutputLogger correctly uses OutputStringThreadSafe. Only minor issue: TerminalSessionService fires SessionRestarted event off-thread (subscribers must handle).

**Terminal subsystem:** Robust. ConPTY handle lifecycle follows correct disposal order (pseudo-console → pipes → process handles). TerminalProcess uses dedicated read thread with 16ms batching. Thread safety via locks. Edge cases handled (resize before start, process exit, restart, Escape key routing). Settings integration correct (Task.Yield for enum choices, GDI+ monospace detection). One minor gap: startup fallback if GetWorkspaceFolder throws (logs but no retry/fallback).

**Connection lifecycle:** Mostly solid but has **critical leak**: VsServiceRpc._activeDiffs survives RPC disposal (HIGH-1 from 2026-03-10 review). When StopConnection disposes RPC, VsServiceRpc instance is not explicitly disposed — orphaned TCS, InfoBars, temp files. Mitigation: Add IDisposable to VsServiceRpc, cancel all diffs in StopConnection. Secondary issue: ServerProcessManager uses fixed 200ms delay + infinite WaitForConnectionAsync (fragile on slow machines).

**VsServiceRpc partials:** Clean split across 6 files (375 LOC total). State sharing minimal (static _machineId, instance _sessionId, _activeDiffs ConcurrentDictionary). No cross-partial dependencies. GetSelectionAsync (DTE-based) vs SelectionTracker (native APIs) duplication is intentional but creates maintenance burden.

**DebouncePusher:** **Critical race condition** (HIGH-2 from 2026-03-10 review). Lines 11-14 have check-then-act race: concurrent Schedule() calls can both see _timer==null, both create timers, one leaks. Affects DiagnosticTracker where SchedulePush called from arbitrary threads (ITableDataSink callbacks). Fix: Add lock around _timer checks in Schedule/Reset/Dispose.

**Code quality:** No obvious bloat (largest files are inherently complex: Package 310, DiagnosticTracker 232, VsServiceRpc.Diff 217, ConPty 209). Minor issues: 15+ silent catch blocks with /* Ignore */ comment (COM exceptions during teardown — pattern is correct but repetitive), logging format inconsistencies, VsServices.Instance singleton mutability.

**Summary:** 2 HIGH (diff cleanup leak, DebouncePusher race), 3 MEDIUM (terminal startup fallback, RPC timeout, GetSelectionAsync duplication), 9 LOW. Threading model is excellent. Terminal subsystem is solid. Overall codebase is in good shape. Full audit report in `.squad/decisions/inbox/hicks-extension-audit.md`.

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

## Cross-Agent Context — Session 2026-04-12

### Terminal Subsystem Code Review Delivery (PR #7)

Completed comprehensive review of all new files in "feat: Embedded Copilot CLI tool window" PR. Found **2 critical bugs** requiring immediate fixes before release:

1. **UTF-8 multi-byte character corruption (TerminalProcess.cs:103)** — Must replace `Encoding.UTF8.GetString()` with stateful `Decoder` to handle characters split across 4096-byte buffer boundaries.

2. **Focus recovery broken (TerminalToolWindowControl.cs:58)** — Script references nonexistent `window.term`. IIFE creates local `terminal` but never exposes on `window`. Fix: add `window.term = terminal;` in terminal-app.js.

**Important issues (4):** Volatile flag, WebView2 error UI, TerminalSessionService thread sync, ResizeObserver for dock panel resizes.

**Ownership established:** Hicks team charter updated to include terminal subsystem. Terminal routing added to `.squad/routing.md`.

### From Hudson (Test Coverage Assessment)

Hudson assessed ~500 LOC new code with zero test coverage. Priorities:

1. **Create CopilotCliIde.Tests project** — Prerequisite for any extension code testing (flagged since 2026-03-10).
2. **TerminalSessionService is highest-value test target** — 90 LOC, testable with factory extraction, no WebView2/VS dependencies.
3. **TerminalProcess needs integration tests** — Requires Windows CI with ConPTY support.

Recommended minimum viable testing: 8-10 unit tests for TerminalSessionService + 2-3 integration tests for TerminalProcess state machine.

### From Ripley (Documentation Standards)

Ripley completed full documentation audit post-PR#7. Updated:
- copilot-instructions.md with dedicated terminal subsystem section
- CHANGELOG.md with PR #7 feature entry
- README.md Usage section with both terminal options
- team.md dependencies (WebView2, ConPTY, xterm.js)

Established expectation: all future feature PRs include documentation updates to copilot-instructions.md, CHANGELOG.md, README.md before merge.

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

### 2026-07-20 — Fix ServerProcessManager WorkingDirectory (Issue #4)

**Bug:** `ServerProcessManager.StartAsync` launched the MCP server via `dotnet` without setting `WorkingDirectory` in `ProcessStartInfo`. The child process inherited VS's working directory (the open solution/project folder). If that folder contained an `appsettings.json` with Kestrel HTTPS endpoint config, Kestrel threw `System.InvalidOperationException: Call UseKestrelHttpsConfiguration()...` and the process exited immediately — the named pipe was never created.

**Fix:** Added `WorkingDirectory = serverDir` to the `ProcessStartInfo` block. The `serverDir` variable (`McpServer/` under the extension install directory) was already computed on line 13. That directory contains only the published server DLL and dependencies — no `appsettings.json` — so Kestrel starts with its default in-memory configuration.

**Key takeaway:** Child processes launched from VS extensions inherit the solution's working directory, not the extension's install directory. Always set `WorkingDirectory` explicitly when launching helper processes to avoid environment contamination from user projects.

## Cross-Agent Context — Session 2026-03-31

**From Hudson:** Wrote 2 regression tests for issue #4 in ServerWorkingDirectoryTests.cs. All 284 tests passing (282 existing + 2 new). Tests cover: (1) Server starts cleanly with correct WorkingDirectory; (2) Server would have crashed with hostile appsettings.json in inherited directory (regression test).

### 2026-07-20 — Terminal Subsystem Code Review (PR #7)

Reviewed all new/modified files from "feat: Embedded Copilot CLI tool window" PR. Full review filed to `.squad/decisions/inbox/hicks-terminal-review.md`.

**Architecture (new files):**
- `ConPty.cs` — P/Invoke wrapper for Windows ConPTY APIs (CreatePseudoConsole, ResizePseudoConsole, etc.). Includes `Session` IDisposable class managing all native handles with correct teardown order.
- `TerminalProcess.cs` — Manages CLI process via ConPTY. Background read thread + 16ms output batching timer. Clean Dispose with thread join outside lock.
- `TerminalSessionService.cs` — Package-level singleton surviving tool window hide/show. Manages terminal process lifecycle. Registered on `VsServices.Instance.TerminalSession`.
- `TerminalToolWindow.cs` — `ToolWindowPane` shell with `PreProcessMessage` override for keyboard passthrough. Docked tabbed with Output window.
- `TerminalToolWindowControl.cs` — WPF `UserControl` + WebView2 + xterm.js bridge. Deferred init at `ApplicationIdle` priority. Attaches/detaches from session service.
- `Resources/Terminal/` — HTML/JS/CSS for xterm.js frontend. IIFE-scoped JS, debounced resize, WebView2 messaging bridge.

**Critical findings (2):**
1. **UTF-8 multi-byte corruption** (`TerminalProcess.cs:103`): `Encoding.UTF8.GetString()` doesn't handle characters split across `ReadFile` boundaries. Must use `Decoder` with persistent state.
2. **Focus script references nonexistent `window.term`** (`TerminalToolWindowControl.cs:58`): The JS IIFE creates local `terminal` variable, never exposed on `window`. Click-to-focus recovery after F5 is broken.

**Important findings (4):**
1. `_webViewReady` not volatile — read cross-thread without synchronization.
2. No user-facing error when WebView2 runtime missing — stuck "Loading" message.
3. `TerminalSessionService` has no thread synchronization on `_process`.
4. JS uses `window.resize` only — misses VS dock panel splitter resizes (needs `ResizeObserver`).

**Key patterns learned:**
- ConPTY session start deferred until xterm.js sends first resize — avoids dimension mismatch.
- Terminal lifecycle independent of MCP connection lifecycle — no shared state.
- `ProvideToolWindow(Transient = true)` means tool window state not persisted across VS restarts.
- WebView2 resources mapped via `SetVirtualHostNameToFolderMapping` with `https://copilot-cli.local/` virtual host.

### 2026-07-20 — Fix 2 Critical Terminal Bugs

**Bug 1: UTF-8 multi-byte character corruption (TerminalProcess.cs)**
- `Encoding.UTF8.GetString(buffer, 0, bytesRead)` is stateless — each call decodes independently. When a multi-byte UTF-8 sequence (emoji, CJK, accented chars) spans two `ReadFile` boundaries, both chunks produce U+FFFD replacement characters.
- **Fix:** Added persistent `Decoder? _utf8Decoder` field, initialized via `Encoding.UTF8.GetDecoder()` in `Start()`. Replaced `GetString()` call in `ReadLoop` with `_utf8Decoder.GetCharCount()` + `_utf8Decoder.GetChars()`. The `Decoder` maintains internal state across calls — incomplete trailing bytes from one read are buffered and completed by the next read.
- **Pattern:** Always use `Decoder` (not `Encoding.GetString`) when decoding streamed byte data that may arrive in arbitrary chunks. This applies to any pipe/socket/serial read loop.

**Bug 2: Broken focus recovery (terminal-app.js)**
- `TerminalToolWindowControl.cs` calls `ExecuteScriptAsync("window.term.focus()")` to recover keyboard focus after F5 debug cycles. But the xterm.js `Terminal` instance was created as local `var terminal` inside an IIFE — `window.term` was `undefined`, so the call silently failed.
- **Fix:** Added `window.term = terminal;` inside the IIFE, just before the click handler registration. Placement is after all addon loading and initial setup, so the exposed instance is fully initialized.
- **Pattern:** When C# needs to call into WebView2 JS via `ExecuteScriptAsync`, the target must be on `window`. IIFE-scoped variables are unreachable from external scripts.

### 2026-07-20 — Fix 4 Important Terminal Issues (Code Review Follow-up)

Fixed all four 🟡 Important issues from the terminal code review.

**Issue 1: `volatile` on `_webViewReady` (TerminalToolWindowControl.cs)**
- Field written on UI thread (DOMContentLoaded callback) and read on thread pool (OnOutputReceived). Without `volatile`, CPU cache coherence not guaranteed under .NET memory model.
- **Fix:** Added `volatile` keyword to field declaration.

**Issue 2: WebView2 graceful fallback (TerminalToolWindowControl.cs)**
- If WebView2 runtime missing, `CreateAsync` or `EnsureCoreWebView2Async` throws. Previously uncaught — would crash via the outer `DeferredInitialize` handler but with a generic message.
- **Fix:** Wrapped both calls in targeted try-catch blocks. On failure: logs clear message with install URL to Output pane, cleans up partial WebView2 state, returns early. Tool window stays at "Loading Copilot CLI…" placeholder — non-functional but non-crashing.
- **Pattern:** Two separate try-catch blocks because `CreateAsync` failure (no runtime) and `EnsureCoreWebView2Async` failure (runtime found but init fails) need different cleanup — the latter must dispose the already-created WebView2 control.

**Issue 3: Thread sync in TerminalSessionService (TerminalSessionService.cs)**
- `StartSession`/`StopSession`/`RestartSession` race when called from UI thread (tool window resize) vs solution events thread (solution close).
- **Fix:** Added `_processLock` object. Extracted `StopSessionCore()` (lock-free inner method) called from `StopSession` and `StartSession` under lock. `RestartSession` locks then delegates to `StartSession` (safe: C# `Monitor` is reentrant for same thread).
- `WriteInput` and `Resize` left lock-free — they delegate to `TerminalProcess` which handles its own thread safety, and brief stale reference is harmless.

**Issue 4: ResizeObserver (terminal-app.js)**
- `window.resize` event doesn't fire when VS dock panel splitter is dragged — only when the outer Chromium window changes size. Terminal could be clipped.
- **Fix:** Added `ResizeObserver` on `#terminal` container element, sharing the existing debounced fit function. Feature-gated with `typeof ResizeObserver !== "undefined"` for safety. Refactored the anonymous resize handler into named `debouncedFit()` function shared by both `window.resize` and `ResizeObserver`.

**Build:** Server 0 errors, 0 warnings. Roslyn validation clean on both C# files.

### 2026-07-20 — Clarify Tools Menu Item Names (External vs Embedded Terminal)

Renamed both Copilot CLI menu items in the Tools menu to eliminate user confusion:
- "Launch Copilot CLI" → **"Copilot CLI (External Terminal)"**
- "Copilot CLI Window" → **"Copilot CLI (Embedded Terminal)"**

Parallel parenthetical naming so both group together and the distinction is obvious at a glance.

**Files changed:**
- `CopilotCliIdePackage.vsct` — ButtonText for both commands
- `TerminalToolWindow.cs` — Caption property (tool window title bar)
- `CopilotCliIdePackage.cs` — log messages and registration comment
- `README.md` — Usage section and tip
- `CHANGELOG.md` — historical menu references
- `.github/copilot-instructions.md` — terminal subsystem docs

**Key finding:** Menu item text lives in 3 layers: VSCT ButtonText (menu), ToolWindowPane Caption (title bar), and docs. All must stay in sync. The "Loading Copilot CLI…" placeholder text in TerminalToolWindowControl was left as-is — it's transient loading state, not a navigable menu item.

**Build:** Roslyn validation clean on both C# files. Server builds 0 errors, 0 warnings.

### 2026-07-20 — Terminal redraw/re-fit on tab visibility changes

**Bug:** Two visual bugs where xterm.js didn't recalculate dimensions after the tool window tab regained visibility: (1) solution switch → tab switch → come back showed frozen/stale content; (2) close solution → reopen showed line wrapping artifacts from stale terminal state.

**Root cause:** `OnVisibleChanged` only focused WebView2, never triggered `fitAddon.fit()`. Session restarts (from solution switch) didn't clear xterm.js state.

**Fix — three layers:**
1. **JS (`terminal-app.js`):** Exposed `window.fitTerminal()` (zero-dimension guard + fit + sendResize) and `window.resetTerminal()` (terminal.reset + fit). Added `document.visibilitychange` listener with 100ms delay — WebView2 maps WPF visibility to this API, making it the primary re-fit trigger for tab switches.
2. **C# control (`TerminalToolWindowControl.cs`):** `OnVisibleChanged` now calls `ScheduleFitScript()` via `Dispatcher.BeginInvoke(Background)` as belt-and-suspenders. New `OnSessionRestarted` handler dispatches `resetTerminal()` to clear stale content on restart.
3. **C# service (`TerminalSessionService.cs`):** Added `SessionRestarted` event, fired outside `_processLock` after successful restart to avoid deadlocks.

**Key learnings:**
- WebView2 maps WPF `IsVisible` → `document.visibilityState`. The `visibilitychange` event fires on VS tool window tab switches — reliable primary mechanism.
- `fitAddon.fit()` silently no-ops when container has zero dimensions (returns undefined from `proposeDimensions`). The explicit guard in `fitTerminal()` prevents unnecessary `sendResize` calls.
- `terminal.reset()` clears viewport + scrollback + cursor state — correct choice for session restart. `terminal.clear()` only clears scrollback.
- Events from `TerminalSessionService` should fire outside `_processLock` to prevent deadlocks when handlers access the service.

**Build:** Roslyn validation clean (0 errors, 0 warnings). Formatter clean. All 284 server tests pass.

### 2026-07-20 — Reset terminal on solution close instead of killing it

**Bug:** When a solution closed, `OnSolutionAfterClosing` called `_terminalSession?.StopSession()`, killing the ConPTY process. The tool window tab stayed visible but showed a frozen, uninteractable terminal. Users had to wait for a new solution to load before the terminal became usable again.

**Fix in `CopilotCliIdePackage.cs`:** Replaced `StopSession()` with `RestartSession(fallbackDir)` where `fallbackDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)`. This stops the old ConPTY process and immediately starts a fresh one rooted at `%USERPROFILE%`. The `SessionRestarted` event fires, which the tool window control handles by calling `resetTerminal()` in xterm.js — clearing stale content and re-fitting.

**Why user home dir:** When no solution is loaded, the terminal needs a sensible working directory. `UserProfile` (`C:\Users\<name>`) is the standard shell default on Windows and matches what `cmd.exe` and PowerShell use when launched without a specific path. `Directory.GetCurrentDirectory()` was considered but it inherits VS's process CWD which may be stale or unexpected.

**Lifecycle note:** Terminal session lifecycle is independent from MCP connection lifecycle. `StopConnection()` still runs (tears down pipes, lock file, MCP server), but the terminal keeps running. On next solution open, `RestartSession(GetWorkspaceFolder())` resets it to the new solution directory — same as before.

**Build:** Roslyn validation clean (0 errors, 0 warnings). All 284 server tests pass.

### 2026-07-20 — Fix box-drawing character gaps in embedded terminal

**Problem:** Horizontal box-drawing characters (─ U+2500) rendered with visible gaps between cells in the embedded xterm.js terminal (WebView2), while external terminals (Windows Terminal) showed smooth continuous lines.

**Root cause:** xterm.js's default canvas renderer draws each character cell independently on a 2D canvas. Even with `customGlyphs: true` (which is the default in v5.5 and uses custom canvas paths instead of font glyphs), the canvas renderer produces sub-pixel gaps at cell boundaries — especially at non-integer DPI values (1.25x, 1.5x) common on modern displays. This is a known xterm.js limitation.

**Investigation:** Confirmed `@xterm/xterm@^5.5.0` — `customGlyphs` already defaults to `true`, `letterSpacing` defaults to `0`. Toggling these options would have no effect. `rescaleOverlappingGlyphs` (defaults to `false`) handles glyph overflow, not cell gaps. The proper fix is the WebGL renderer addon.

**Fix:** Added `@xterm/addon-webgl` (GPU-accelerated renderer):
- **`package.json`**: Added `@xterm/addon-webgl` dependency
- **`lib/addon-webgl.js`**: Bundled the WebGL addon (247KB, same pattern as addon-fit)
- **`terminal.html`**: Added `<script>` tag for the addon
- **`terminal-app.js`**: Load WebGL addon after `terminal.open()` with try/catch fallback to canvas. Includes `onContextLoss` handler for GPU context recovery.

**Key files:** `src/CopilotCliIde/Resources/Terminal/terminal-app.js`, `terminal.html`, `lib/addon-webgl.js`

**Why WebGL over canvas:** The WebGL renderer uses GPU texture atlas rendering. Each glyph is pre-rendered into a texture atlas, and the GPU draws quads for each cell. Box-drawing characters are rendered as continuous paths in the atlas, not per-cell canvas operations — eliminating the cell-boundary gap issue entirely. Also ~2x faster for scrolling-heavy output.

**Fallback:** If WebGL initialization fails (unlikely in WebView2/Chromium, but possible with disabled GPU), the canvas renderer remains active. The fallback is silent — no error shown to the user.

**Build:** VSIX build clean (MSBuild, 0 errors). `addon-webgl.js` auto-included via `Resources\Terminal\**\*.*` glob in CSPROJ.

### 2026-07-21 — Replace WebView2+xterm.js with native Microsoft.Terminal.Wpf

Completed full migration from WebView2/Chromium/xterm.js terminal rendering to VS's built-in `Microsoft.Terminal.Wpf.TerminalControl` — the same native control used by Windows Terminal and VS's own embedded terminal.

**Key implementation decisions:**

1. **VS-deployed assembly reference (not NuGet):** `Microsoft.Terminal.Wpf.dll` and `Microsoft.Terminal.Control.dll` ship with VS at `CommonExtensions\Microsoft\Terminal\`. Used `<Reference Include="Microsoft.Terminal.Wpf">` with `<HintPath>$(DevEnvDir)CommonExtensions\Microsoft\Terminal\...</HintPath>` and `<Private>false</Private>`. This skips NuGet entirely — no CI package stability risk, no native DLL bundling in VSIX, no assembly resolution gymnastics. The DLL resolves from VS's AppDomain at runtime.

2. **ITerminalConnection bridge:** `TerminalToolWindowControl` implements `ITerminalConnection` directly (matching VS's own `TerminalControl.xaml.cs` pattern). Interface surface: `Start()` (no-op, deferred to Resize), `WriteInput(string)` → `TerminalSessionService.WriteInput()`, `Resize(uint, uint)` → session start or resize, `Close()` (no-op), `TerminalOutput` event ← `OnOutputReceived`. Zero marshaling overhead — direct method calls replace JSON-over-WebView2-messaging.

3. **Theme detection without internal APIs:** VS's `TerminalThemer.cs` uses `IVsColorThemeService` (internal PIA). We can't access that from a third-party extension. Instead, detect dark/light theme by checking luminance of `EnvironmentColors.ToolWindowBackgroundColorKey` using ITU-R BT.601 luma formula. Color tables copied exactly from VS's own `TerminalThemer.cs`.

4. **What was deleted:**
   - `Resources/Terminal/` — terminal.html, terminal-app.js, lib/{xterm.js, xterm.css, addon-fit.js, addon-webgl.js}
   - `Microsoft.Web.WebView2` NuGet from both `Directory.Packages.props` and `CopilotCliIde.csproj`
   - xterm.js npm dependencies from `package.json`
   - All WebView2 init code, JSON messaging, deferred loading, focus recovery hacks

5. **What was preserved:**
   - `TerminalProcess.cs`, `TerminalSessionService.cs`, `ConPty.cs` — unchanged
   - `PreProcessMessage` in `TerminalToolWindow` — still needed for key routing
   - Deferred session start on first Resize — same pattern, simpler path

**API surface (Microsoft.Terminal.Wpf v1.22.0.0):**
- `TerminalControl` — WPF UserControl wrapping native `TerminalContainer` (HwndHost)
- `ITerminalConnection` — Start, WriteInput, Resize, Close, TerminalOutput event
- `TerminalTheme` — ColorTable[16], DefaultBackground/Foreground/SelectionBackground, CursorStyle
- `SetTheme(TerminalTheme, string fontFamily, short fontSize)` — 4th param `Color externalBackground` is optional
- Assembly: `PublicKeyToken=f300afd708cefcd3`, requires `System.Xaml` reference in consuming project

**Build:** MSBuild 0 errors, 0 warnings. All 284 server tests pass. Formatter clean.

### 2026-07-24 — VsInstallRoot for Terminal.Wpf HintPath

Replaced `$(DevEnvDir)` with `$(VsInstallRoot)\Common7\IDE\` in the Microsoft.Terminal.Wpf assembly HintPath. `$(VsInstallRoot)` is set by the VSSDK NuGet targets (Microsoft.VSSDK.BuildTools) which use vswhere internally — works both inside VS IDE and from command-line MSBuild without manual overrides.

This eliminated the `Find VS install path` CI step and `/p:DevEnvDir=...` override from both ci.yml and release.yml. Simpler, less fragile.

**Key insight:** `$(DevEnvDir)` includes the trailing `Common7\IDE\` path segment (e.g., `C:\VS\Common7\IDE\`), while `$(VsInstallRoot)` is just the root (e.g., `C:\VS`). So the HintPath needed `\Common7\IDE\` inserted.

### 2026-07-19 — DebouncePusher Race Condition Fix

Fixed two race conditions in `DebouncePusher.cs`:

1. **TOCTOU on `_timer`:** Two threads calling `Schedule()` simultaneously could both see `_timer == null`, both create a `new Timer(...)`, and one leaks. Fix: create the timer once in the constructor with `Timeout.Infinite` (dormant). `Schedule()` just calls `_timer.Change(200, Timeout.Infinite)` — no null check, no race. Timer is `readonly`.

2. **Unsynchronized `_lastKey`:** Plain `string?` read/written from UI thread and timer callback thread. Fix: marked `volatile` for cross-thread visibility.

3. **`Reset()` vs `Dispose()` separation:** `Reset()` now parks the timer (`Change(Timeout.Infinite, Timeout.Infinite)`) instead of disposing it — the pusher remains reusable after `StopTracking()`. `Dispose()` does the final teardown. This matches `SelectionTracker`'s lifecycle where `Reset()` is called on solution close but `Schedule()` resumes on the next solution open.

**Key insight:** `Timer.Change()` is thread-safe by design — multiple concurrent calls are fine, last writer wins on the due time. This makes the single-timer pattern inherently race-free without any locking.

### 2026-07-20 — Fix Diff Orphaning on Solution Switch (HIGH-1)

Fixed the long-standing HIGH-1 finding from the 2026-03-10 review. On solution switch, `StopConnection()` now properly cleans up all active diffs before tearing down RPC.

**Root cause:** `VsServiceRpc` was created as `new VsServiceRpc()` inline in `StartRpcServerAsync` and passed directly to `JsonRpc.Attach()` — never stored as a field. `StopConnection()` had no reference to it, so `_activeDiffs` entries were orphaned: TCS objects hung until 1-hour timeout, InfoBars stayed visible, temp files leaked, diff frames stayed open.

**Fix (3 parts):**
1. Added `CleanupAllDiffs()` to `VsServiceRpc.Diff.cs` — iterates `_activeDiffs`, sets each TCS result to (Rejected, ClosedViaTool), closes frames, removes InfoBars, deletes temp files, clears the dictionary. Guarded by `ThreadHelper.ThrowIfNotOnUIThread()`.
2. Stored the `VsServiceRpc` instance as `_vsServiceRpc` field in `CopilotCliIdePackage` (previously anonymous `new VsServiceRpc()` in `StartRpcServerAsync`).
3. `StopConnection()` calls `_vsServiceRpc?.CleanupAllDiffs()` as its FIRST action, before disposing RPC/pipes/process. This ensures TCS objects complete before the RPC channel is torn down, giving `OpenDiffAsync` awaits a chance to return cleanly.

**Threading:** `StopConnection()` is always called from UI thread (solution events, `StartConnectionAsync` preamble, `Dispose`). `CleanupAllDiffs()` requires UI thread for `IVsWindowFrame.CloseFrame` and `IVsInfoBarUIElement` operations.

**Build:** Server compiles clean (0 errors, 0 warnings).

### 2026-07-24 — Replace Task.Delay(200) with READY handshake in ServerProcessManager

**Problem:** `ServerProcessManager.StartAsync()` used `await Task.Delay(200)` after spawning the MCP server process — a fragile timing assumption. On slow machines or cold .NET startup, 200ms wasn't always enough. On fast machines, it was wasted time.

**Fix:** Replaced the delay with a proper stdout-based readiness handshake. After `_process.Start()`, the extension reads lines from `StandardOutput` until it sees a line matching `"READY"` (exact, trimmed). Bishop is simultaneously adding `Console.WriteLine("READY")` to the server after Kestrel binds its pipe.

**Implementation details:**
- `WaitForReadySignalAsync(Process)` — static helper, races a `Task.Run` read loop against `Task.Delay(10s)` via `Task.WhenAny`.
- **net472 constraint:** `StreamReader.ReadLineAsync()` has no `CancellationToken` overload on net472. Used `Task.WhenAny` with a timeout task instead of `CancellationTokenSource`.
- **Process exit detection:** If the process exits before READY, `ReadLineAsync()` returns null → throws with exit code.
- **Timeout:** 10 seconds — generous for cold .NET startup, throws `TimeoutException` with clear message.
- **Stdout drain:** After READY, a fire-and-forget `Task.Run` loop drains remaining stdout to prevent the server from blocking on a full output buffer. Suppressed CS4014 with `#pragma warning disable`.

**Key pattern:** When launching a child process that needs time to initialize, never use `Task.Delay` — use a readiness signal on stdout. The parent reads lines until the signal appears, with a timeout. After the signal, drain remaining stdout on a background thread.

**Build:** MSBuild 0 errors, 0 new warnings. Two pre-existing VSTHRD010 warnings in CopilotCliIdePackage.cs (not related).


### 2026-04-16 — Extension Reassessment (Hicks)

Full file-by-file re-audit of src/CopilotCliIde/. Key outcomes:

**Confirmed fixed:**
- DebouncePusher TOCTOU: timer is now created once in constructor with `Timeout.Infinite`, `_lastKey` is `volatile`. Schedule() only calls `_timer.Change()`. No more timer leak on concurrent Schedule.
- Active-diff cleanup on teardown: `StopConnection` → `VsServiceRpc.CleanupAllDiffs` → per-diff `TrySetResult(Rejected, ClosedViaTool)` + InfoBar.Close + Frame.CloseFrame + temp file delete.
- Silent catches mostly replaced by `OutputLogger` calls; remaining `/* Ignore */` blocks are justified (best-effort cleanup paths).

**New / still-open concerns:**
- HIGH: Mid-session "cleared selection" push is missing. UntrackView() doesn't send an empty notification to the CLI, so the CLI keeps a stale selection when user switches to a non-editor window. decisions.md 2026-07-20 describes a PushClearedSelection/PushEmptySelection that is not present in current code — likely a regression during refactor.
- MEDIUM: ServerProcessManager never drains stderr (RedirectStandardError=true but no BeginErrorReadLine) — child can block on full stderr pipe.
- MEDIUM: `timeoutCts` in VsServiceRpc.Diff.cs:47 leaks on any exception between creation and the success-path Dispose at L86. Use `using` or `finally`.
- MEDIUM: `_mcpCallbacks` field has no memory-model guarantees (assigned on RPC task, read from timer callbacks). No observed bug but brittle.
- LOW: `WaitForConnectionAsync` in StartRpcServerAsync has no timeout; TerminalProcess._flushTimer Dispose doesn't wait for in-flight callbacks.

**Takeaways for future work:**
- When removing a push path, double-check the state-machine contract with the CLI (initial state vs. live state flows are different: server-side PushInitialStateAsync covers only new connections).
- Process redirection: always drain both stdout and stderr, or neither. Half-drained processes deadlock on log-heavy failure paths.

Inbox entry: `.squad/decisions/inbox/hicks-reassessment-extension-health.md`
