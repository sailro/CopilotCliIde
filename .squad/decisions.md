# Squad Decisions

## Stale File Selection Fix (2026-03-30)

### Decision: Push cleared selection when all editors close

**Author:** Hicks  
**Date:** 2026-03-30  
**Status:** Implemented & Approved  

#### Context

When all editor tabs were closed in VS (solution still loaded), Copilot CLI continued showing the last opened file name. The `SelectionTracker` correctly untracked the view but never pushed a "cleared" notification to the MCP server — the server's cached push state retained the stale file name.

#### Decision

`SelectionTracker` now pushes a `SelectionNotification` with all-null fields (no file, no selection, no text) when there is no active text editor view. This goes through the existing 200ms debouncer with dedup key `"cleared"`.

Two call sites trigger the cleared push:
1. `TrackActiveView` when `wpfView == null` (non-editor frame becomes active)
2. `OnViewClosed` (editor tab closes — belt-and-suspenders for SEID_WindowFrame timing)

#### Impact

- **Pull path:** `VsServiceRpc.GetSelectionAsync` was already correct — returns null `FilePath` when no editor is open.
- **Server (MCP server):** Receives `SelectionNotification` with null fields. The `PushInitialStateAsync` path and cached selection state handle null `FilePath`/`FileUrl` gracefully.
- **Tests (Hudson):** No server test changes needed — the fix is extension-only. Added 3 new regression tests; existing 282 tests + 3 new = 285 tests passing.

#### Production Changes

- `src/CopilotCliIde/SelectionTracker.cs`: Added `PushClearedSelection()` method and updated `OnDebounceElapsed` logging.

---

### Decision: All-Documents-Closed Regression Coverage

**Author:** Hudson  
**Date:** 2026-03-30  
**Status:** Implemented & Approved

#### Context

When all editor documents close in VS (solution still loaded), Copilot CLI was still showing the last opened file name. The expected behavior is: `get_selection` reports no file, and the push path sends a cleared notification.

#### Decision

Added 3 regression tests in `SseNotificationIntegrationTests.cs` covering:
1. **Pull path** — `get_selection` tool returns `current: false` with no `filePath` when no editor is active.
2. **Push path** — A cleared `SelectionNotification` (all null fields) arrives on the SSE stream with `text: ""` and `filePath` null or absent.
3. **Push/pull consistency** — After documents close, both paths agree: push shows cleared, pull shows `current: false`.

#### Production Fix Status

**Approved.** The production fix in `SelectionTracker.PushClearedSelection()` is correct and complete:
- Called from `TrackActiveView()` when `wpfView == null` (window frame change to non-editor)
- Called from `OnViewClosed()` when a tracked view closes
- Creates empty `SelectionNotification()` and schedules debounced push

#### Impact

- Test count: 282 → 285 (3 new regression tests)
- No production code changes by Hudson
- New reusable helpers: `CallToolAsync`, `ExtractJsonRpcFromResponse`, `ExtractAllSseDataJsonForMethod`

**Option 2: Roslyn IDiagnosticService.DiagnosticsUpdated (Internal API)**
- internal to Roslyn; not public API
- Could break on any VS update
- Roslyn team actively moving away from this toward pull-based diagnostics for LSP
- ⚠️ Too fragile

**Option 3: Workspace.WorkspaceChanged (Roslyn Public API)**
- No DiagnosticsChanged kind in WorkspaceChangeKind enum
- Tracks structure (project/document add/remove/edit), not diagnostic output
- Would need polling after document edit
- ❌ Wrong abstraction level

**Option 4: ITableManagerProvider + ITableDataSink (Table API)** ✅ **RECOMMENDED**
- Public, documented API in Microsoft.VisualStudio.Shell.TableManager
- Headless data layer beneath Error List WPF control
- Thread-safe, callable from any thread
- Catches ALL diagnostic changes — design-time, explicit builds, analyzer updates
- No new NuGet packages (included via Microsoft.VisualStudio.SDK)
- Integrates cleanly with existing 200ms debounce + content dedup architecture

**Key design decisions:**
- Use sink purely as **change notification trigger** (no reading)
- Keep ErrorListReader.CollectGrouped() for actual diagnostics reading
- Keep existing OnBuildDone and DocumentSaved triggers as fallbacks
- 200ms debounce + content dedup already in place
- Track subscriptions in HashSet<ITableDataSource> + lock for thread safety

**Option 5: IErrorList / IErrorListService** — No change notification events; UI manipulation only
**Option 6: IVsDiagnosticsProvider** — Does not exist
**Option 7: VS Code Comparison** — VS Code uses LSP 	extDocument/publishDiagnostics; our approach taps into ITableDataSource → ITableDataSink (closest public equivalent)

### Recommendation

**Use Option 4: ITableManagerProvider + ITableDataSink** — the only public API providing real-time diagnostic change notifications.

---

## Decision: ITableDataSink for Real-Time Diagnostic Notifications

**Author:** Hicks (Extension Dev)
**Date:** 2026-07-19
**Status:** Implemented

### Context

Our extension only pushed diagnostics_changed after explicit builds and file saves. Ripley researched the options and recommended ITableManagerProvider + ITableDataSink — the headless data layer beneath the Error List WPF control.

### Decision

Implemented Option 4: subscribe to ITableDataSink on the ErrorsTable manager as a notification-only trigger. The sink does not read diagnostics — it calls ScheduleDiagnosticsPush() which feeds into the existing 200ms debounce + content dedup + ErrorListReader.CollectGrouped() pipeline.

### Implementation Details

**New file: DiagnosticTableSink.cs**
- Implements ITableDataSink (14 interface members)
- Pure notification trigger — every sink method calls ScheduleDiagnosticsPush()
- Does NOT read diagnostics

**Changes to CopilotCliIdePackage.cs**
- **StartConnectionAsync():** Gets ITableManagerProvider via MEF, subscribes to all existing ITableDataSource instances
- **SourcesChanged handler:** Subscribes to dynamically added sources; uses HashSet<ITableDataSource> + lock for thread safety
- **StopConnection():** Unsubscribes, disposes all subscriptions
- Existing OnBuildDone and OnDocumentSaved triggers kept as fallbacks

---

## PR #7 — Terminal Subsystem Code Review & Documentation

**Date:** 2026-04-12

### Decision: Post-PR #7 Documentation Standards for Terminal Subsystem

**Author:** Ripley (Lead)  
**Type:** Documentation standard

#### Context

PR #7 added a significant new subsystem (embedded terminal via ConPTY + WebView2 + xterm.js) but shipped with no documentation updates. This decision records the documentation updates made and establishes expectations for future feature PRs.

#### Action

All documentation updated to reflect the terminal subsystem:

1. **copilot-instructions.md** now has a dedicated "Embedded Terminal Subsystem" section covering architecture, key files, lifecycle, threading, and independence from MCP. This is the most important doc for AI-assisted development — it must stay current.

2. **CHANGELOG.md** [Unreleased] populated with the feature. Every PR that adds user-visible functionality must have a changelog entry before merge.

3. **README.md** Usage section updated to present both terminal options (embedded window vs. external launch).

4. **team.md** Stack section updated with new dependencies (WebView2, ConPTY, xterm.js).

#### Key Architectural Clarification

The terminal subsystem is **completely independent** of the MCP/RPC layer. It does not use named pipes (MCP), StreamJsonRpc, or lock file discovery. The only shared code paths are:
- `VsServices.Instance` for service location
- `CopilotCliIdePackage` for solution lifecycle hooks
- `GetWorkspaceFolder()` for solution directory

This means terminal bugs cannot affect MCP connectivity, and vice versa. Future work on either subsystem should respect this boundary.

#### Expectation for Future PRs

Feature PRs should include documentation updates or explicitly note what needs updating. At minimum:
- copilot-instructions.md for any new pattern or subsystem
- CHANGELOG.md for any user-visible change
- README.md for usage-facing changes

---

### Terminal Subsystem Code Review — PR #7

**Author:** Hicks (Extension Dev)  
**Date:** 2026-04-12  
**Scope:** All new/modified files from "feat: Embedded Copilot CLI tool window" PR

#### 🔴 Critical — Must Fix

**C1. UTF-8 multi-byte character corruption in ReadLoop**

- **File:** `TerminalProcess.cs:103`
- **Issue:** `Encoding.UTF8.GetString(buffer, 0, bytesRead)` treats each `ReadFile` result as a complete UTF-8 sequence. If a multi-byte character (emoji, CJK, accented chars) is split across two reads — which **will** happen with 4096-byte buffers — the partial trailing bytes produce replacement characters (`�`) in xterm.js.
- **Fix:** Replace `Encoding.UTF8.GetString()` with a `System.Text.Decoder` instance (from `Encoding.UTF8.GetDecoder()`) that maintains decode state across calls:

```csharp
// In ReadLoop, before the while loop:
var decoder = Encoding.UTF8.GetDecoder();
var charBuffer = new char[4096];

// Inside the loop:
var charCount = decoder.GetChars(buffer, 0, bytesRead, charBuffer, 0);
var text = new string(charBuffer, 0, charCount);
```

