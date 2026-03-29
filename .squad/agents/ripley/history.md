# Project Context

- **Owner:** Sebastien
- **Project:** CopilotCliIde — A Visual Studio extension (VSIX) bridging GitHub Copilot CLI's /ide command with Visual Studio via MCP over named pipes. Three C# projects: CopilotCliIde (VS extension, net472), CopilotCliIde.Server (MCP server, net10.0), CopilotCliIde.Shared (contracts, netstandard2.0).
- **Stack:** C#, .NET, MSBuild, VSSDK, StreamJsonRpc, MCP, Windows named pipes
- **Created:** 2026-03-05

## Core Context

Ripley leads protocol analysis, reverse-engineering, and spec validation. Key decisions:
- **Capture source truth (2026-03-08):** vs-1.0.7.ndjson is our code; vscode captures are ground truth
- **Multi-session protocol analysis:** All contract changes verified correct; 4 test failures were infrastructure issues
- **Deep capture analysis:** Confirmed 3 NDJSON files structurally identical across VS/VS Code, minor cosmetic differences only
- **Test coverage gaps:** open_diff/close_diff/get_vscode_info response tests proposed but deferred pending stabilization
- **Documentation:** 7 edits to protocol.md based on capture analysis; all P0/P1/P2 gaps addressed
- **Protocol rules:** Always use PathUtils for file URIs; never raw Uri.ToString()

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->

### 2026-03-06 — VS Code Lock File Reverse Engineering

Reverse-engineered the Copilot Chat extension (`github.copilot-chat-0.38.2026022303/dist/extension.js`) to compare lock file formats.

**Key findings:**
- Our lock file schema is **identical** to VS Code's — same 8 fields, same names, same types. No alignment needed.
- Both use `~/.copilot/ide/{uuid}.lock` path, `\\.\pipe\mcp-{uuid}.sock` pipe names, `Nonce {uuid}` auth.
- Both do PID-based stale lock cleanup on startup.
- **One difference:** VS Code updates its lock file when workspace folders change or trust is granted. We write once. Low priority for VS since it's single-solution.
- The "Watching IDE lock file" log from Copilot CLI is on the **CLI side**, not the extension. The extension only writes/updates/deletes.
- VS Code's MCP server is an Express HTTP server listening on the named pipe, not StreamJsonRpc. The `/mcp` fragment in the URI maps to the HTTP route.
- Decompiled key functions: `vSn` (create lock), `_Sn` (stale cleanup), `B1i` (pipe name), `iCt` class (lock file model with update/remove).

**Decision merged:** `.squad/decisions.md` — "Lock File Format Compatibility" section. Verdict: no action required on schema; low-priority enhancement for workspace folder tracking.

### 2026-03-06 — README Audit Against Codebase

Audited README.md against the actual source after VS Code schema alignment work. Fixed 6 categories of inaccuracies:

**Key wire format facts confirmed from source:**
- MCP SDK (ModelContextProtocol 0.8.0-preview.1) serializes DTO properties as camelCase. Tools returning raw DTOs (`get_vscode_info`, `get_selection`) get camelCase automatically.
- Tools returning anonymous objects (`open_diff`, `close_diff`, `get_diagnostics`) explicitly set snake_case/lowercase property names in the tool layer (`OpenDiffTool.cs`, `CloseDiffTool.cs`).
- `get_diagnostics` returns `result.Files ?? []` — a raw array at root, not wrapped in an object. This matches VS Code.
- `DiffResult` has `Result` ("SAVED"/"REJECTED") and `Trigger` fields. `UserAction` is kept for backward compat, mapped from Result.
- Both `selection_changed` and `diagnostics_changed` use `DebouncePusher` with 200ms timer. Selection data is captured eagerly on UI thread, pushed after quiet period.
- `diagnostics_changed` is triggered by `BuildEvents.OnBuildDone` + `DocumentEvents.DocumentSaved`, not real-time Roslyn analyzers.
- `read_file` is our 7th tool, not in VS Code's 6-tool protocol. Harmless extra capability.

**Files examined:** `Contracts.cs`, `VsServiceRpc.cs`, `CopilotCliIdePackage.cs`, `SelectionTracker.cs`, `DebouncePusher.cs`, `Program.cs`, all 7 tool files in `Tools/`.

### 2026-03-07 — Documentation Session Completion

Led documentation audit session. Verified README.md against codebase, fixed 6 categories of wire format inaccuracies. Bishop verified protocol.md simultaneously and found one discrepancy (get_diagnostics missing filePath field — fixed). All documentation now aligned with implementation. Both agents committed changes.

### 2026-03-07 — PathUtils Review & URI Consistency Fix

