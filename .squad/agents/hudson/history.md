# Project Context

- **Owner:** Sebastien
- **Project:** CopilotCliIde — A Visual Studio extension (VSIX) bridging GitHub Copilot CLI's /ide command with Visual Studio via MCP over named pipes. Three C# projects: CopilotCliIde (VS extension, net472), CopilotCliIde.Server (MCP server, net10.0), CopilotCliIde.Shared (contracts, netstandard2.0).
- **Stack:** C#, .NET, MSBuild, VSSDK, StreamJsonRpc, MCP, Windows named pipes
- **Created:** 2026-03-05

## Core Context

Hudson owns test suite, coverage analysis, and test infrastructure. Key decisions:
- **xUnit v3 migration (2026-03-05):** Migrated from v2 (2.9.3) to v3 (3.2.2); all 94 tests passed
- **143 tests current status:** Multi-session capture fix resolved 4 failing tests; all now pass
- **Test infrastructure:** 11 test files covering HTTP parsing, DTO serialization, tool discovery, protocol compatibility, traffic replay
- **TrafficParser:** Now session-aware; GetToolCallResponse scopes by sequence number to isolate IDs across sessions
- **Coverage gaps identified:** New tests needed for open_diff/close_diff/get_vscode_info response structures (P1 priority, deferred)
- **Golden snapshots:** Removed; replaced with proxy-based capture approach for ground truth

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->