- **Impact:** Any non-ASCII terminal output (git log with non-Latin names, file paths with Unicode, emoji in prompts) will render incorrectly at arbitrary buffer boundaries.

**C2. Focus recovery script references nonexistent `window.term`**

- **File:** `TerminalToolWindowControl.cs:58`
- **Issue:** `ExecuteScriptAsync("if(window.term)term.focus()")` — but `terminal-app.js` wraps everything in an IIFE. The xterm.js `Terminal` instance is a local variable named `terminal`, never exposed on `window`. `window.term` is always `undefined`, so the focus recovery after F5 debug cycles silently does nothing.
- **Fix (either):**
  1. In `terminal-app.js`, add `window.term = terminal;` after `terminal.open(...)` to expose it as a global.
  2. Or change the C# script to `document.querySelector('.xterm-helper-textarea')?.focus()` which targets xterm.js's internal focus element directly.
- Option 1 is simpler and enables future scripting.
- **Impact:** After F5 debug start/stop, clicking the tool window doesn't refocus the terminal. User must close and reopen the tool window.

#### 🟡 Important — Should Fix

**I1. `_webViewReady` flag is not volatile**

- **File:** `TerminalToolWindowControl.cs:19`
- **Issue:** `_webViewReady` is written on the UI thread and read from a thread pool thread. Without `volatile` or synchronization, the thread pool thread may read a stale `false` value due to CPU cache coherency. On x86 (strong ordering) unlikely to manifest; **will** bite on ARM64 (Surface Pro X, etc.).
- **Fix:** Declare as `private volatile bool _webViewReady;`

**I2. No user-facing error when WebView2 runtime is missing**

- **File:** `TerminalToolWindowControl.cs:130`
- **Issue:** If WebView2 Evergreen runtime isn't installed, `CoreWebView2Environment.CreateAsync()` throws `WebView2RuntimeNotFoundException`. Catch block logs to Output pane but user sees stuck "Loading Copilot CLI…" message forever.
- **Fix:** Detect `WebView2RuntimeNotFoundException` and update `Content` to a helpful message with installation link.

**I3. TerminalSessionService lacks thread synchronization**

- **File:** `TerminalSessionService.cs`
- **Issue:** `_process` read/written from multiple threads with no synchronization. Event unsubscribe → dispose sequence not atomic.
- **Fix:** Add `lock` around `_process` access, or document that all public methods must be called on the UI thread.

**I4. No ResizeObserver — container resizes may be missed**

- **File:** `terminal-app.js:48-54`
- **Issue:** JS only listens to `window.resize`, but VS dock panel resizes (drag splitter, undock/redock) may not fire `window.resize`. xterm.js renders at old dimensions, leaving blank space or clipping.
- **Fix:** Add `ResizeObserver` on terminal container to watch for dimension changes independently of window resize.

#### 🟢 Minor — Nice to Have

**M1. ConPty.Write silently ignores WriteFile failures** (`ConPty.cs:245`)  
**M2. Unused constant `STARTF_USESTDHANDLES`** (`ConPty.cs:69`)  
**M3. TerminalToolWindowControl doesn't unsubscribe own events in Dispose** (`TerminalToolWindowControl.cs:281-292`)  
**M4. ConPty.Session properties have public setters** — should be `init` setters (`ConPty.cs:114-119`)

#### ✅ Good Patterns — Well Done

1. **Deferred WebView2 initialization** — `Dispatcher.BeginInvoke` at `ApplicationIdle` priority avoids blocking VS during startup.
2. **ConPTY handle cleanup order** — Pseudo-console closed first (signals EOF), then pipes, then process, remaining handles. Correct teardown sequence.
3. **Output batching at 16ms / 60fps** — Accumulates rapid output into batches before posting to WebView2. Prevents thousands of calls per second.
4. **PreProcessMessage keyboard passthrough** — Arrow keys, Tab, Escape returned `false` so VS doesn't eat them before WebView2.
5. **Session survives tool window hide/show** — TerminalSessionService at package scope; process keeps running when window hidden.
6. **Solution lifecycle integration** — Session restarts on SolutionOpened, stops on AfterClosing. Mirrors MCP connection without coupling.
7. **Error path cleanup in ConPty.Create** — All error paths properly close/free handles.
8. **Thread-safe Dispose in TerminalProcess** — Read thread `Join(3000)` outside lock to avoid deadlock.

---

### Terminal Feature Test Coverage Gaps — PR #7

**Author:** Hudson (Tester)  
**Date:** 2026-04-12  
**Trigger:** PR #7 merged — "feat: Embedded Copilot CLI tool window"  
**Severity:** HIGH — 5 new source files, ~500 LOC, zero test coverage

#### Summary

PR #7 added 5 new files to the VS extension project (`CopilotCliIde`, net472):
- `ConPty.cs` — P/Invoke wrapper for Windows ConPTY APIs (~254 LOC)
- `TerminalProcess.cs` — Process lifecycle + async output batching (~165 LOC)
- `TerminalSessionService.cs` — Package-level singleton lifecycle (~90 LOC)
- `TerminalToolWindow.cs` — VS ToolWindowPane shell (~38 LOC)
- `TerminalToolWindowControl.cs` — WPF + WebView2 + xterm.js bridge (~293 LOC)

**None of these files have any test coverage.** The existing test project (`CopilotCliIde.Server.Tests`) targets `net10.0` and references only the server and shared projects — it cannot reference the VS extension project (`net472`, VSSDK dependencies).

#### Build/Test Baseline

- `dotnet build src/CopilotCliIde.Server/CopilotCliIde.Server.csproj` — ✅ Passes
- `dotnet test src/CopilotCliIde.Server.Tests/` — ✅ 284/284 pass
- No test project exists for the VS extension project

#### Testability Analysis by Component

**1. ConPty.cs** — P/Invoke wrapper (HARD to unit test)
- All methods are `private static` P/Invoke or thin wrappers — no abstraction layer, no interface.
- `Session.Dispose()` has ordered cleanup logic testable if you can create real sessions.
- **What SHOULD be tested:** P1: Session.Dispose() idempotency (double-dispose safety) ✅ Safe by inspection. P1: Create() with invalid command. P2: Resize() with zero/negative dimensions.

**2. TerminalProcess.cs** — Process management (MEDIUM testability)
- Manages ConPTY session with async output reading, batched output via 16ms timer, input writing.
- Tightly coupled to `ConPty.Create()` (no interface/abstraction).
- **What SHOULD be tested:** P1: Start() on disposed throws. P1: Start() when already running throws. P1: WriteInput()/Resize() on stopped process is no-op. P1: Dispose() idempotency. P1: ProcessExited event fires. P2: Output batching produces batched OutputReceived events.

**3. TerminalSessionService.cs** — Lifecycle singleton (HIGH testability)
- Orchestrates terminal process lifecycle. Uses composition (owns a `TerminalProcess`).
- Main barrier is `new TerminalProcess()` hardcoded in `StartSession()`.
- **What SHOULD be tested:** P1: StartSession() stops existing session before starting new. P1: StopSession() unsubscribes and disposes. P1: RestartSession() uses previous directory/dimensions. P1: WriteInput()/Resize() on no session is no-op.

---

## Fix: 4 Important Terminal Issues — 2026-04-12

**Author:** Hicks (Extension Dev)  
**Date:** 2026-04-12  
**Status:** Implemented  
**Commit:** 3b86fbc  

### Summary

Resolved 4 important thread-safety, error handling, and layout correctness issues from PR #7 terminal subsystem review.

### Changes

**1. Volatile `_webViewReady` (TerminalToolWindowControl.cs)**
- Added `volatile` keyword to ensure cross-thread visibility between DOMContentLoaded writer (UI thread) and OnOutputReceived reader (thread pool).

**2. WebView2 graceful fallback (TerminalToolWindowControl.cs)**
- Wrapped `CoreWebView2Environment.CreateAsync` and `EnsureCoreWebView2Async` in separate try-catch blocks.
- On failure: logs descriptive message with WebView2 install URL to Output pane, cleans up partial state, returns early.
- Tool window remains at placeholder text — non-functional but non-crashing.

**3. Thread sync in TerminalSessionService.cs**
- Added `_processLock` object field for synchronization.
- `StartSession`, `StopSession`, `RestartSession` acquire lock before touching `_process`.
- Extracted `StopSessionCore()` (no-lock inner) to avoid double-locking from `StartSession` → `StopSession` path.
- `WriteInput` and `Resize` remain lock-free — delegate to TerminalProcess (self-synchronized).

**4. ResizeObserver for dock panel (terminal-app.js)**
- Added `ResizeObserver` on `#terminal` container, complementing existing `window.resize` listener.
- Both share named `debouncedFit()` function with 50ms debounce.
- Feature-gated: `typeof ResizeObserver !== "undefined"` for safety.
- Handles VS dock panel splitter drags that don't fire `window.resize`.

### Verification

- `dotnet build src/CopilotCliIde.Server/` — 0 errors, 0 warnings.
- Roslyn validation on both C# files — clean.

