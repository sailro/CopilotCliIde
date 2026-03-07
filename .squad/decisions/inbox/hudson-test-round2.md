# Hudson — Test Gap Analysis Round 2

**Date:** 2026-03-08
**Author:** Hudson (Tester)
**Context:** Fresh vs-1.0.7 capture analyzed against 140 passing tests. Round 2 of gap analysis.

## New Findings from vs-1.0.7 Capture

The fresh capture reveals several alignment changes and new fields that existing tests don't cover:

1. **`source` field now present in `diagnostics_changed`** — value `"AzdTool.csproj"` (project name). VS Code 0.38 does NOT have `source` in `diagnostics_changed` notifications. Our server's `PushDiagnosticsChangedAsync` includes it. **No test validates this.**
2. **`code` field is `null` in `diagnostics_changed`** — vs-1.0.7 shows `code: None`. VS Code 0.38's `get_diagnostics` response has `code: "CS0246"`. Type inconsistency (null vs string) untested.
3. **`logging` capability** — vs-1.0.7 returns `"capabilities": {"logging": {}, "tools": {...}}`. VS Code captures only have `"tools"`. Our server does NOT include `logging`. No test catches this drift.
4. **`text` field always present** in both `selection_changed` notifications AND `get_selection` responses across all 3 captures (including empty string `""`). Our `GetSelectionTool` explicitly ensures this with `result.Text ?? ""`. Untested.
5. **`current: false` state** in `get_selection` — vs-1.0.7 shows `{text: "", current: false}` when no editor is active. Distinct from vscode-0.38's `null` response. Untested distinction.
6. **Out-of-order responses** — vs-1.0.7 returns id=8 before id=7 (SEQ 41 before SEQ 43). Existing C1 test validates correlation but not ordering.

## Prioritized Test Recommendations (Top 8)

### 🔴 1. Diagnostics Notification `source`/`code` Field Validation

**Name:** `VsCodeDiagnosticsChanged_DiagnosticItems_HaveCodeAndSourceFields`
**Validates:** That `diagnostics_changed` notification diagnostic items include `code` and `source` keys when diagnostics are non-empty.
**Source:** **NEW** — discovered from vs-1.0.7 capture analysis.
**What it catches:** Existing Test 6 (`VsCodeDiagnosticsChanged_HasExpectedStructure`) validates `range`, `message`, `severity` but skips `code` and `source`. The vs-1.0.7 capture proves our server sends both. If either gets dropped, CLI could lose error codes and project attribution. Cross-capture: VS Code 0.38 has `code` but not `source` in notifications — test should document this as a known variation.

### 🔴 2. Our Server get_selection Response Shape (Integration)

**Name:** `OurServer_GetSelectionResponse_MatchesCaptureStructure`
**Validates:** Integration test: connect to our MCP server, mock the RPC layer to return a SelectionResult, call `tools/call` with `get_selection`, verify response has `text`, `filePath`, `fileUrl`, `selection`, `current` fields with correct types.
**Source:** Original list **E4** — still relevant.
**What it catches:** No integration test validates our server's actual get_selection response end-to-end over the pipe. The capture shows `text` should always be present (even as `""`) and `current` distinguishes active vs cached. Catches regressions in `GetSelectionTool`'s anonymous object construction.

### 🔴 3. Auth Rejection (401 Unauthorized)

**Name:** `OurServer_InvalidNonce_Returns401`
**Validates:** Integration test: connect to our MCP server with wrong nonce, send initialize request, verify 401 response with no further processing.
**Source:** Original list **D1** — high value, now feasible with existing pipe infra from E1.
**What it catches:** Broken auth bypass. The nonce is the ONLY security boundary. Zero test coverage on this critical path. Pipe infrastructure already exists in TrafficReplayTests.

### 🟡 4. Cross-Capture Capability Consistency