- **Test project:** `CopilotCliIde.Server.Tests` (xUnit v3, net10.0) in `src/CopilotCliIde.Server.Tests/`. Run with `dotnet test src\CopilotCliIde.Server.Tests\CopilotCliIde.Server.Tests.csproj`. 94 tests covering HTTP parsing, chunked encoding, response writing, DTO serialization, tool discovery, RPC client events, and service provider DI.
- **Central Package Management:** All package versions must be in `Directory.Packages.props` — `PackageReference` in `.csproj` files must NOT have `Version` attributes. Test packages (xunit.v3 3.2.2, xunit.runner.visualstudio 3.1.4, Microsoft.NET.Test.Sdk 17.14.1, coverlet.collector 6.0.4, NSubstitute 5.3.0) are registered there.
- **xUnit v3 migration (2026-03-05):** Migrated from xUnit v2 (2.9.3) to xUnit v3 (3.2.2). Required changes: replace `xunit` package with `xunit.v3` in Directory.Packages.props, update PackageReference in test csproj, and add `<OutputType>Exe</OutputType>` to test project PropertyGroup. All 94 tests passed without any test code changes (basic xUnit v2 features are fully compatible with v3).
- **InternalsVisibleTo:** The server project exposes internals to `CopilotCliIde.Server.Tests` via `<InternalsVisibleTo>` in the csproj. Three HTTP helper methods in `McpPipeServer` were changed from `private static` to `internal static` for testability: `ReadHttpRequestAsync`, `ReadChunkedBodyAsync`, `WriteHttpResponseAsync`.
- **MCP tool names are a compatibility contract:** The 7 tool names (`get_vscode_info`, `get_selection`, `open_diff`, `close_diff`, `get_diagnostics`, `read_file`, `update_session_name`) must match VS Code's Copilot Chat extension exactly. `ToolDiscoveryTests` enforces this.
- **UpdateSessionNameTool is the only pure tool:** It has no RPC dependency and can be tested directly. All other tools are thin RPC forwarders.
- **ReadChunkedBodyAsync throws EndOfStreamException on truncated streams:** Due to `ReadExactlyAsync` usage for trailing `\r\n` — this is intentional behavior.
- **MCP schema alignment tests (2026-03-06):** Added 29 tests (97→126) for VS Code schema alignment. New file `ToolOutputSchemaTests.cs` validates snake_case keys in `open_diff`/`close_diff` tool outputs and `get_diagnostics` array-at-root format. Extended `DtoSerializationTests` with `VsInfoResult.AppName`/`Version`, `DiffResult.Result` SAVED/REJECTED uppercase, trigger values, `SelectionResult` JSON key shapes, diagnostics grouping, and `DiagnosticItem.Code`/`Source`. Extended `RpcClientTests` with `DiagnosticsChanged` event tests. Extended `NotificationFormatTests` with `diagnostics_changed` JSON-RPC format.
- **RpcClient is sealed:** Cannot be mocked with NSubstitute. Tool methods (other than `UpdateSessionNameTool`) can only be tested via output schema validation on the anonymous objects they produce, not via direct invocation with mocked dependencies.
- **Severity mapping centralization (2026-03-07):** Ripley promoted `VsServiceRpc.MapSeverity` to internal static, refactored `CopilotCliIdePackage.CollectDiagnosticsGrouped` to call it. No new VSIX unit test added (disproportionate infrastructure cost; indirect coverage via `NotificationFormatTests` sufficient). All 109 tests pass.
- **Team Notification (2026-03-07T11:41:21Z):** Hicks implemented husky pre-commit hook for whitespace enforcement. All team members should run `npm run format` before committing and `npm run format:check` in CI pipelines. The pre-commit hook validates all .NET files. See `.squad/decisions.md` — "Whitespace Enforcement via Husky Pre-Commit Hook" for details.
- **Protocol compatibility test infrastructure — Phase 1 (2026-03-07):** Created golden JSON snapshot infrastructure in `Snapshots/` directory (8 JSON files + README) with TYPE-PLACEHOLDER format for structural comparison. Built `JsonSchemaComparer` utility for superset comparison (walks JSON trees, checks property names/types, allows extras in actual, fails on missing). Added `ProtocolCompatibilityTests` with 3 tests: `ToolsList_ContainsAllVsCodeTools` (verifies 6 VS Code tool names present), `ToolsList_ToolInputSchemas_MatchGolden` (verifies each tool's parameter names and types match golden file), `LockFile_Schema_MatchesVsCode` (verifies 8 required fields with correct types). Updated csproj with `<Content Include="Snapshots\**" CopyToOutputDirectory="PreserveNewest" />`. All 112 tests pass (109 existing + 3 new). Phase 2 (McpHandshakeTests integration) and Phase 3 (per-tool response golden tests) are next.

### 2026-03-07T17:02:27Z — Protocol Compatibility Phase 1 Orchestration Complete

Orchestration log written to `.squad/orchestration-log/2026-03-07T17-02-27Z-hudson.md`. This spawn delivered the Phase 1 golden snapshot infrastructure for protocol compatibility test automation.

**Outcome:** ✅ SUCCESS. Golden Snapshots/ directory created with 8 JSON files, JsonSchemaComparer utility implemented, ProtocolCompatibilityTests class with 3 passing tests. Current test count: 112 passing (109 existing + 3 new). No regressions.

**Phase 1 complete.** Bishop's test seam (RpcClient constructor) is now in place. Phase 2 (handshake integration test, ~4 hours) can proceed using both Bishop's infrastructure and the golden snapshot framework completed here.

**Next:** Phase 2 — Bishop to lead handshake integration tests. Phase 3 and 4 deferred (per-tool golden tests and refresh script).

### 2026-03-07 — Deep Test Gap Analysis (Captures + Existing Tests)

- **131 tests currently passing** across 11 test files.
- **3 capture files** analyzed: vs-1.0.7 (42 entries), vscode-0.38 (38 entries), vscode-insiders-0.39 (39 entries).
- **Schema drift found:** `get_diagnostics.uri` parameter type changed from `"string"` (v0.38/0.39) to `["string","null"]` (VS 1.0.7). No test catches this.
- **Capability evolution:** VS 1.0.7 adds `logging` capability key not present in earlier captures.
- **4 of 7 tools never called in any capture:** `get_vscode_info`, `open_diff`, `close_diff`, `read_file`. Response schemas unvalidated from real traffic.
- **get_selection response differs across captures:** VS 1.0.7 omits `text` key when empty; vscode-0.39 includes it.
- **Every capture has exactly 2 `tools/list` calls.** Idempotency not tested.
- **Empty text / isEmpty=true** notifications dominate captures (10+ per file). Tests validate structure but don't specifically target this edge case.
- **Test 7 only validates tool names**, not initialize response shape, not input schemas, not auth rejection.
- **TrafficParser** has 3 JSON extraction strategies (SSE, brace-matching, direct parse) — none directly unit-tested.
- **Proposed 28 new tests** across 6 categories. Full report at `.squad/decisions/inbox/hudson-test-gap-analysis.md`.

### 2026-03-07 — Top 5 Priority Tests from Gap Analysis (A1, B1, B2, E1, C1)

- **5 new test methods added** to `TrafficReplayTests.cs`, bringing total from 131 to 140 (9 new executions: 5 methods, 3 are Theory×3 captures + 2 Fact).
- **A1 `AllCaptures_ToolInputSchemas_AreConsistent`** — Cross-capture [Fact] comparing tool inputSchema property names, types, and required fields across all captures. Documents `get_diagnostics:uri:type` as a known variation (`"string"` vs `["string","null"]`).
- **B1 `VsCodeGetSelectionResponse_HasExpectedStructure`** — [Theory] per-capture validation of `get_selection` response schema. Handles `"null"` text (no active editor) and validates `current`, `filePath`, `fileUrl`, `selection` structure. Documents that `text` key is optional (VS 1.0.7 omits it when empty).
- **B2 `VsCodeUpdateSessionNameResponse_HasExpectedStructure`** — [Theory] per-capture validation of `update_session_name` response: `content[0].text` must parse as `{"success":true}`.
- **E1 `OurServer_InitializeResponse_HasExpectedStructure`** — [Fact] integration test reusing pipe infrastructure from Test 7. Validates our server's initialize response has `protocolVersion`, `capabilities.tools.listChanged`, `serverInfo.name`, and `serverInfo.version`.
- **C1 `AllCaptures_RequestResponseIds_AreCorrelated`** — [Fact] cross-capture test verifying JSON-RPC id correlation. Every response id must match a request id; every request id must have a response. Tolerates truncated request frames (common in captures where chunked encoding hides the id field).
- **New helper `ExtractJsonRpcFromResponse`** added for parsing SSE/JSON/brace-matched responses from our MCP server (used by E1).
- All 140 tests pass. No production code changes.

### 2026-03-08 — Test Gap Analysis Round 2 (vs-1.0.7 Fresh Capture)

- **140 tests still passing** across 12 test files (confirmed baseline).
- **vs-1.0.7 capture key differences from round 1:** `source` field now present in `diagnostics_changed` (value: project name like "AzdTool.csproj"); `code` field present but null; `text` field always present in both `selection_changed` and `get_selection` (was previously noted as optional); `logging` capability added in initialize response (not in VS Code captures); out-of-order responses observed (id=8 before id=7).
- **Cross-capture field alignment:** `text` key in `selection_changed` is always present across all 3 captures (was previously uncertain for vs-1.0.7). `source` field is present in vs-1.0.7 `diagnostics_changed` but absent in VS Code 0.38 notifications. `code` is "CS0246" in VS Code get_diagnostics responses but null in vs-1.0.7 notifications.
- **Existing Test 6 gap confirmed:** `VsCodeDiagnosticsChanged_HasExpectedStructure` validates `range`, `message`, `severity` but does NOT check `source` or `code` fields. Same gap in Test 4 for get_diagnostics responses.
- **8 new tests recommended** (3 critical, 4 important, 1 nice-to-have). Top 3: diagnostic source/code field validation, get_selection integration test, auth rejection test. Full analysis at `.squad/decisions/inbox/hudson-test-round2.md`.
- **Tests not recommended (with rationale):** B3-B6 (no capture data), C2-C3 (timing/dedup), D4-D5 (already covered or hard to test), F1-F4 (testing test infrastructure).

### 2026-03-08 — Refactor + Round 2 Priority Tests (3 new tests, 1 refactor)

- **Removed `FindAllCaptureFiles()` method.** Cross-capture `[Fact]` tests (A1, C1, Test 7) now inline `Directory.GetFiles(FindCapturesDir(), "*.ndjson")`. `FindCapturesDir()` retained as shared infrastructure. All per-capture tests use `[Theory] [MemberData(nameof(CaptureFiles))]` with `TheoryData<string>`.
- **Extended Test 6 (`VsCodeDiagnosticsChanged_HasExpectedStructure`)** to validate `source` and `code` fields on diagnostic items when present. Type-checks: string or null. Handles cross-capture variation (vs-1.0.7 has `source`, VS Code 0.38 may not).
- **New Test E2 (`OurServer_GetSelectionResponse_HasExpectedStructure`)** — Integration test: mocks `IVsServiceRpc.GetSelectionAsync()` → returns `SelectionResult` with realistic data → calls `tools/call get_selection` over pipe → validates all fields (`text`, `filePath`, `fileUrl`, `selection`, `current`) and sub-fields (`start.line`, `start.character`, `end.line`, `end.character`, `isEmpty`).
- **New Test E3 (`OurServer_InvalidNonce_Returns401`)** — Integration test: connects to server, sends request with wrong nonce, asserts HTTP 401 Unauthorized response. Validates the nonce as the sole security boundary.
- **Fixed pre-existing Test B1 bug:** `VsCodeGetSelectionResponse_HasExpectedStructure` failed on vs-1.0.7 because `current: false` responses omit `filePath`/`fileUrl`/`selection`. Added early return when `current` is `false`.
- **xUnit v3 TheoryData note:** `TheoryDataRow<T>.GetData()` is `protected` in xUnit v3 — cannot extract values from `TheoryData<T>` in non-Theory tests. Use `FindCapturesDir()` directly for `[Fact]` tests needing all captures.
- **Test count:** 142 (was 140). All passing.

### 2026-03-08 — Test Suite Impact Analysis (Updated Captures)

- **143 tests total, 4 failing, 139 passing** after Sebastien updated all 3 captures with more tool invocations.
- **Captures now contain multiple MCP sessions** within a single file (vs-1.0.7: 3 sessions, vscode-0.38: 4 sessions, vscode-insiders-0.39: 2 sessions). JSON-RPC ids reset across sessions, causing id collision.
- **4 failing tests — root cause: multi-session id collision.** `GetToolCallResponse` (Strategy 1) finds a request id from session 2 but matches it to a response from session 1. Test C1 (`AllCaptures_RequestResponseIds_AreCorrelated`) fails because response ids are no longer unique across the entire capture.
- **New tool invocations in captures (previously never called):** `open_diff` (3-6 per capture, 3 response patterns: SAVED/accepted_via_button, REJECTED/rejected_via_button, REJECTED/closed_via_tool), `close_diff` (1-4 per capture, 2 patterns: already_closed=true/false), `get_vscode_info` (1-4 per capture).
- **`read_file` still never called** in any capture. No capture-based response validation possible.
- **TrafficParser needs session-aware correlation.** The `GetToolCallResponse` method must detect session boundaries (initialize responses) and scope id matching within sessions.
- **6 new capture-based tests proposed** (P1): open_diff response structure, close_diff response structure, get_vscode_info response structure. **2 schema tests proposed** (P2-P3): ClosedViaTool trigger, Timeout trigger.
- **No snapshot/golden files exist** in current codebase — Phase 1 infrastructure was apparently removed.
- **DtoSerializationTests and ToolOutputSchemaTests** already updated by Sebastien — no changes needed there.
- Full report at `.squad/decisions/inbox/hudson-test-impact-2026-03-08.md`.

### 2026-03-08 — New Replay Tests B3–B5 + ClosedViaTool Schema Test (Complete)

- **10 new test executions added** (143 → 153): 3 Theory tests × 3 captures = 9, plus 1 Fact.
- **B3 `OpenDiffResponse_HasExpectedStructure`** — [Theory] per-capture. Uses `GetAllToolCallResponses("open_diff")` to validate ALL open_diff responses. Checks MCP envelope (`content[0].type == "text"`), parses inner JSON, asserts `success` (bool), `tab_name` (string), `message` (string). On success: validates `result` is SAVED/REJECTED, `trigger` is one of 5 known values from `DiffTrigger`. Skips captures with no open_diff calls.
- **B4 `CloseDiffResponse_HasExpectedStructure`** — [Theory] per-capture. Uses `GetAllToolCallResponses("close_diff")`. Validates MCP envelope + inner JSON with `success`, `already_closed`, `tab_name`, `message`. Skips captures with no close_diff calls.
- **B5 `GetVsCodeInfoResponse_HasExpectedStructure`** — [Theory] per-capture. Uses `GetAllToolCallResponses("get_vscode_info")`. Validates MCP envelope + inner JSON. Only asserts `appName` and `version` (common across VS Code and VS). Skips captures with no get_vscode_info calls.
- **`OpenDiff_Output_ClosedViaTool`** — [Fact] in ToolOutputSchemaTests. Validates `DiffTrigger.ClosedViaTool` serializes correctly to `"closed_via_tool"` with `DiffOutcome.Rejected` result.
- **Fixed nullable warning** in B3: cast `knownTriggers` to `ISet<string?>` for `Assert.Contains` overload compatibility with xUnit v3.
- All 153 tests pass. No production code changes.

### 2026-03-09 — Deep Capture Inspection: Post-Update Test Gap Analysis

- **173 tests passing** as baseline (confirmed via `dotnet test`).
- **4 captures analyzed**: vs-1.0.8 (108 entries, our extension), vscode-0.38 (140 entries), vscode-0.39 (124 entries), vscode-insiders-0.39 (113 entries).
- **Key structural change**: All captures are now `type: "raw"` only. Bodies are parsed JSON objects where available, but HTTP frames dominate. Multiple MCP sessions per capture (3-4 each) identified by `Mcp-Session-Id` headers, with JSON-RPC ID resets across sessions.
- **DELETE /mcp disconnect**: New protocol pattern in 3 of 4 captures (not in vscode-0.38). Followed by `body: 0` integer response. No test coverage.
- **HTTP 400 retry sequences**: 17 retries in vscode-0.38, 6 in vscode-0.39. Error: "Session ID must be a single, defined, string value". No test coverage. Our extension (vs-1.0.8) has zero 400s.
- **HTTP 406 Not Acceptable**: One instance in vscode-0.38 — content negotiation failure. No test coverage.
- **GET /mcp SSE stream**: Present in all captures (seq ~5-6). Not tested in any replay or integration test.
- **Cross-capture diagnostic fields**: `source` field present in vs-1.0.8 diagnostics_changed but absent in VS Code 0.38. `code` field present in all but values differ (string codes vs null).
- **open_diff/close_diff lifecycle**: Rich patterns in captures — vscode-0.38 shows close_diff before open_diff (LLM cleaning up previous diffs). No lifecycle pairing test exists.
- **Snapshots/ directory removed**: ProtocolCompatibilityTests and JsonSchemaComparer no longer exist. Not a gap — approach shifted to capture-based replay.
- **11 gaps identified** (2 P1-Critical, 6 P2-Important, 2 P3-Nice-to-have, 1 resolved). Full report at `.squad/decisions/inbox/hudson-test-gaps.md`.

### 2026-03-09 — Deep Test Coverage Inspection & Gap Analysis

Completed final comprehensive test gap analysis of all 4 updated capture files (vs-1.0.8, vscode-0.38, vscode-0.39, vscode-insiders-0.39) with 173 tests as baseline.

**Gap Summary (11 total):**

**P1-Critical (must address):**
1. **DELETE /mcp disconnect not validated** — 3 of 4 captures show DELETE followed by empty body. Tests should validate request/response structure and session cleanup. Effort: Small.
2. **HTTP 400 retry sequence not tested** — vscode-0.38 has 17 retries, vscode-0.39 has 6, our vs-1.0.8 has 0. Tests should verify error format and eventual success. Effort: Medium.

**P2-Important (should address):**
3. HTTP 406 Not Acceptable error (Small, part of #2)
4. `body: 0` non-object parser robustness (Small)
5. GET /mcp SSE stream initiation validation (Medium)
6. Multi-session MCP-Session-Id boundary regression (Medium) — core parser fix, high regression risk
7. open_diff → close_diff lifecycle pairing (Medium) — blocking semantics validation
8. Cross-capture output format consistency (Medium) — response shape divergence detection

**P3-Nice-to-Have:**
9. HTTP 202 Accepted response validation (Small)
10. `read_file` tool capture (Deferred — never invoked)
11. Snapshot staleness (Resolved — approach shifted to capture-based replay)

**Assessment:** 8-10 new test methods needed (~15-20 test executions total). Prioritize P1-Critical first (DELETE, HTTP 400), then P2-Important (multi-session boundary, lifecycle, consistency).

**Deliverable:** Orchestration log written to `.squad/orchestration-log/2026-03-09T20-31-14Z-hudson.md` with detailed gap prioritization.