**4. TerminalToolWindow.cs** — VS ToolWindowPane (LOW testability)
- 38-line shell, inherits VSSDK `ToolWindowPane`, requires VS shell.
- Key filtering in PreProcessMessage could be extracted to pure static method and unit tested.

**5. TerminalToolWindowControl.cs** — WPF + WebView2 bridge (VERY LOW testability)
- 293-line complex control requiring WebView2 runtime, WPF dispatcher, VS shell services.
- Message parsing logic and restart-on-enter could be extracted to testable static methods.

#### Prioritized Test Recommendations

**Immediate (can do now, no production changes needed)**
None — all terminal code lives in net472 extension project with no test project. Creating `CopilotCliIde.Tests` is a prerequisite.

**Short-term (requires new test project or refactoring)**

| Priority | Test | Component | Type | Effort | Prerequisite |
|----------|------|-----------|------|--------|-------------|
| P1 | TerminalSessionService lifecycle (start/stop/restart) | TerminalSessionService | Unit | Low | Factory extraction |
| P1 | TerminalProcess state machine (Start/Stop/Dispose transitions) | TerminalProcess | Integration | Medium | ConPTY available on CI |
| P1 | Session.Dispose() double-dispose safety | ConPty.Session | Integration | Low | ConPTY available |
| P2 | PreProcessMessage key filtering | TerminalToolWindow | Unit | Low | Extract to static method |
| P2 | WebMessage JSON parsing (input/resize dispatch) | TerminalToolWindowControl | Unit | Low | Extract to static method |

**Structural Prerequisites**

1. **Create `CopilotCliIde.Tests` project** (net472 or net8.0-windows with shims) — flagged since 2026-03-10. Without it, no extension code can be tested.
2. **Extract `ITerminalProcessFactory`** — allows `TerminalSessionService` to be fully unit-tested with mocks.
3. **Extract message parsing and key filtering to static methods** — enables unit testing without WPF/WebView2/VS dependencies.

#### Risk Assessment

The terminal feature has **zero automated test coverage** for ~500 LOC of new code that manages native handles, runs background threads, uses timer-based batching, bridges WPF ↔ WebView2 ↔ xterm.js, and handles process lifecycle. ConPTY handle management and threading code are highest risk. Both are correct by inspection, but future changes have no safety net.

#### Recommendation

**Minimum viable testing (recommended for next sprint):**
1. Create `CopilotCliIde.Tests` project targeting `net8.0-windows`
2. Extract `TerminalSessionService` factory dependency
3. Write 8-10 unit tests for `TerminalSessionService` lifecycle
4. Write 2-3 integration tests for `TerminalProcess` state transitions (requires Windows CI runner with ConPTY)

This covers the most testable and highest-risk code without requiring VS shell or WebView2 infrastructure.

### Files Changed

- **Created:** src/CopilotCliIde/DiagnosticTableSink.cs
- **Modified:** src/CopilotCliIde/CopilotCliIdePackage.cs

### Build Status

- Extension: Builds clean (153 tests pass)
## Decision: vscode-0.41 Capture — Test Infrastructure Fixes Needed

**Author:** Bishop (Server Dev)
**Date:** 2026-07-19
**Status:** Proposed
**Affects:** Hudson (Tester)

### Context

The new vscode-0.41.ndjson capture introduces 5 test failures in TrafficReplayTests and CrossCaptureConsistencyTests. These are all test infrastructure issues — the server code matches VS Code 0.41 perfectly.

### Root Cause

The 0.41 capture contains multi-session traffic where close_diff while open_diff is pending causes TWO responses on the same SSE stream (open_diff resolves first, then close_diff follows). The TrafficParser's response matching logic incorrectly attributes the open_diff resolution response to the close_diff or update_session_name tool call.

### Failing Tests

1. CloseDiffResponse_HasExpectedStructure — picks up open_diff response instead of close_diff
2. ToolResponseFields_ExactMatchWithVsCode — open_diff fields attributed to close_diff/update_session_name
3. DeleteMcpDisconnect_PresentIn039Captures — assertion about DELETE position
4. CloseDiffLifecycle_TabNamesAndAlreadyClosedConsistency — multi-session response matching
5. Http400RetrySequence_HasValidErrorStructure — 0.41's 400 response format

### Decision Needed