**Name:** `AllCaptures_InitializeCapabilities_AreDocumented`
**Validates:** Compare `capabilities` object across all captures. Document that vs-1.0.7 adds `logging: {}` not present in VS Code captures. Flag any undocumented new capability keys.
**Source:** **NEW** — discovered from vs-1.0.7 capture analysis.
**What it catches:** Capability drift between IDE implementations. The `logging` key in vs-1.0.7 means our server advertises a capability VS Code doesn't (or vice versa). This test would be similar to A1 but for the initialize response rather than tool schemas.

### 🟡 5. get_diagnostics Response `code`/`source` Fields

**Name:** `VsCodeGetDiagnosticsResponse_DiagnosticItems_HaveCodeField`
**Validates:** That `get_diagnostics` tool call response diagnostic items include `code` field (string or null). Optionally `source`.
**Source:** **NEW** — extension of Test 4 (`VsCodeGetDiagnosticsResponse_HasExpectedStructure`).
**What it catches:** Test 4 validates the outer structure (content→text→array→uri/filePath/diagnostics→message/severity/range) but does NOT check `code` or `source` inside diagnostic items. The vscode-0.38 capture has `code: "CS0246"` inside `get_diagnostics` responses. This is the tool response path (vs recommendation 1 which covers the notification path).

### 🟡 6. Our Server get_diagnostics Response Shape (Integration)

**Name:** `OurServer_GetDiagnosticsResponse_MatchesCaptureStructure`
**Validates:** Integration test: mock RPC to return DiagnosticsResult with files/diagnostics, call `tools/call` with `get_diagnostics`, verify response matches capture format — array of `{uri, filePath, diagnostics: [{message, severity, range, code, source}]}`.
**Source:** Original list **E3** — still relevant.
**What it catches:** Validates the full path from DiagnosticsResult DTO through GetDiagnosticsTool anonymous object to JSON-RPC serialization. No integration test exists for this tool.

### 🟡 7. tools/list Idempotency

**Name:** `OurServer_ToolsListCalledTwice_ReturnsSameResult`
**Validates:** Integration test: call `tools/list` twice on our server, verify both responses have identical tool names, counts, and schemas.
**Source:** Original list (evolved from capture observation that every capture has exactly 2 `tools/list` calls).
**What it catches:** Non-deterministic tool discovery, state mutation between calls. Simple to add using existing pipe infrastructure.

### 🟢 8. Invalid HTTP Method Response

**Name:** `OurServer_UnsupportedHttpMethod_ReturnsError`
**Validates:** Integration test: send PUT/PATCH/DELETE to our MCP server, verify it returns an error response (currently 404).
**Source:** Original list **D2**.
**What it catches:** Unhandled method crashes. Production code returns 404 (arguably should be 405). Low priority since it's a defensive edge case rarely hit in production.

## Tests NOT Recommended (with rationale)

| Original ID | Why Skip |
|---|---|
| B3-B6 (uncalled tool responses) | No capture data for `get_vscode_info`, `open_diff`, `close_diff`, `read_file` tool responses. Output schema tests already cover these. |
| C2 (notification ordering) | Timing-dependent, low signal. Hard to test deterministically. |
| C3 (dedup notifications) | Implementation detail, not protocol contract. |
| D4 (missing Content-Length) | Already covered by chunked encoding tests. |
| D5 (SSE disconnection) | Hard to test reliably in unit test context. |
| F1-F4 (TrafficParser tests) | Testing test infrastructure has diminishing returns. |

## Summary

| Priority | Count | Coverage Area |
|---|---|---|
| 🔴 Critical | 3 | Diagnostic fields, selection integration, auth |
| 🟡 Important | 4 | Capabilities, diagnostic code fields, diagnostics integration, idempotency |
| 🟢 Nice-to-have | 1 | Invalid HTTP method |

**Recommended implementation order:** #1 (source/code validation) → #3 (auth rejection) → #2 (get_selection integration) → #4 (capabilities) → #5 (diagnostic code fields)

Tests 1, 4, 5 are pure capture analysis (fast to implement). Tests 2, 3, 6, 7, 8 are integration tests using existing pipe infrastructure.