Reviewed `PathUtils.cs` after Sebastien questioned whether BCL could replace it.

**Verdict: PathUtils is necessary.** Both methods exist because BCL doesn't match the VS Code protocol:
- `System.Uri("C:\\Dev\\file.cs").AbsoluteUri` → `file:///C:/Dev/file.cs` (uppercase `C`, literal `:`)
- VS Code protocol requires → `file:///c%3A/Dev/file.cs` (lowercase `c`, encoded `%3A`)
- `ToLowerDriveLetter` has no BCL equivalent — no method lowercases just the drive letter.

**Bug found:** Three call sites bypassed PathUtils and used raw `new Uri(path).ToString()`:
1. `VsServiceRpc.GetSelectionAsync()` — `FileUrl` and `FilePath` produced wrong format
2. `VsServiceRpc.GetDiagnosticsAsync()` — `Uri` and `FilePath` wrong
3. `CopilotCliIdePackage.CollectDiagnosticsGrouped()` — diagnostics push `Uri` wrong

This meant the initial selection (via `get_selection` tool, backed by `VsServiceRpc`) produced different URIs than ongoing pushes (via `SelectionTracker`, which used PathUtils correctly). Same for diagnostics: tool response vs push notification had different URI formats.

**Fix:** All 3 sites now use `PathUtils.ToVsCodeFileUrl` and `PathUtils.ToLowerDriveLetter`. Server builds clean, 109 tests pass.

**Team rule:** Any code producing file URIs for MCP protocol MUST use PathUtils, never raw Uri.ToString(). See `.squad/decisions.md` — "PathUtils is Protocol-Required, Not a Hack" section.

### 2026-03-07T10:44:04Z — PathUtils XML Documentation

Requested inline XML documentation to make the protocol requirement discoverable in source code. Hicks added class-level docs, method remarks explaining why BCL alternatives are insufficient. Documentation appears in IDE tooltips and generated docs.

**Build:** 109 tests pass.

### 2026-03-07T105114Z — Severity Mapping Centralization (Implemented & Verified)

Refactored duplicated `vsBuildErrorLevel` → severity string mapping that existed in two places:
- `VsServiceRpc.MapSeverity` (private static, explicit 4-arm switch)
- `CopilotCliIdePackage.CollectDiagnosticsGrouped` (inline 3-arm switch, missing explicit `Low` case)

**Decision:** Promoted `VsServiceRpc.MapSeverity` from `private static` to `internal static`. `CopilotCliIdePackage` now calls `VsServiceRpc.MapSeverity(item.ErrorLevel)` instead of duplicating the switch.

**Why not Shared project?** `vsBuildErrorLevel` is `EnvDTE80` (VS SDK). Shared is netstandard2.0 with no VS SDK dependency — moving it there would violate the architecture boundary.

**Why not a new utility class?** Both callers are in the same extension project. The method is 5 lines, pure, and stateless. A new file would be over-engineering.

**Verification:** Hudson ran full test suite. All 109 tests pass. No dedicated net472 unit test added (disproportionate infrastructure cost; existing indirect coverage sufficient). See `.squad/decisions.md` for merged decisions and rationale.

**Build:** Server + extension compile clean, 109 tests pass.

### 2026-03-07T11:41:21Z — Team Notification: Husky Pre-Commit Hook Installed