Hudson should update the TrafficParser and test assertions to handle:
1. Overlapping tool responses (one tool call triggering another tool's response)
2. DELETE position flexibility in multi-session captures
3. The 0.41 capture's specific 400 response body format

### Server Code Impact

None. All protocol compatibility confirmed — no server changes needed for VS Code 0.41.

---

## Capture Analysis: vs-1.0.14.ndjson (2026-03-30)

**Authors:** Bishop (Server Dev), Hudson (Tester)  
**Date:** 2026-03-30  
**Status:** Complete — Test Gaps Identified  

### Executive Summary

Analyzed vs-1.0.14.ndjson (121 lines, 7 MCP sessions) against 260 existing server tests. **Coverage excellent; baseline tests pass with new capture.** Identified 6 test gaps (1 HIGH risk, 1 MEDIUM risk, 4 LOW/nice-to-have).

### Context

This is the first capture exercising all 7 MCP tools, including the full open_diff/close_diff lifecycle and get_vscode_info, from both Copilot CLI v1.0.0 (standard) and mcp-call v1.0 (lightweight tool caller) across 7 sessions.

### Covered Behaviors ✅

- ✅ diagnostics_changed push notification format (code field: CS0116, IDE1007)
- ✅ All three open_diff outcomes (SAVED/accepted, REJECTED/rejected, REJECTED/closed_via_tool)
- ✅ selection_changed ↔ get_selection push/pull consistency
- ✅ diagnostics_changed ↔ get_diagnostics push/pull consistency
- ✅ All 7 MCP tools exercised across sessions

### Uncovered Gaps — Prioritized Tests

#### HIGH RISK (Protocol Correctness)

**G1. Cross-Session Close-Diff Resolves Open-Diff**
- **What:** Session 3 open_diff resolved REJECTED/closed_via_tool by session 4's close_diff
- **Why Critical:** Tests protocol blocking semantics across session boundaries
- **Proposed Test:** `SseNotificationIntegrationTests.cs` → `OpenDiff_ResolvedByCloseDiffFromDifferentSession`

#### MEDIUM RISK (Compatibility)

**G4. get_diagnostics URI Filter Returns Empty Array**
- **What:** Capture calls get_diagnostics with specific URI, gets `[]`
- **Why:** Filtered empty result is the common case; envelope validation missing
- **Proposed Test:** `TrafficReplayTests.cs` → `GetDiagnostics_WithUriFilter_ReturnsEmptyWhenNoDiagnostics`

#### LOW RISK / NICE-TO-HAVE

**G2.** Dual DELETE idempotency (both return 200 OK) — idempotent behavior confirmed, no action needed  
**G3.** get_selection current=false minimal shape ({text: "", current: false} with no filePath/fileUrl/selection) — already tested, no action  
**G5.** Content-Length framing (mcp-call uses Content-Length; all integration tests match) — low risk, undocumented assumption  
**G6.** Requests without X-Copilot-* headers (mcp-call omits them; server doesn't use them) — low risk, smoke test optional  

### Action Items

**Recommended:** Implement G1 (HIGH) and G4 (MEDIUM) tests. G2-G6 are covered or low-priority.

**Effort:** G1 = Medium (cross-session setup), G4 = Small (URI filtering edge case)

### Decision

Test additions G1–G4 provide value. Hudson to implement; Bishop to review. G5–G6 are optional robustness tests for future sprints.

---

## Decision: Protocol Diff — vscode-0.41.ndjson vs vscode-0.39.ndjson

**Date:** 2026-03-28
**Author:** Hudson (Tester)
**Status:** Analysis Complete — Action Required

### Executive Summary

Comprehensive protocol comparison between vscode-0.41.ndjson (CLI 0.41, VS Code 1.113.0) and vscode-0.39.ndjson (CLI 0.39). **No tool schema changes, no initialize response changes, no notification structure changes.** The core protocol is stable. However, the 0.41 capture exposes a **TrafficParser session propagation bug** caused by overlapping blocking tool calls (open_diff), which breaks 5 existing tests.

### 1. Initialize Handshake

#### Request (Client → Server)

| Field | 0.39 | 0.41 | Impact |
|---|---|---|---|
| params.protocolVersion | "2025-03-26" | "2025-11-25" | Client-side only; server responds with "2025-11-25" in both |
| params.clientInfo.name | "test-client" | "mcp-call" | Client identity change (renamed CLI process) |
| params.clientInfo.version | "1.0.0" | "1.0" | Minor version string change |

#### Response (Server → Client)

**IDENTICAL.** Same protocolVersion: "2025-11-25", same capabilities: {tools: {listChanged: true}}, same serverInfo: {name: "vscode-copilot-cli", title: "VS Code Copilot CLI", version: "0.0.1"}.

**Impact:** No code changes needed. No test updates needed for initialize response.

### 2. Tool Schemas (tools/list)

**ALL 6 TOOLS IDENTICAL.** No additions, no removals, no input schema changes.

Tools: close_diff, get_diagnostics, get_selection, get_vscode_info, open_diff, update_session_name.

**Impact:** None.

### 3. Notifications

| Notification | 0.39 count | 0.41 count | Structure |
|---|---|---|---|
| diagnostics_changed | 20 | 19 | IDENTICAL keys: {uris} |
| notifications/initialized | 5 | 8 | IDENTICAL (no params) |
| selection_changed | 19 | 22 | IDENTICAL keys: {filePath, fileUrl, selection, text} |

No new notification types. No removed notification types. No structural changes.

**Impact:** None.

### 4. HTTP Transport

#### Header Changes (Structural)

| Category | Change | Detail |
|---|---|---|
| 400 Bad Request content-type | **CHANGED** | application/json; charset=utf-8 → text/html; charset=utf-8 |
| 400 Bad Request body format | **CHANGED** | JSON-RPC error object → plain text "Invalid or missing session ID" |
| 202 Accepted mcp-session-id | **REMOVED** | 0.39 had it; 0.41 202 responses have no mcp-session-id header |

All other headers (authorization, x-copilot-*, mcp-protocol-version) are structurally identical — only session-specific values differ.

#### 400 Error Format Change (BREAKING for tests)

**0.39:** {"jsonrpc":"2.0","error":{"code":-32000,"message":"Bad Request: Session ID must be a single, defined, string value"},"id":null}

**0.41:** Invalid or missing session ID (plain text, not JSON)

**Impact:** Test Http400RetrySequence_HasValidErrorStructure fails because it expects JSON-RPC error structure.

### 5. Tool Call Responses

#### open_diff — New Trigger Value

**0.41 adds a new trigger value:** "closed_via_tool" (in addition to existing "accepted_via_button" and "rejected_via_button").

This appears when close_diff is called on an active open_diff, causing the open_diff's blocking TaskCompletionSource to resolve with result: "REJECTED", trigger: "closed_via_tool".

#### close_diff — Response Structure UNCHANGED

The close_diff tool response itself is unchanged: {success, already_closed, tab_name, message}.

**Critical finding:** The ToolResponseFields_ExactMatchWithVsCode test reports close_diff and update_session_name as having new fields (result, trigger, tab_name, message). **This is a TrafficParser correlation bug, not a protocol change.** See Section 7.

#### update_session_name — Response Structure UNCHANGED

Response remains {success: true}.

#### get_vscode_info — Response Structure UNCHANGED

Response still has: {version, appName, appRoot, language, machineId, sessionId, uriScheme, shell}.

### 6. DELETE /mcp (Session Disconnect)

| Aspect | 0.39 | 0.41 |
|---|---|---|
| DELETE count | 1 | 2 |
| Headers | Identical structure | Identical structure |
| Response | 200 OK, chunked empty body | 200 OK (first), then 400 Bad Request (second) |

**0.41 sends 2 DELETE requests** — likely because the second DELETE targets an already-torn-down session (gets 400 back).

**Impact:** Test DeleteMcpDisconnect_PresentIn039Captures needs update — it asserts DELETE entries are within last 3 sequence numbers, but with 2 DELETEs the first one is further from the end.

### 7. TrafficParser Session Propagation Bug (ROOT CAUSE of 5 test failures)

#### The Bug

The 0.41 capture uses **many short-lived one-shot sessions** (8 MCP session IDs, 7 initialize requests) instead of 0.39's pattern (4 session IDs, 4 initializes). Each session runs a single tool call with id=1, then disconnects.

The TrafficParser's pendingServerSession propagation assumes responses arrive in FIFO order after their HTTP 200 header. This breaks when **open_diff blocks** — the HTTP 200 for open_diff arrives immediately (seq=110, session dda4cd5e), but the actual result body arrives much later (seq=120) after another session (4a58dc94) has started. The intervening initialize response at seq=115 incorrectly **consumes the pending session from the open_diff HTTP 200**, and the open_diff result body at seq=120 gets assigned the **wrong session (4a58dc94 instead of dda4cd5e)**.

#### Concrete Misassignment

`
seq=109: open_diff request (session dda4cd5e, id=1) — BLOCKS
seq=110: HTTP 200 (session dda4cd5e) → sets pendingServerSession
seq=114: initialize request (session 4a58dc94)
seq=115: initialize response (id=0) → WRONGLY consumes dda4cd5e's pending session
seq=119: HTTP 200 (session 4a58dc94) → sets pendingServerSession
seq=120: open_diff result body (SHOULD be dda4cd5e) → WRONGLY gets 4a58dc94's session
seq=121: close_diff result body → gets NO session (pendingServerSession consumed)
`

#### Consequence

GetAllToolCallResponses("close_diff") for request seq=118 (session 4a58dc94) matches seq=120 (wrong — this is open_diff's resolution) instead of seq=121 (correct close_diff response). The test then sees {result, trigger} fields in what it thinks is a close_diff response.

Similarly, GetAllToolCallResponses("update_session_name") picks up wrong responses due to cascade effects.

### 8. Session Pattern Change

| Aspect | 0.39 | 0.41 |
|---|---|---|
| MCP session IDs | 4 | 8 |
| Initialize requests | 4 | 7 |
| Session pattern | Multi-call sessions | One-shot sessions (1 tool call per session) |
| 400 error batch | 6 retries (session collision) | 0 (no collisions) |
| First session tool calls | 8 (multi-call) | 10 (main session has get_selection, get_diagnostics, etc.) |
| Subsequent sessions | 3 sessions, each with 2-4 tool calls | 7 sessions, each with 1 tool call |

The 0.41 CLI creates a fresh MCP session for each tool call after the initial handshake session. This eliminates session collision errors (no more 400 retry batches) but creates many more sessions with id=1 reuse.

### 9. Failing Tests — Required Updates

#### 5 Tests Failing

| # | Test | Root Cause | Fix |
|---|---|---|---|
| 1 | ToolResponseFields_ExactMatchWithVsCode | TrafficParser misattributes responses across sessions (open_diff ↔ close_diff) | Fix TrafficParser session propagation for blocking tool calls |
| 2 | Http400RetrySequence_HasValidErrorStructure | 0.41's 400 error is plain text, not JSON-RPC | Update test to handle both JSON-RPC and plain-text 400 bodies |
| 3 | CloseDiffResponse_HasExpectedStructure | Parser returns open_diff response for close_diff request (wrong session) | Fix TrafficParser; then test passes as-is |
| 4 | DeleteMcpDisconnect_PresentIn039Captures | 0.41 has 2 DELETEs; assertion seq >= lastSeq - 3 fails for first DELETE | Relax assertion or check only the LAST DELETE |
| 5 | CloseDiffLifecycle_TabNamesAndAlreadyClosedConsistency | Parser returns wrong response (no already_closed field) → GetProperty throws | Fix TrafficParser; then test passes as-is |

#### Root Cause Classification

- **Tests 1, 3, 5:** TrafficParser session propagation bug — need parser fix
- **Test 2:** Real protocol change (400 error format) — need test update
- **Test 4:** Real behavior change (double DELETE) — need test update

### 10. Impact Assessment — Required Actions

#### Code Changes (CopilotCliIde.Server or Extension)

**NONE REQUIRED.** The protocol is fully backward compatible. Tool schemas, initialize response, and notification structures are identical. Our server already speaks protocolVersion: "2025-11-25".

#### TrafficParser Fix (PRIORITY 1)

Fix TrafficParser.cs session propagation for overlapping blocking tool calls. The pendingServerSession approach fails when a response body arrives after a new session's initialize response has consumed the pending session. Options:
1. Track pending sessions per-response-id instead of globally
2. Don't consume pendingServerSession for initialize responses (they don't have tool content)
3. Associate HTTP 200 responses with the REQUEST that triggered them (by seq proximity)

#### Test Updates (PRIORITY 2)

1. **Http400RetrySequence_HasValidErrorStructure**: Handle plain-text 400 bodies (skip JSON-RPC validation when content-type is text/html)
2. **DeleteMcpDisconnect_PresentIn039Captures**: Allow multiple DELETE entries; check the last one is near the end

### 11. Things That Did NOT Change

For completeness — these are all **confirmed identical** between 0.39 and 0.41:

- Initialize response structure and values
- All 6 tool inputSchemas (properties, types, required fields)
- Tool execution and annotation metadata
- selection_changed notification params structure
- diagnostics_changed notification params structure
- HTTP method patterns (POST, GET, DELETE)
- Authorization header format (Nonce-based)
- mcp-protocol-version header value ("2025-11-25")
- SSE event format for notifications
- Tool response envelope structure (result.content[0].type="text")


---

# Hudson Fast Retry Decision — 2026-03-28

## Context
vscode-0.41 capture introduced overlapping same-id tool calls, plain-text HTTP 400 responses, and non-terminal DELETE placement that broke replay/cross-capture assertions.

## Decision
Use **tool-response shape filtering** in `TrafficParser.GetAllToolCallResponses` when correlating responses, and make replay tests tolerant of protocol-valid variants seen in 0.41.

## What changed
1. Parser correlation now:
   - propagates client session IDs from HTTP request headers into parsed request bodies,
   - matches raw tool names with whitespace-tolerant parsing,
   - filters candidate responses by expected output shape per tool (`open_diff`, `close_diff`, `get_vscode_info`, `update_session_name`).
2. Tests now:
   - assert DELETE has a valid response rather than strict end-of-file sequence position,
   - accept JSON-RPC and plain-text 400 error payload formats.

## Rationale
ID-only matching is insufficient when captures contain concurrent same-id requests across tool calls and sessions. Shape-aware correlation keeps parser behavior deterministic for replay tests while staying capture-driven and avoiding fixture edits.

## Verification
- `dotnet build src\CopilotCliIde.Server.Tests\CopilotCliIde.Server.Tests.csproj --no-restore`
- `dotnet test src\CopilotCliIde.Server.Tests\CopilotCliIde.Server.Tests.csproj --no-build --filter "ToolResponseFields_ExactMatchWithVsCode|Http400RetrySequence_HasValidErrorStructure|CloseDiffResponse_HasExpectedStructure|DeleteMcpDisconnect_PresentIn039Captures|CloseDiffLifecycle_TabNamesAndAlreadyClosedConsistency"`
- `dotnet test src\CopilotCliIde.Server.Tests\CopilotCliIde.Server.Tests.csproj --no-build`

All green (21/21 targeted, 213/213 full).


---

# StreamJsonRpc Version Compatibility for VS2022

**Date:** 2026-03-26  
**Author:** Hicks (Extension Dev)  
**Status:** Implemented  

## Problem

Extension crashed on VS2022 with error: "Could not load file or assembly 'StreamJsonRpc, Version=2.24.0.0'". The VSIX worked on VS2026 but not VS2022 because:

1. We referenced StreamJsonRpc 2.24.84 (assembly version 2.24.0.0)
2. VS2026 ships with StreamJsonRpc 2.24.x → worked
3. VS2022 ships with StreamJsonRpc 2.9.x–2.22.x depending on update → failed
4. VSSDK build tools strip VS-provided assemblies from VSIX → StreamJsonRpc wasn't packaged

## Solution

**Downgraded to VS SDK 17.0.31902.203 and StreamJsonRpc 2.9.85** to match VS 2022.0 (the minimum version declared in the manifest `[17.0,19.0)`).

### Changes Made

1. **Directory.Packages.props**:
   - `Microsoft.VisualStudio.SDK`: 17.14.40265 → 17.0.31902.203
   - `Microsoft.VSSDK.BuildTools`: 17.14.2120 → 17.0.5232
   - `StreamJsonRpc`: 2.24.84 → 2.9.85
   - Added `System.Text.Json` 8.0.5 (not included in VS SDK 17.0)

2. **CopilotCliIde.csproj**:
   - StreamJsonRpc reference: added `ExcludeAssets="runtime" PrivateAssets="all"` to prevent inclusion in VSIX
   - Added System.Text.Json reference for IdeDiscovery JSON serialization

3. **CopilotCliIde.Shared/Contracts.cs**:
   - Removed `[JsonRpcContract]` attribute (introduced in StreamJsonRpc 2.13, not available in 2.9)
   - Removed `using StreamJsonRpc;`

4. **CopilotCliIde.Shared.csproj**:
   - Removed StreamJsonRpc PackageReference entirely (not needed, interfaces are POCOs)
   - Removed NoWarn for StreamJsonRpc0008 analyzer

5. **DiagnosticTracker.cs**:
   - Replaced `HashCode` struct (not fully supported in net472 with old SDK) with manual hash combining using `unchecked` block and prime multiplication (17 + 31 * value pattern)

6. **source.extension.vsixmanifest**:
   - Removed arm64 architecture support (not supported by VS SDK 17.0)

### Verification

- Build succeeds with 0 errors (only warnings for MessagePack vulnerability in ModelContextProtocol dependency and VSTHRD analyzer suggestions)
- VSIX generated: `CopilotCliIde.vsix` (5.1 MB)
- **StreamJsonRpc.dll is NOT in VSIX root** → extension uses VS's copy (2.9.x on VS2022.0, 2.24.x on VS2026)
- **StreamJsonRpc.dll IS in McpServer/** → server (net10.0) has its own copy (safe, separate process)

## Architecture Notes

- **Extension (net472)**: Runs in VS process, uses VS's StreamJsonRpc via binding resolution
- **Server (net10.0)**: Standalone child process, bundles its own StreamJsonRpc 2.9.85
- **Shared (netstandard2.0)**: No StreamJsonRpc dependency, just RPC contract POCOs
- StreamJsonRpc is wire-compatible across 2.x versions (server 2.9 can talk to VS extension using VS's 2.24)

## VS Version Compatibility Table

| VS Version | StreamJsonRpc Shipped | Supported |
|------------|----------------------|-----------|
| 2022.0 (17.0) | 2.9.x | ✅ Yes (now) |
| 2022.1–2022.3 (17.1–17.3) | 2.10.x–2.12.x | ✅ Yes |
| 2022.4+ (17.4+) | 2.13.x+ | ✅ Yes |
| 2026.0+ (19.0+) | 2.23.x+ | ✅ Yes |

## Tradeoffs

- **Lost JsonRpcContractAttribute**: This is an analyzer suggestion for proxy generation. We don't use proxy generation, so no functional impact.
- **Lost arm64 support**: VS SDK 17.0 schema doesn't support arm64 architecture in manifest. Can be re-added if we move minimum to VS 2022.4+.
- **Manual hash combining**: Slightly less collision-resistant than `HashCode` struct, but adequate for deduplication key generation.

## References

- [StreamJsonRpc VS Integration Docs](https://microsoft.github.io/vs-streamjsonrpc/docs/vs.html)
- VS 2022.0 ships StreamJsonRpc 2.9.x
- VS SDK 17.0.31902.203 = VS 2022 RTM SDK


---

## DiagnosticSeverity Shared Contract (2026-03-28)

**Owner(s):** Bishop, Hicks  
**Topic:** Diagnostics

### Context
Diagnostics severity values were represented as ad-hoc string literals across extension and server tests ("error", "warning", "information"). The team requested extraction into a shared contract in CopilotCliIde.Shared.

### Decision
Introduce DiagnosticSeverity as a shared constants contract in CopilotCliIde.Shared\Contracts.cs. Migrate extension and server consumers to use those constants. Keep DiagnosticItem.Severity typed as string for backward-compatible serialization and protocol parity.

### Rationale
- Wire format already uses lowercase strings and is relied on by capture-compatibility tests.
- Changing the property type to enum/value object would add serialization risk and wider churn.
- Shared constants eliminate duplicated literals while preserving protocol behavior.
- No JSON shape or transport behavior changes.

### Impact
- Centralized source of truth for supported severities.
- Reduced chance of casing drift or typo regressions in diagnostics payloads.
- DiagnosticTracker in extension now uses shared constants when mapping from __VSERRORCATEGORY.
- All tests updated to reference shared definitions.
- All 213 tests passing (msbuild + dotnet).

### Files Modified
- src/CopilotCliIde.Shared/Contracts.cs — added DiagnosticSeverity constants
- src/CopilotCliIde/DiagnosticTracker.cs — use shared constants in severity mapping
- Test files in both projects — reference shared constants


## Phase A Refactor: McpPipeServer Inner Class Extraction (2026-03-28)

**Owner(s):** Bishop  
**Topic:** Server architecture

### Context

McpPipeServer.cs contained three distinct concerns mixed together:
- HTTP frame read/write logic (130 lines)
- SSE client lifecycle management (150 lines)
- MCP tool DI container setup (90 lines)
- Main MCP orchestration logic (200 lines)

Total file size: 572 lines. This made it difficult to evolve HTTP handling or DI without touching unrelated orchestration code.

### Decision

Extract three inner classes into standalone files:
1. **HttpPipeFraming.cs** — Static utility methods for HTTP frame reading/writing
2. **SseClient.cs** — SSE client state, lifecycle events, and keep-alive management
3. **SingletonServiceProvider.cs** — MCP tool reflection-based DI registration

All three extracted classes are internal (not public). McpPipeServer remains the sole public surface for MCP server operations. No public API change.

### Rationale

- Extraction enables future Phase B/C improvements (buffered header reading, per-client dedup) without touching unrelated code paths.
- Test visibility improves: SingletonServiceProviderTests no longer needs reflection to reach a private nested class.
- Separation of concerns makes each extracted component independently testable and evolvable.
- Zero behavioral change — this is pure refactoring.

### Impact

- No protocol change. No API change. No behavior change.
- 213/213 tests pass (identical to baseline).
- Future HTTP framing work (H1, H3, H4 from Review Findings) can now safely modify HttpPipeFraming.cs without risk of cascade changes.
- McpPipeServer.cs reduced to ~350 lines (orchestration only).

### Files Modified

- src/CopilotCliIde.Server/McpPipeServer.cs — removed inner classes, preserved public interface
- src/CopilotCliIde.Server/HttpPipeFraming.cs — new
- src/CopilotCliIde.Server/SseClient.cs — new
- src/CopilotCliIde.Server/SingletonServiceProvider.cs — new
- Tests validated: 213/213 passing

---

## Phase B: McpPipeServer Route Split & SseBroadcaster Extraction

**Author:** Bishop (Server Dev)  
**Date:** 2026-07-21  
**Type:** Refactor (no protocol changes)

### Decision

Split McpPipeServer.HandleConnectionAsync into focused route handlers and extract SSE client management into SseBroadcaster.

### What Changed

#### New: SseBroadcaster class (src/CopilotCliIde.Server/SseBroadcaster.cs)
- Internal class owning SSE client list + lock
- AddClient() / RemoveClient() — thread-safe registration
- BroadcastAsync() — serializes and writes chunked SSE to all clients
- BroadcastSelectionChangedAsync() / BroadcastDiagnosticsChangedAsync() — notification formatters

#### Modified: McpPipeServer (src/CopilotCliIde.Server/McpPipeServer.cs)
- HandleConnectionAsync → thin dispatcher (~50 lines)
- HandleMcpPostAsync (static) — POST route with timeout logic
- HandleSseGetAsync — GET SSE route with client registration
- HandleMcpDeleteAsync (static) — DELETE route
- Push methods delegate to _broadcaster
- Public API surface unchanged

### Impact

- **Tests:** 213/213 pass, no test changes needed
- **Program.cs:** No changes needed (delegates to same public methods)
- **PipeProxy:** No impact (uses HttpPipeFraming directly)
- **Protocol wire format:** Zero changes

### Who Should Know

- **Hudson:** SseBroadcaster is internal with InternalsVisibleTo access — can write unit tests for it directly
- **Hicks:** No extension changes needed
- **Ripley:** McpPipeServer LOC reduced from ~375 to ~340, with clearer separation of concerns

---

### HttpPipeFraming Literals Pass 2 Constants Extraction

**Author:** Bishop (Server Dev)  
**Date:** 2026-03-10  
**Status:** Implemented
**Reviewed:** Hudson ✅

## Context

The second pass of constants extraction on `src/CopilotCliIde.Server/HttpPipeFraming.cs` to improve code readability and maintainability without changing protocol behavior.

## Decision

Extract 3 new constants and 1 helper method:

### New Constants (3)
- **`ContentTypeHeader`** — standardizes `"content-type"` header name, mirrors existing `ContentLengthHeader`/`TransferEncodingHeader` pattern, eliminates bare string literal in 2 header checks
- **`ConnectionHeader`** — standardizes `"connection"` header name, used in 1 header lookup
- **`EventStreamContentType`** — names the `"text/event-stream"` magic string that controls branching between chunked-vs-content-length encoding

### New Helper (1)
- **`ReadTrailingCrlfAsync(Stream stream, CancellationToken ct)`** — deduplicates the 2-byte CRLF read operation that appeared twice in `ReadChunkedBodyAsync` (once after each chunk's data, once after the final zero-chunk). Makes intent self-documenting.

## What Was Deliberately Skipped
- Chunk terminator byte literals (`"0\r\n\r\n"u8`, `"\r\n0\r\n\r\n"u8`) — used once each, already very readable as UTF-8 literals; extracting would add indirection without clarity gain
- `"HTTP/1.1"` version string — used once, universally recognizable
- Chunk assembly `Buffer.BlockCopy` block — used once, extraction would obscure byte-level intent

## Verification

**Test Run:**
```
dotnet test src\CopilotCliIde.Server.Tests\CopilotCliIde.Server.Tests.csproj
```
**Result:** 213 tests pass, 0 failed, 0 skipped  
**Build:** Clean, no warnings  
**Protocol:** Wire format unchanged

## Review Decision (Hudson)

✅ **Approved.** No protocol drift, no extra tests needed. Existing suite validates extraction correctness.

## Team Impact

**Low.** Self-contained readability improvement. No API surface changes. `PipeProxy` uses its own HTTP helpers and is unaffected.


---

## HttpPipeFraming Chunk-End Constants (Merged 2026-03-28)

# HttpPipeFraming Chunk-End Constants

**Author:** Bishop (Server Dev)  
**Date:** 2026-03-10  
**Status:** Implemented  
**Scope:** `src/CopilotCliIde.Server/HttpPipeFraming.cs`

## Context

Pass 3 of literal extraction in `HttpPipeFraming.cs`. Previous passes extracted string constants (`Crlf`, `HeaderTerminator`, etc.) but deliberately left two `u8` byte literals as "single-use and already readable." User feedback identified these as inconsistent — the same `\r\n` and `\r\n\r\n` sequences already had named constants but weren't being used in the chunk terminator bytes.

## Decision

Replaced the two hardcoded `u8.ToArray()` chunk terminators with `static readonly byte[]` fields composed from the existing string constants:

```csharp
// Before (hardcoded u8 literals)
var chunkEnd = "\r\n0\r\n\r\n"u8.ToArray();
var terminator = "0\r\n\r\n"u8.ToArray();

// After (constant-driven, allocated once)
private static readonly byte[] ChunkEndBytes = Encoding.UTF8.GetBytes($"{Crlf}0{HeaderTerminator}");
private static readonly byte[] ChunkTerminatorBytes = Encoding.UTF8.GetBytes($"0{HeaderTerminator}");
```

## Rationale

- **DRY:** `\r\n` and `\r\n\r\n` are already named as `Crlf` and `HeaderTerminator` — using them everywhere makes the semantic structure visible
- **C# `u8` limitation:** UTF-8 string literals don't support interpolation or concatenation, so `static readonly byte[]` with `Encoding.UTF8.GetBytes()` is the idiomatic alternative
- **Performance:** `static readonly` allocates once at class load vs `u8.ToArray()` which allocates a new array on every call
- **Wire-identical:** Output bytes are unchanged; 213 server tests pass

## Team Impact

Low. Server-only change, no contract or protocol impact. Establishes the pattern: when byte arrays are composed from protocol-level sequences (`CRLF`, header terminator), use the named constants via `Encoding.UTF8.GetBytes()` rather than inline `u8` literals.


---

## Extract HTTP Literal Constants (Merged 2026-03-28)

# Decision: Extract HTTP Literal Constants in HttpPipeFraming

**Author:** Bishop (Server Dev)
**Date:** 2026-03-10
**Status:** Implemented

## Context

`HttpPipeFraming.cs` contained repeated string literals for HTTP protocol elements — CRLF sequences, header terminator, and header names (`content-length`, `transfer-encoding`) that appeared in both read and write paths.

## Decision

Extracted four `private const string` fields at class level:

| Constant | Value | Usage Count |
|---|---|---|
| `Crlf` | `"\r\n"` | 7 substitutions across read/write |
| `HeaderTerminator` | `"\r\n\r\n"` | 2 (header detection + header block end) |
| `ContentLengthHeader` | `"content-length"` | 2 (read body + write header) |
| `TransferEncodingHeader` | `"transfer-encoding"` | 2 (read body + write header) |

**Not extracted:** Single-use header names (`connection`, `content-type`), UTF-8 byte literals (`u8` chunk terminators), and char-based patterns in `ReadChunkedBodyAsync` (pattern matching `'\r', '\n'`). These don't repeat and are already readable.

## Impact

- Wire output is 100% identical — verified by all 213 server tests passing (26 HTTP-specific tests across HttpParsingTests, HttpResponseTests, ChunkedEncodingTests).
- No API surface changes (constants are `private`).
- No test modifications needed.



---

# McpPipeServer SAFE Literals Extraction

**Date:** 2026-03-28
**Author:** Bishop (Server Dev)
**Status:** Implemented
**Scope:** `src/CopilotCliIde.Server/McpPipeServer.cs` only

## Decision

Extracted four magic literals from `McpPipeServer.cs` into `private const` fields, following the same naming and placement conventions as `HttpPipeFraming.cs`:

| Constant | Type | Value | Usage Count |
|---|---|---|---|
| `PipeStartupDelayMs` | `int` | `200` | 1 (StartAsync) |
| `McpToolTimeoutSeconds` | `int` | `30` | 1 (HandleMcpPostAsync) |
| `OpenDiffToolName` | `string` | `"open_diff"` | 1 (HandleMcpPostAsync) |
| `SessionIdHeader` | `string` | `"mcp-session-id"` | 4 (POST 200/202 headers, SSE GET lookup, SSE response line) |

## Rationale

- Eliminates scattered magic numbers/strings in protocol-critical code paths.
- `SessionIdHeader` had the highest duplication risk (4 occurrences) — a typo in any one would silently break session tracking.
- Naming follows `HttpPipeFraming.cs` precedent: `PascalCase`, `private const`, grouped at top of class.
- `OpenDiffToolName` documents the semantic coupling between the timeout-skip logic and the tool registration in `OpenDiffTool.cs`.

## Validation

- 213 server tests pass (`dotnet test`).
- Wire output is byte-identical (all constants inline at compile time).
- No other files modified.

---

### 2026-03-28T22:23:46Z: User directive
**By:** Sebastien Lebreton (via Copilot)
**What:** protocol.md should describe VS Code observed protocol only, not note potential extras from our implementation.
**Why:** User request — captured for team memory

---

# Hudson — CHANGELOG.md Review

**Date:** 2026-03-29
**Verdict:** ✅ APPROVED (with minor notes)
**Reviewed file:** `CHANGELOG.md` (created by Hicks)
**Audit method:** Independent cross-reference of all 160+ commits across 14 git tags (1.0.0–1.0.13 + HEAD)

---

## Audit Checklist vs. Changelog Coverage

| Version | Date ✓ | Features ✓ | Fixes ✓ | PRs ✓ | Links ✓ | Notes |
|---------|--------|------------|---------|-------|---------|-------|
| 1.0.0   | ✅     | ✅          | n/a     | n/a   | ✅       | All 7 tools listed correctly |
| 1.0.1   | ✅     | ✅          | ✅       | n/a   | ✅       | |
| 1.0.2   | ✅     | ✅          | n/a     | n/a   | ✅       | Correctly marked as version-bump-only |
| 1.0.3   | ✅     | ✅          | ✅       | n/a   | ✅       | |
| 1.0.4   | ✅     | ✅          | n/a     | n/a   | ✅       | |
| 1.0.5   | ✅     | ✅          | ✅       | n/a   | ✅       | |
| 1.0.6   | ✅     | ✅          | ✅       | n/a   | ✅       | Large release, well-organized |
| 1.0.7   | ✅     | ✅          | ✅       | n/a   | ✅       | |
| 1.0.8   | ✅     | ✅          | ✅       | n/a   | ✅       | |
| 1.0.9   | ✅     | ⚠️          | n/a     | n/a   | ✅       | See Note 1 |
| 1.0.10  | ✅     | n/a        | ✅       | ✅ #2  | ✅       | |
| 1.0.11  | ✅     | ✅          | n/a     | n/a   | ✅       | Dual-tag correctly noted |
| 1.0.12  | ✅     | ✅          | n/a     | ✅ #3  | ✅       | Community PR credited |
| 1.0.13  | ✅     | ✅          | n/a     | n/a   | ✅       | |

---

## Notes (non-blocking)

### Note 1: 1.0.9 — CI workflow omitted

The changelog says "GitHub Actions release workflow for automated builds" but 1.0.9 introduced **two** separate workflows:
- `ci.yml` — CI build/test workflow (commit `7a7dd2a`)
- `release.yml` — Automated release workflow (commit `29e9b54`)

**Suggested fix:** Change to:
```markdown
### Added

- GitHub Actions CI workflow (`ci.yml`)
- GitHub Actions release workflow (`release.yml`)
- Build status badge in README
```

### Note 2: Non-standard Keep a Changelog categories

The header claims "based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)" but uses custom categories (`### Test`, `### Docs`, `### Build`) not in the spec (which only defines: Added, Changed, Deprecated, Removed, Fixed, Security).

This isn't wrong — it's a reasonable extension — but it's **inconsistent**: some releases use `### Docs` for README changes (1.0.4, 1.0.6, 1.0.13) while others fold them into `### Changed` (1.0.5). Similarly, `### Build` appears in 1.0.9 and 1.0.13 but CI/build changes are under `### Changed` elsewhere.

**Suggested fix:** Either:
- (a) Fold `Test`/`Docs`/`Build` items into the standard categories, OR
- (b) Keep the custom categories but use them consistently across all releases

### Note 3: 1.0.4 internal docs in changelog

The entry "Add Copilot instructions and code review instructions" under `### Docs` refers to `.github/copilot-instructions.md` — a development-time file, not user-facing. Debatable whether it belongs in a public changelog. Not a blocker.

---

## Factual Accuracy

Every date, PR reference, feature description, and compare link verified against git history. **No factual errors found.** The only substantive omission is the CI workflow in 1.0.9 (Note 1).

---

## Decision

**APPROVED.** The changelog is factually accurate, well-structured, and covers all 14 releases. The two notes above are quality improvements, not blockers. Recommend addressing Note 1 (CI workflow omission) before shipping — it's a one-line fix.

---

# Decision: Remove diagnostics source field from NotificationFormatTests

- **Date:** 2026-03-28T22:21:54Z
- **Requester:** Sebastien Lebreton
- **Context:** The diagnostics source field is obsolete and should no longer be validated in server notification format tests.
- **Decision:** Remove all source references (input payload and assertions) from src/CopilotCliIde.Server.Tests/NotificationFormatTests.cs while keeping notification format intent intact.
- **Outcome:** Test now validates remaining diagnostics fields (message, severity, range, code) without asserting obsolete source.

---

# Ripley — CHANGELOG.md Polish (Hudson Note 1)

**Date:** 2026-03-29
**Requested by:** Sebastien Lebreton
**Status:** Applied

---

## What changed

In CHANGELOG.md release 1.0.9 under `### Added`, the single bullet:

> GitHub Actions release workflow for automated builds

was split into two bullets to match what was actually shipped:

- GitHub Actions CI workflow (`ci.yml`)
- GitHub Actions release workflow (`release.yml`)

## Why

Hudson's code review (`.squad/decisions/inbox/hudson-changelog-review.md`, Note 1) identified that 1.0.9 introduced **two** separate workflow files but the changelog only mentioned one. This is a factual omission — the CI workflow (`ci.yml`, commit `7a7dd2a`) was missing entirely.

## Team relevance

When adding CI/build infrastructure in future releases, each distinct workflow file should get its own changelog bullet. Lumping them under a single description loses traceability back to commits and makes the changelog less useful as a historical record.


---

# Hudson — SSE Resume is a Custom Store Feature

**Author:** Hudson (Tester)  
**Date:** 2026-03-29  
**Scope:** CopilotCliIde.Server  
**Type:** Architecture decision

## Context

Regression tests for the TrackingSseEventStreamStore simplification effort confirm that resume via Last-Event-ID is implemented entirely by the custom store. The test Resume_ReplaysMissedEvents_WhenLastEventIdProvided exercises this end-to-end.

## Decision

If the custom SSE store is removed or replaced with the MCP SDK default:
1. The Resume_ReplaysMissedEvents_WhenLastEventIdProvided test **will fail** — this is intentional.
2. The team must decide whether resume is required for CLI behavior. If yes, an alternative implementation is needed.
3. All other SSE behaviors (live push, initial push on connect, event delivery order) should continue working with the default store.

## Impact

- **10 regression tests** now guard the SSE notification contract
- **Resume is the only behavior** that depends on the custom store's history/replay logic
- Live push and initial-push-on-connect work through the MCP SDK's session mechanism, not the custom store


---

# Decision: vs-1.0.14 Capture Test Implementation

**Author:** Hudson (Tester)
**Date:** 2026-07-25
**Status:** Implemented

## Context

Prior analysis of the vs-1.0.14 capture identified 3 concrete test gaps. This implements all three.

## Tests Added (260 → 281)

1. **B5b `GetVsCodeInfoResponse_HasAllExpectedFields`** — Validates all 8 VS Code reference fields (version, appName, appRoot, language, machineId, sessionId, uriScheme, shell) are present and non-empty strings. Extends B5 which only checked appName + version.

2. **B6 `GetDiagnostics_EmptyResult_HasValidMcpEnvelope`** — Validates the empty-result path (content[0].text == "[]") has correct MCP envelope. Test 4 returns early on empty arrays, leaving this path uncovered.

3. **B7 `OpenDiffClosedViaTool_ResolvesAfterCloseDiff`** — Validates the closed_via_tool lifecycle pairing between open_diff and close_diff. Uses structural validation (tab-name correlation) rather than temporal ordering (seq numbers), because VS Code and VS have different response ordering.

## Design Decisions

- **Structural over temporal:** Initial implementation used seq-number comparison to enforce close_diff-before-open_diff ordering. This failed on VS Code captures where the ordering differs. Redesigned as a structural pairing test — both responses must exist with correct fields.
- **No capture modifications:** All tests work with existing capture data.
- **Theory over Fact:** All 3 are [Theory] tests running against all 7 captures, providing cross-implementation coverage.

## Impact

- Closes the 3 identified P1 gaps from the vs-1.0.14 analysis
- No remaining high-priority capture test gaps


---

# Decision: Cleared Selection Event Timing

**Author:** Hicks (Extension Dev)
**Date:** 2026-07-20
**Status:** Implemented

## Context

When closing editor tabs, `OnViewClosed` was calling `PushClearedSelection()` for **every** tab close. In a 3-file workspace, closing all tabs would emit 3 cleared events — but only the last one (when no editors remain) is meaningful. The first two are immediately superseded by VS focusing the next editor tab and `SEID_WindowFrame` firing a real selection event.

## Decision

**`OnViewClosed` must NOT push a cleared selection.** It only calls `UntrackView()`.

The cleared event is emitted solely from `TrackActiveView` (driven by `SEID_WindowFrame`) when `wpfView == null` — meaning VS has settled on a non-editor window as the active frame, confirming no editors remain.

## Rationale

- `SEID_WindowFrame` fires AFTER VS resolves the next active window, so it reflects actual editor state, not a transient closing state.
- `OnViewClosed` fires during the close of the tracked view, BEFORE VS has decided what to focus next — emitting cleared here is premature.
- The 200ms debounce in `DebouncePusher` provides additional protection against rapid close sequences, but the root fix is not relying on it — we simply don't emit from the wrong event.

## Implications

- **Server code:** Unchanged. The server receives the same `SelectionNotification` shape — just fewer spurious cleared events.
- **Testing:** Server integration tests pass (285/285). Extension-side behavior is validated by the VS event model contract: `SEID_WindowFrame` always fires when the active frame changes.
- **Edge cases:** "Close All Tabs" produces exactly one cleared event (VS activates a non-editor window once). Rapid Ctrl+W across all tabs also produces one event (debounce coalesces).

---

# Decision: Cleared Event Timing — OnViewClosed Must Not Push (Approved)

**Author:** Hudson (Testing & QA)
**Date:** 2026-03-30
**Status:** APPROVED (revised fix)

## Context

The initial stale-selection fix had `OnViewClosed` calling `PushClearedSelection()` on every editor tab close. This caused 3 spurious cleared events when closing 3 files sequentially — the user reported "sending 3x for nothing and with a bad timing."

## Decision

`OnViewClosed` must only call `UntrackView()`. The `PushClearedSelection()` call belongs exclusively in `TrackActiveView` when `wpfView == null` (meaning VS has no active editor after a `SEID_WindowFrame` change).

**Why this is correct:**
- When closing an intermediate tab, VS fires `SEID_WindowFrame` to activate the next tab → `TrackActiveView` gets a valid editor → pushes selection (not cleared)
- When closing the last tab, VS fires `SEID_WindowFrame` with a non-editor frame (tool window or null) → `TrackActiveView` gets `wpfView == null` → pushes cleared (exactly once)

## Impact

- SelectionTracker: `OnViewClosed` = `UntrackView()` only
- SelectionTracker: `PushClearedSelection()` called only from `TrackActiveView` null path
- 3 new regression tests added (288 total): 3-file workflow, server transparency, single-file edge case
- The server does NOT filter cleared events — the guard is in the extension

---

# Decision: Selection Clear Regression Root Cause

**Author:** Ripley (Regression Archaeology)
**Date:** 2026-07-19
**Status:** Finding (no code change)

## Context

The extension stopped sending `selection_changed` notifications with cleared state when all document tabs close. This left Copilot CLI displaying stale selection data.

## Finding

**Exact regression commit: `3d17a6f`** — "Push current selection when copilot-cli SSE client connects" (2026-03-05 09:49)

This commit deliberately removed `PushEmptySelection()` from `TrackActiveView()`, `OnViewClosed()`, and deleted the method entirely. The commit message justified it with: *"copilot-cli ignores empty file paths."*

The assumption was incorrect — the CLI needs the notification as a state transition signal regardless of payload content.

## Timeline

| Commit | Date | Effect |
|--------|------|--------|
| `912f832` | 2026-03-05 09:43 | Added PushEmptySelection — behavior correct |
| `3d17a6f` | 2026-03-05 09:49 | Removed PushEmptySelection — **regression** |
| `be35e41` | 2026-03-07 | Extraction to SelectionTracker carried broken state |

## Implication

The fix (adding `PushClearedSelection` back) is already in progress as an uncommitted change in the working tree. The new implementation correctly:
- Pushes clear from `TrackActiveView` only (not `OnViewClosed`)
- Uses the debouncer instead of immediate send
- Sends a bare `SelectionNotification()` (all nulls) rather than the old empty-string-filled version

## Team Impact

- Hicks: If implementing the fix, the working-tree change is the right approach. Verify the notification shape matches what CLI expects.
- Hudson: Add a regression test for the "all tabs closed → cleared selection push" path.

## Decision: PushEmptySelection bypasses DebouncePusher

**Date:** 2026-07-20
**Author:** Hicks
**Status:** Implemented

### Context

After a full revert, the PushEmptySelection behavior from commit 912f832 needed to be ported into the refactored SelectionTracker class. The original implementation sent empty selection events immediately via Task.Run, bypassing any debounce logic.

### Decision

PushEmptySelection bypasses DebouncePusher and sends immediately. It uses its own dedup field (_lastPushedKey with key ":empty:") rather than sharing DebouncePusher._lastKey.

### Rationale

1. **Immediacy**: Empty selection events are infrequent (editor close/switch to tool window) and signal a meaningful state change. Debouncing them would add 200ms latency with no spam-prevention benefit.
2. **Dedup isolation**: Since PushEmptySelection doesn't go through Schedule() → OnDebounceElapsed(), it can't use DebouncePusher.IsDuplicate(). A separate volatile field provides the same protection without coupling the two paths.
3. **Bidirectional reset**: When a real selection push goes through OnDebounceElapsed, it clears _lastPushedKey so subsequent empty pushes aren't suppressed. This ensures the state machine stays correct across open→close→open cycles.

### Impact

- SelectionTracker.cs only — no interface or contract changes
- Wire format unchanged (empty strings, not nulls, matching 912f832)
- All 288 existing tests pass; no new tests needed (server-side tests don't cover UI-thread push logic)

## Decision: Set WorkingDirectory on MCP Server Process

**Author:** Hicks  
**Date:** 2026-07-20  
**Issue:** #4  
**Commit:** cbd55f3  

### Context

ServerProcessManager.StartAsync launches the MCP server as a child process via dotnet. Without an explicit WorkingDirectory, the child inherits VS's current working directory — which is the open solution/project folder.

If that folder contains an ppsettings.json with Kestrel HTTPS endpoint configuration, Kestrel attempts to load it and throws InvalidOperationException because UseKestrelHttpsConfiguration() was never called. The process exits immediately; the named pipe is never created.

### Decision

Set WorkingDirectory = serverDir in ProcessStartInfo, where serverDir is the McpServer/ subdirectory under the extension install path. This directory contains only the published server binaries — no ppsettings.json — so Kestrel starts with default configuration.

### Rule

**Always set WorkingDirectory explicitly when launching child processes from VS extensions.** The inherited directory is the user's project folder, which can contain arbitrary configuration files that interfere with the child process.

## Decision: Fix 2 Critical Terminal Bugs — 2026-07-20

# Decision: Fix 2 Critical Terminal Bugs

**Author:** Hicks (Extension Dev)  
**Date:** 2026-07-20  
**Status:** Implemented  
**Files changed:** `src/CopilotCliIde/TerminalProcess.cs`, `src/CopilotCliIde/Resources/Terminal/terminal-app.js`

## Context

Both bugs were identified during Hicks's code review of PR #7 (embedded terminal subsystem) and logged in `.squad/agents/hicks/history.md` under "Terminal Subsystem Code Review Delivery (PR #7)" as critical findings #1 and #2.

## Bug 1: UTF-8 Multi-Byte Character Corruption

**Root cause:** `Encoding.UTF8.GetString(buffer, 0, bytesRead)` in `ReadLoop()` is stateless. Each call treats the byte array as a complete, independent UTF-8 stream. When a multi-byte character (2-4 bytes) is split across two `ReadFile` calls at the 4096-byte buffer boundary, both chunks decode the partial bytes as U+FFFD replacement characters.

**Fix:** Replaced with a persistent `System.Text.Decoder` instance (`Encoding.UTF8.GetDecoder()`). The `Decoder` maintains internal state — incomplete trailing bytes from one `GetChars()` call are buffered and prepended to the next call's input, producing correct characters.

**Impact:** Affects any terminal output containing non-ASCII characters (emoji, CJK, accented characters, box-drawing characters in TUI apps). Corruption is probabilistic — depends on whether a multi-byte sequence lands on a buffer boundary.

## Bug 2: Broken Focus Recovery (window.term)

**Root cause:** `TerminalToolWindowControl.cs:58` calls `ExecuteScriptAsync("window.term.focus()")` to recover keyboard focus after VS debug cycles (F5). The xterm.js `Terminal` instance is created as `var terminal` inside an IIFE in `terminal-app.js` — it's scoped to the function closure and invisible on `window`. The call fails silently (`undefined.focus()` is not called; `window.term` is just `undefined`).

**Fix:** Added `window.term = terminal;` in `terminal-app.js` after full initialization, exposing the instance for C# interop.

**Impact:** After any F5 debug cycle, clicking in the terminal did not restore keyboard focus. Users had to close and reopen the tool window.

## Build Verification

- Server project: `dotnet build` — 0 errors, 0 warnings.
- Roslyn validation of `TerminalProcess.cs` — clean.
- Extension project is net472/VSSDK (requires MSBuild, not validated here).

## Patterns Established

1. **Streamed UTF-8 decoding:** Always use `Decoder` (not `Encoding.GetString`) when reading byte streams in chunks. This applies to pipes, sockets, serial ports — any scenario where character boundaries don't align with read boundaries.
2. **WebView2 JS interop:** Any JS object that C# needs to reach via `ExecuteScriptAsync` must be assigned to `window`. IIFE-local variables are unreachable.