Hicks implemented whitespace enforcement via husky pre-commit hook (Sebastien's directive). **All team members should adopt the following practices immediately:**

- Before committing: Run `npm run format` to auto-fix any whitespace violations
- In CI pipelines: Use `npm run format:check` to verify without modifying
- Git commit now automatically triggers the pre-commit hook running `dotnet format --verify-no-changes`

The hook applies to all .NET code across the solution. See `.squad/decisions.md` — "Whitespace Enforcement via Husky Pre-Commit Hook" for full details.

### 2026-03-07 — Protocol Compatibility Test Architecture

Designed architecture for automated protocol compatibility regression tests. Key decisions:

**Approach:** Golden snapshot tests + MCP handshake integration tests, in the existing `CopilotCliIde.Server.Tests` project. Rejected live proxy tests (CI nightmare), extension.js extraction (fragile), and separate test project (unnecessary).

**Two layers:**
1. **Golden Schema Tests** — compare our MCP tool outputs against JSON snapshots captured from real VS Code ↔ Copilot CLI traffic. Structural superset check (we can have more fields, never fewer).
2. **MCP Handshake Integration Tests** — spin up real `McpPipeServer` on test pipe with mocked `IVsServiceRpc`, perform full `initialize` → `tools/list` → tool calls → SSE notifications over real HTTP-on-pipe.

**Test seam needed:** `RpcClient` needs an internal constructor accepting `IVsServiceRpc` so tests can inject mocks without connecting a real RPC pipe. Minimal change, already `[InternalsVisibleTo]`.

**Reference data:** Golden JSON snapshots in `Snapshots/` folder, committed to repo. Refreshed manually (monthly or on major VS Code update) via proxy capture script. Not automated in CI — VS Code extension updates aren't predictable enough.

**Phased rollout:** Phase 1 (golden infra + tools/list), Phase 2 (handshake integration), Phase 3 (per-tool golden tests), Phase 4 (refresh script).

**Files:** `ProtocolCompatibilityTests.cs`, `McpHandshakeTests.cs`, `Snapshots/*.json`, `scripts/refresh-snapshots.ps1`

**Key existing test coverage:** 109 tests already cover tool discovery, output schemas, notification formats, DTO serialization, and HTTP parsing individually. The new tests add integration-level and golden-reference validation.

Full proposal: `.squad/decisions/inbox/ripley-protocol-compat-test-architecture.md`

### 2026-03-07T17:02:27Z — Protocol Compatibility Phase 1 Orchestration Complete

Orchestration log written to `.squad/orchestration-log/2026-03-07T17-02-27Z-ripley.md`. This spawn delivered the full protocol compatibility test automation architecture design.

**Outcome:** ✅ SUCCESS. Complete two-layer architecture designed and documented. Phase 1 (golden snapshot infrastructure) completed by Hudson (3 tests, 8 golden JSON files). Phase 2 (handshake integration test) scoped at ~4 hours, Phase 3-4 deferred.

**Key artifacts:**
- `.squad/decisions.md` — "Protocol Compatibility Test Architecture" section merged from inbox with full specification covering test structure, reference data management, phased rollout, and assignments
- `RpcClient` test seam specification (Option 1 — internal constructor) → implemented by Bishop ✅
- Phase 1 infrastructure specification → completed by Hudson ✅
- Phase 2 handshake test architecture → ready for Bishop to implement

**Next phases:**
- **Phase 2** (Bishop lead): MCP handshake integration test (~4h) using golden framework + test seam
- **Phase 3** (Hudson): Per-tool golden response tests (~2h)
- **Phase 4** (Hicks or Bishop): Refresh script (~3h)

**Team impact:** Automated protocol compatibility checks now enabled. Golden snapshots committed to repo; manual refresh monthly or on major VS Code update. All production code changes complete (RpcClient seam is only change needed). Existing 109 tests remain at 100% pass rate.

### 2026-03-09T20:44:31Z — P1 Protocol Documentation Updates

Orchestration log written to `.squad/orchestration-log/2026-03-09T20-44-31Z-ripley.md`. Updated protocol.md with 7 edits based on capture deep-inspection findings. Documented multi-session lifecycle, execution.taskSupport field, LLM retry behavior, DELETE framing, virtual URI normalization, tool annotations, and error code availability.

**Outcome:** ✅ SUCCESS. All 7 edits applied to `doc/protocol.md`:
1. Multi-session lifecycle — sequence numbering across session boundaries documented
2. execution.taskSupport — clarified VS forbids task execution
3. LLM retry behavior — retries are Copilot CLI logic, not explicit tool calls
4. DELETE framing — Content-Length: 0 for payloads and responses
5. Virtual URI normalization — file:///c%3A/... format rules clarified
6. Tool annotations — discovery requirements and custom read_file handling
7. Error code availability — only via IErrorList table control API, DTE fallback

**Cross-references:** Each topic linked to corresponding decision entry or implementation code.

**Impact:** Protocol.md now fully reflects capture behavior and implementation. Serves as ground truth for future protocol questions.

### 2026-03-07 — Protocol Compat Testing Redesign: Proxy-Based Approach

Sebastien rejected the golden-snapshot-from-source-code approach as circular ("I want tests really using vscode-insiders"). Redesigned the entire protocol compatibility testing architecture around a named pipe proxy.

**New approach:**
- C# console app (`tools/PipeProxy/`, net10.0) that sits between Copilot CLI and VS Code Insiders on the named pipe
- Reuses `ReadHttpRequestAsync`, `WriteHttpResponseAsync`, `ReadChunkedBodyAsync` from `McpPipeServer.cs` (already internal static, fully tested)
- Reads VS Code's lock file, writes its own lock file so CLI connects to proxy
- Logs all traffic as NDJSON (structured, machine-parseable)
- Captured traffic committed to repo as test data — real wire captures, not derived from source code

**Testing flow:**
1. Developer runs proxy while using `copilot /ide` with VS Code Insiders → captures traffic to NDJSON
2. Replay tests (`TrafficReplayTests.cs`) read captured traffic, send same requests to our server, structurally compare responses
3. Future optional: live dual-target comparison mode (proxy sends to both VS Code and our server simultaneously)

**Key decisions:**
- C# over Node.js/PowerShell — reuses existing production HTTP parsing code
- NDJSON over raw bytes/SQLite — grep-able, diffable, streamable
- Structural comparison over value comparison — same fields/types/nesting, not same values
- Standalone tool over test fixture — capture is manual (requires human driving CLI), replay is automated CI

**Supersedes:** Previous golden snapshot architecture. Existing `Snapshots/*.json` files kept temporarily until replay tests replace them.

**Proposal:** `.squad/decisions/inbox/ripley-proxy-based-compat-testing.md`

### 2026-03-08 — Protocol Documentation vs Capture Analysis

Deep analysis of all 3 NDJSON capture files against `doc/protocol.md`. Key findings:

**Server info version mismatch:** Doc says `"version": "1.0.0"` but both VS Code captures (0.38, 0.39) show `"0.0.1"`. Only our server sends `"1.0.0"`. Doc should note the actual VS Code value or state it's implementation-defined.

**Undocumented `title` field in serverInfo:** All captures include `"title": "VS Code Copilot CLI"` in the initialize response serverInfo, but the doc only documents `name` and `version`.

**`logging` capability not in VS Code:** Our server advertises `"logging": {}` capability. VS Code doesn't. Doc correctly shows only `{"tools": {"listChanged": true}}` — our server has the extra capability (from MCP SDK defaults).

**VS Code tool schemas include `additionalProperties: false` and `$schema`:** VS Code 0.38/0.39 tool input schemas include `"additionalProperties": false` and `"$schema": "http://json-schema.org/draft-07/schema#"` on tools with parameters. Our schemas and the doc omit these. Functionally irrelevant but a structural difference.

**`get_diagnostics` uri type difference:** VS Code declares `uri` as `{"type": "string"}` (no default). Our server declares `{"type": ["string", "null"], "default": null}`. Doc describes it as optional string without specifying JSON Schema type.

**`get_selection` can return `null`:** vscode-0.38 capture shows the tool returning `null` (no active/cached editor). Doc only describes the object response with `current: true/false`, never mentions the `null` case.

**`source` field never observed in captures:** Doc lists `source: string?` for diagnostic items in `get_diagnostics`. No capture file includes this field. The field may be VS Code language-dependent.

**HTTP transport: chunked encoding, not Content-Length:** Doc describes `Content-Length` as standard POST header. All captures show CLI using `Transfer-Encoding: chunked` instead.

**VS Code session ID behavior:** VS Code (Express-based) appears to echo the CLI's `X-Copilot-Session-Id` as the `mcp-session-id`. Our server generates its own. Doc says "server generates" — VS Code's behavior is technically different.

**202 Accepted for notifications:** VS Code returns `202 Accepted` with `text/plain` for `notifications/initialized`. Our server returns `200 OK` with `text/event-stream`. Doc mentions 202 for notifications but doesn't detail the VS Code behavior for the initialized notification specifically.

**Old Snapshots/ directory removed:** Commit `251d28d` removed the golden snapshot tests. The proxy-capture approach fully superseded them. No Snapshots/README.md to check.

**Report written to:** `.squad/decisions/inbox/ripley-protocol-doc-analysis.md`

### 2026-03-08 — Protocol.md Updated from Capture Analysis

Applied 7 surgical edits to `doc/protocol.md` based on capture analysis findings:

1. **P0 — `get_selection` null response:** Documented that the tool can return `null` content when no editor is active and no cached selection exists. Added blockquote after the response table.
2. **P1 — 200 vs 202 response codes:** Expanded the notification response line to explain `202 Accepted` with `text/plain` for fire-and-forget messages, noting that some servers return `200 OK` and the CLI accepts both.
3. **P1 — Server info version and title:** Changed example version from `"1.0.0"` to `"0.0.1"` (actual VS Code value), added `"title"` field, noted version is implementation-defined.
4. **P1 — Content-Length vs chunked encoding:** Updated HTTP headers to show both `Content-Length` and `Transfer-Encoding: chunked` as valid, noting CLI typically uses chunked. Updated the POST example to show chunked.
5. **P2 — Diagnostics `source` field:** Added note that the field may be absent depending on the language service.
6. **P2 — `additionalProperties` and `$schema`:** Added blockquote noting VS Code includes these in tool schemas but they're optional.
7. **P2 — Session ID format:** Already documented as implementation-defined — no change needed.

No code changes, documentation only. All edits preserve existing correct content.

### 2026-03-08 — Protocol Documentation Round 2 Re-Analysis

Deep re-analysis of all 3 capture files (vscode-0.38, vscode-insiders-0.39, vs-1.0.7) against the UPDATED `doc/protocol.md`. The vs-1.0.7 capture is new — first capture from our own Visual Studio extension.

**Result: No protocol doc changes needed.** All 7 Round 1 fixes are accurate against the new capture data:

1. `get_selection` null response — VS Code 0.38 confirms `"null"` text. Our server returns `{"text":"","current":false}` instead (server compliance issue, not doc issue).
2. 200 vs 202 for notifications — our server returns `202 Accepted` with `content-type: text/plain; content-length: 0`. VS Code uses `text/plain; charset=UTF-8` with chunked body. Both valid.
3. Server info — our server sends `version: "1.0.0"`, VS Code sends `"0.0.1"`. Doc correctly notes version is implementation-defined.
4. Chunked encoding — all captures confirm CLI uses `Transfer-Encoding: chunked`.
5. Source field — present in our diagnostics push (`"source": "AzdTool.csproj"`), absent from VS Code pushes. Doc correctly says "may be absent."
6. additionalProperties/$schema — VS Code includes on parameterized tools only. Our server omits both. Doc correctly notes these are optional.
7. Session ID — VS Code maintains one ID per session. Our server generates a new ID per POST response (7 unique IDs in one session). Doc correctly says "generates on first POST" — our server has a compliance issue.

**Server implementation issues discovered (not doc issues):**
- Our `get_selection` returns a partial object instead of `"null"` when no editor is active
- Our server rotates `mcp-session-id` per request instead of maintaining one per session
- Our server advertises `"logging": {}` capability (MCP SDK default) that VS Code doesn't have

**Captures README minor items:** Says "Copilot Chat extension version" in naming convention, but `vs-1.0.7` uses our VSIX version. Says "VS Code" but now includes our VS captures.

**Coverage gap:** No capture invokes `get_vscode_info` — response format is documented but unverified against wire data.

### 2026-03-08 — Deep Protocol Analysis of Updated Captures (open_diff/close_diff scenarios)

All 3 captures updated by Sebastien to include open_diff accept, reject, and close_diff cancellation scenarios. Deep analysis confirms:

**Sebastien's code changes are correct and complete:**
- `DiffOutcome` constants ("SAVED"/"REJECTED") match all captures exactly
- `DiffTrigger` constants ("accepted_via_button"/"rejected_via_button"/"closed_via_tool") match all captures exactly
- Slimmed `DiffResult` (removed `DiffId`, `OriginalFilePath`, `ProposedFilePath`, `UserAction`) — confirmed these fields never appear in any capture
- Slimmed `CloseDiffResult` (removed `OriginalFilePath`) — confirmed absent from captures
- Updated tests use new constants correctly — 139 of 143 tests pass

**New finding — close_diff-cancels-open_diff produces two responses:** When `close_diff` is called while `open_diff` blocks, both VS and VS Code emit: (1) the open_diff response with `result:"REJECTED"`, `trigger:"closed_via_tool"`, then (2) the close_diff response with `already_closed:false`. Our implementation handles this correctly.

### 2026-03-08 — Deep Protocol Analysis (Multi-Session Captures)

Comprehensive protocol compatibility analysis of all 3 NDJSON capture files with multi-session scenarios. Sebastien updated captures to include multiple tool invocations across sequential MCP sessions.

**Key Findings:**
- **Multi-session corruption detected:** Captures now contain 2–4 MCP sessions per file. JSON-RPC IDs reset between sessions. TrafficParser's `GetToolCallResponse` crosses session boundaries when matching request→response pairs, causing 4 test failures.
- **Session ID isolation required:** Need sequence-scoped response matching. `GetToolCallRequest` → return both request ID and sequence number. `GetToolCallResponse` → only match responses with `Seq > requestSeq` within the same session.
- **New tool invocations captured:** `open_diff` (5–6 instances per capture with 3 response patterns: SAVED/accepted_via_button, REJECTED/rejected_via_button, REJECTED/closed_via_tool), `close_diff` (1–4 instances), `get_vscode_info` (1–4 instances).
- **`read_file` never invoked** in any capture.

**Deliverables:**
- Identified 4 failing tests tied to multi-session ID collision
- Identified root cause: `TrafficParser` lacks session awareness
- Scoped TrafficParser fixes needed for Bishop (described in full detail)
- Proposal: `.squad/decisions/inbox/ripley-capture-analysis-2026-03-08.md`

**4 test failures caused by multi-session captures:** Updated captures contain multiple sessions per file (capture script runs accept, reject, and close_diff scenarios in separate sessions). `TrafficParser.GetToolCallResponse()` correlates by JSON-RPC `id`, but IDs are reused across sessions. `AllCaptures_RequestResponseIds_AreCorrelated` expects unique IDs. Fix requires session-aware parsing.

**Minor message text difference:** VS `close_diff` returns "closed and changes rejected"; VS Code says "closed successfully" (VsServiceRpc.cs line 153). Cosmetic only.

**Coverage gap closed:** `get_vscode_info` is now exercised in all 3 captures. VS response schema (`{ideName, appName, version, solutionPath, solutionName, solutionDirectory, projects, processId}`) is intentionally different from VS Code (`{version, appName, appRoot, language, machineId, sessionId, uriScheme, shell}`) — by design.

**Report:** `.squad/decisions/inbox/ripley-capture-analysis-2026-03-08.md`



### 2025-07-19 — Design-Time Build Diagnostic API Research

Researched proper VS API for real-time diagnostic change notifications (equivalent of VS Code's `vscode.languages.onDidChangeDiagnostics`).

**APIs evaluated (7 total):**
1. **IVsSolutionBuildManager / IVsUpdateSolutionEvents** — explicit builds only, not design-time. ❌
2. **Roslyn IDiagnosticService.DiagnosticsUpdated** — internal API, MEF-exported but not public. Roslyn is actively deprecating it for pull-based diagnostics. ⚠️ Too fragile.
3. **Workspace.WorkspaceChanged** — `WorkspaceChangeKind.DiagnosticsChanged` does NOT exist. Tracks structure, not analysis results. ❌
4. **ITableManagerProvider + ITableDataSink** — ✅ **Recommended.** Public, documented Table Manager API. Subscribe to `ITableDataSource` instances on `StandardTables.ErrorsTable` as a consumer `ITableDataSink`. Gets called by Roslyn when diagnostic data changes at the data layer, below WPF.
5. **IErrorList / IErrorListService** — no change notification events. ❌
6. **IVsDiagnosticsProvider** — does not exist. ❌
7. **VS Code comparison** — `onDidChangeDiagnostics` wraps LSP `publishDiagnostics`. Our Option 4 is the closest public equivalent.

**Key insight:** Our previous Error List WPF approach failed because we subscribed to the **UI layer** (WPF DataGrid events). The Table Manager API has a separate **data layer** (`ITableDataSource` → `ITableDataSink`) that is headless, thread-safe, and doesn't trigger WPF event storms. No new NuGet packages needed — everything is in `Microsoft.VisualStudio.SDK`.

**Implementation plan:** Use `ITableDataSink` as a change notification trigger only; keep existing `ErrorListReader.CollectGrouped()` (DTE) for reading. Keep existing `BuildEvents`/`DocumentEvents` triggers as fallbacks. 200ms debounce + content dedup unchanged.

**Report:** `.squad/decisions/inbox/ripley-design-build-api.md`

### 2026-03-08 — VS API for Design-Time Diagnostic Change Notifications (Research Complete)

Completed comprehensive research evaluating 7 VS API options for real-time diagnostic change notifications from Roslyn's data layer.

**Evaluated options:**
1. IVsSolutionBuildManager / IVsUpdateSolutionEvents — fires for explicit builds only, no improvement
2. IDiagnosticService (internal) — technically ideal but too fragile, actively being sunset by Roslyn team
3. Workspace.WorkspaceChanged — no DiagnosticsChanged kind; tracks structure, not analysis results
4. **ITableManagerProvider + ITableDataSink** ✅ — only public API providing real-time change notifications
5. IErrorList — no change notification events
6. IVsDiagnosticsProvider — doesn't exist
7. VS Code comparison — confirms Table Manager is closest public equivalent to LSP onDidChangeDiagnostics

**Recommendation:** Option 4. Rationale: public stable API, no new dependencies, headless (no WPF storms), thread-safe, catches all diagnostic changes, integrates with existing 200ms debounce + dedup architecture.

**Research artifact:** ripley-design-build-api.md with full evaluation, implementation patterns, pros/cons, risks, and design decisions. Ready for implementation handoff to Hicks.

**Team impact:** Unblocks real-time diagnostic push notifications (P2 feature) and establishes pattern for future VS API research.

### 2026-03-09 — Deep Protocol Inspection: 4 Capture Files vs protocol.md

Performed systematic analysis of all 4 capture files (vscode-0.38, vscode-0.39, vscode-insiders-0.39, vs-1.0.8) against doc/protocol.md. Used Python to parse raw HTTP captures (two entry types: `event` for raw HTTP, `body` for pre-parsed SSE JSON).

**Key findings (15 total, detailed in `.squad/decisions/inbox/ripley-protocol-findings.md`):**

1. **No protocol changes between 0.38→0.39** — tool schemas, server info, and protocol version are identical across all VS Code captures.
2. **`execution.taskSupport` not `annotations`** — protocol.md references annotations but actual schema uses `"execution": {"taskSupport": "forbidden"}` wrapper. No `annotations` field observed in any capture.
3. **DELETE /mcp is a 0.39+ feature** — not present in 0.38 capture. Targets the SSE stream session ID.
4. **LLM retry pattern documented** — CLI sometimes forges requests without Mcp-Session-Id (12 errors in 0.38, 6 in 0.39, 0 in insiders/VS). Improving across versions.
5. **Multi-session lifecycle** — CLI reuses SSE from Session 1, creates new sessions per conversation turn, sends DELETE on exit. Not documented in protocol.md.
6. **VS session ID rotation (known bug)** — Our extension generates new session ID per response (29 unique IDs vs VS Code's 4). CLI adapts but this is non-compliant.
7. **Diagnostic schema differences** — VS Code returns `code` (no `source`); our extension returns `source` (no `code`). VS extension also zeros out `range.end`.
8. **Virtual URI schemes** — `copilot-cli-readonly:/` scheme not documented in protocol.md.
9. **Duplicate `tools/list`** — CLI always sends 2 tools/list calls in initial session.

**Protocol.md updates needed:** 8 items (1 high: multi-session lifecycle, 2 medium: execution schema fix + LLM retries, 5 low).
**Code fixes needed:** 4 items (1 high: session ID rotation, 2 medium: diagnostic range.end + code field, 1 low: schema extras).

### 2026-03-09 — Deep Protocol Inspection & Findings Consolidation

Completed final systematic analysis of all 4 capture files (vscode-0.38, vscode-0.39, vscode-insiders-0.39, vs-1.0.8) in parallel with Bishop and Hudson. Identified 15 protocol observations across 4 sources with detailed classification.

**Final Protocol Assessment:**
- Protocol is **stable 0.38→0.39** — zero structural changes
- Multi-session lifecycle is the core protocol pattern (SSE + re-initialize + DELETE)
- Our extension implements the protocol correctly with 1 critical bug (session ID rotation)

**Findings Summary (15 total):**
- 8 protocol.md updates needed (1 High, 2 Medium, 5 Low severity)
- 2 critical code bugs (Session ID rotation P1, diagnostics bugs P2)
- 5 cosmetic divergences (VS-specific, acceptable)

**Deliverable:** Orchestration log written to `.squad/orchestration-log/2026-03-09T20-31-14Z-ripley.md` with full actionable summary per finding.

### 2026-03-09 — Protocol.md Updated: Multi-Session, Execution Schema, LLM Retries

Applied 7 surgical edits to `doc/protocol.md` based on capture analysis findings from the 4-file deep inspection:

1. **P0 — Multi-session lifecycle (§2):** Added full subsection documenting Session 1 (initialize → initialized → GET SSE → tools/list ×2 → tool calls), Session 2+ (initialize → initialized → tool calls), and teardown (DELETE targeting Session 1's SSE session ID). Includes key behaviors: SSE reuse, tools/list caching, DELETE targeting.
2. **P1 — `execution.taskSupport` schema (§3):** Replaced flat `taskSupport: "forbidden"` with correct `execution: { taskSupport: "forbidden" }` wrapper. Added JSON example showing per-tool `execution` property. Added note that no `annotations` property was ever observed in captures.
3. **P1 — LLM header retry pattern (§7 new):** New section documenting CLI LLM sending requests without `Mcp-Session-Id` (400 errors) and without `Accept` header (406 errors). Includes retry frequency data: 12 retries in 0.38, 6 in 0.39, improving trend.
4. **P2 — DELETE version note (§2):** Added `Mcp-Session-Id` header to DELETE example, documented empty response body, noted introduction around CLI 0.39, cross-referenced Multi-Session Lifecycle section.
5. **P2 — `copilot-cli-readonly:/` virtual URI (§4):** Expanded virtual URI section from inline text to a table listing both `git://` and `copilot-cli-readonly:/` schemes with usage descriptions.
6. **P2 — Duplicate `tools/list` note:** Documented in Multi-Session Lifecycle section and updated Initial Connection sequence diagram to show both calls.
7. **P3 — `annotations` clarification (§3):** Added blockquote in Tool Execution Mode noting no `annotations` property observed in wire captures; `execution` is the actual mechanism.

Also updated: Initial Connection sequence diagram (§6) to show `notifications/initialized`, duplicate `tools/list`, and the SSE stream setup. Protocol Compatibility Checklist gained 4 new items. Section numbering updated (new §7 CLI Error Recovery; Implementation Guide bumped to §8).

### 2026-03-10T20:52:45Z — Full Architecture & Code Quality Review

Performed comprehensive review of all 3 projects (extension, server, shared) + test suite. 195 tests pass. Findings by impact level documented and shared via decisions inbox.

**Key areas identified:**
- Threading hazards in DebouncePusher (unsynchronized timer/key access), ServerProcessManager (Task.Delay race), VsServiceRpc diff tracking (concurrent close paths)
- 28+ silent catch blocks across extension + server — major debuggability gap
- VsServiceRpc (398 lines) and McpPipeServer (573 lines) both mix too many concerns
- IdeDiscovery.WriteLockFileAsync is sync-over-async
- No tests for PathUtils, DebouncePusher, or any extension-project logic
- DiagnosticTracker.ComputeDiagnosticsKey omits end position — potential false dedup
- MCP compatibility: missing `source` field in DiagnosticItem, `logging` capability not in VS Code
- Build: no Clean target for published server artifacts

### 2026-03-10 — Comprehensive Architecture & Code Quality Review

Executed a full codebase review across all three projects (extension, server, shared). Produced formal review report with 4 HIGH, 5 MEDIUM, 6 LOW findings. Report merged to `.squad/decisions.md` "Review Findings — 2026-03-10" section.

**Key findings:**
- **HIGH priority:** DebouncePusher threading hazard (race between UI thread and timer), VsServiceRpc diff cleanup race (concurrent paths), ServerProcessManager readiness fragility (Task.Delay 200ms guess)
- **MEDIUM priority:** IdeDiscovery async/sync naming, DiagnosticTracker incomplete hash, unnecessary UI-thread round-trip, missing `source` field in diagnostics DTO, missing MSBuild clean target
- **LOW priority:** God class refactoring (DiffManager, SSE broadcaster), untested PathUtils/DebouncePusher, header byte-by-byte reading inefficiency

**Cross-references:** Hicks' HIGH-2 (DebouncePusher race) aligns exactly with H1. Silent catch block pattern (H4) spans extension and server. Bishop's H1 (cache-control header) is simple fix. Hudson's test coverage gaps (especially L4, L5) are direct consequences.

**Decision:** Report filed to `.squad/decisions.md`. Ready for sprint planning and cross-team coordination.
### 2026-03-29 — CHANGELOG.md Generation

Generated comprehensive CHANGELOG.md covering all 14 tagged releases (1.0.0 through 1.0.13) plus Unreleased.

**Key observations:**
- Tags 1.0.10 and 1.0.11 point to the same commit (f72ce8b) — re-tag situation
- 1.0.2 was a version-bump-only release with no functional changes
- Major architecture shifts: 1.0.0 (initial), 1.0.3 (blocking diff + SSE), 1.0.5 (native VS editor APIs), 1.0.6 (Output Window logging + refactoring wave), 1.0.7 (live diagnostics + capture-based testing)
- PRs #2 (VS2022 compat) and #3 (Launch Copilot CLI menu) are the only external contributions
- Changelog follows Keep a Changelog format with compare links


### 2026-03-29 — CHANGELOG.md Polish (Hudson Note 1)

Applied Hudson's non-blocking correction to CHANGELOG.md release 1.0.9. The original entry merged two distinct GitHub Actions workflows into one bullet ("GitHub Actions release workflow for automated builds"). Split into two explicit bullets matching the actual commits: `ci.yml` (CI build/test, commit 7a7dd2a) and `release.yml` (automated release, commit 29e9b54). Straightforward factual fix — no judgment calls required.

---

## Session Update: 2026-03-29 Changelog Audit Cycle

**Work completed:**
- **Spawn 1:** Drafted CHANGELOG.md from git history — 14 releases with full commit coverage, compare links, and PR references.
- **Spawn 2:** Applied Hudson Note 1 fix — split 1.0.9 "GitHub Actions release workflow" into two bullets: CI workflow (ci.yml) + release workflow (release.yml).

**Merged decisions:**
- **ripley-changelog-polish.md:** Note 1 fix complete. Workflow separation documented as best practice for future infrastructure releases.

**Team learning:**
- When documenting infrastructure changes (CI, build tools), list each distinct workflow/tool separately rather than grouping under a single description. Improves traceability to commits and historical accuracy.
