# Squad Decisions

## Active Decisions

### xUnit v3 Migration

**Author:** Hudson (Tester)
**Date:** 2026-03-05

Migrated test project from xUnit v2 (2.9.3) to xUnit v3 (3.2.2).

**Key Points:**
- Updated `Directory.Packages.props`: `xunit` → `xunit.v3` (2.9.3 → 3.2.2)
- Updated `CopilotCliIde.Server.Tests.csproj`: PackageReference, added `<OutputType>Exe</OutputType>`
- All 94 tests passed without code changes — basic xUnit features (attributes, assertions) are fully compatible
- Test infrastructure unchanged; `xunit.runner.visualstudio` (3.1.4) handles discovery and execution

**Impact:** Team should run `dotnet restore` to fetch xunit.v3. No impact on test authoring or CI/CD.

### Server Test Project Structure

**Author:** Hudson (Tester)
**Date:** 2025-07-17

Created `CopilotCliIde.Server.Tests` (xUnit, net10.0) with 94 tests across 10 test files.

**Key Points:**
- Added `InternalsVisibleTo` to the server project; 3 private static HTTP helpers (`ReadHttpRequestAsync`, `ReadChunkedBodyAsync`, `WriteHttpResponseAsync`) changed to `internal static` for testability
- All test package versions in `Directory.Packages.props` (Central Package Management)
- Tool discovery tests enforce MCP tool name compatibility contract with VS Code
- Run tests: `dotnet test src\CopilotCliIde.Server.Tests\CopilotCliIde.Server.Tests.csproj`

**Impact:** All team members should run `dotnet test` before pushing server changes. Tool name tests catch accidental schema breaks.

## Reverse-Engineering Decisions (2026-03-06)

### Lock File Format Compatibility

**Author:** Ripley (Lead)  
**Status:** Aligned

Schema comparison of `~/.copilot/ide/{uuid}.lock` across VS Code (v0.38.2026022303) and CopilotCliIde:

**Verdict:** Identical 8-field structure (`socketPath`, `scheme`, `headers`, `pid`, `ideName`, `timestamp`, `workspaceFolders`, `isTrusted`). Fully compatible.

**One difference:** VS Code updates lock file when:
- Workspace folders change (`workspace.onDidChangeWorkspaceFolders`)
- Workspace trust is granted (`workspace.onDidGrantWorkspaceTrust`)

Our implementation recreates the lock file on solution switch, which is less granular but sufficient for single-solution scenarios. **Recommendation:** Low-priority feature (multi-root support is a VS Code concept; VS rarely opens multiple solutions).

**No action required** on schema. Lock files remain compatible.

---

### MCP Tool Schemas & Compatibility

**Author:** Bishop (Server Dev)  
**Status:** Needs alignment (medium effort, high importance)

VS Code registers **6 MCP tools**; we register **7** (extra `read_file`, which is harmless).

#### Tool Inventory ✅
All 6 tools present with matching names. No compatibility issues.

#### Critical Differences (must align)

**1. `get_vscode_info` response**
- VS Code returns: `version`, `appName`, `appRoot`, `language`, `machineId`, `sessionId`, `uriScheme`, `shell`
- We return: `ideName`, `solutionPath`, `solutionName`, `solutionDirectory`, `projects`, `processId`
- **Issue:** No shared field names except implicitly (`appName` ↔ `ideName`?)
- **Action:** Add `appName: "Visual Studio"` and `version: "..."` fields to our response for discovery

**2. `get_selection` response structure**
- VS Code: nested `selection: {start: {line, character}, end: {line, character}, isEmpty}`
- We: flat `startLine`, `startColumn`, `endLine`, `endColumn`, `isEmpty`
- **Field names:** `text` vs `selectedText`, `fileUrl` vs `fileUri`
- **Action:** Align field names and nest `selection` object to match VS Code shape

**3. `get_diagnostics` response structure**
- VS Code: grouped by file, each file has `{uri, filePath, diagnostics: [{message, severity, range, source, code}]}`
- We: flat list `{file, severity, message, line, column, project}`
- **Issue:** Missing range end position, `source`, `code` per diagnostic
- **Action:** Restructure to group by file, include range objects, add source/code fields

**4. `open_diff` response**
- VS Code returns: `success`, `result: "SAVED"|"REJECTED"` (uppercase), `trigger`, `tab_name`, `message`
- We return: `success`, `userAction`, `tabName` (camelCase), `diffId`, `error`, `originalFilePath`, `proposedFilePath`
- **Issue:** Missing `result` and `trigger` fields; uppercase vs lowercase; snake_case vs camelCase
- **Action:** Add `result` field (copy/alias of `userAction`), add `trigger` field, uppercase values, align casing

**5. `close_diff` response casing**
- VS Code: `already_closed`, `tab_name` (snake_case)
- We: `alreadyClosed`, `tabName` (camelCase)
- **Action:** Align to snake_case for consistency

#### Moderate Differences (should align)

**6. Push notification: `diagnostics_changed`**
- VS Code broadcasts this with 200ms debounce (like `selection_changed`)
- We don't implement it
- **Action:** Consider adding (medium effort, enables real-time error feedback in CLI)

**7. `selection_changed` debounce**
- VS Code: 200ms debounce
- We: instant push (dedup only)
- **Action:** Add 200ms debounce to reduce pipe chatter on rapid cursor movements

#### Low-Priority Differences

- `read_file` tool: we expose it, VS Code doesn't. Harmless extra capability.
- Server version `"1.0.0"` vs `"0.0.1"`: cosmetic difference.
- `taskSupport: forbidden` vs absent: we're more explicit, no functional difference.

#### Prioritized Recommendations
1. **P0:** Add `appName`/`version` to `get_vscode_info`, uppercase `open_diff` values, add `result`/`trigger` fields
2. **P1:** Align `get_selection` and `get_diagnostics` response shapes
3. **P2:** Add `diagnostics_changed` notification, 200ms debounce

---

### open_diff Implementation & Selection Tracking

**Author:** Hicks (Extension Dev)  
**Status:** Acceptable (InfoBar UX valid, minor refinements needed)

#### open_diff UI & Blocking Mechanism

VS Code uses **editor title bar buttons** (✓/✕ icons); we use **InfoBar inside the diff frame**. Both provide clear accept/reject affordances for the user. Our InfoBar approach is idiomatic to Visual Studio and acceptable.

**Critical difference: resolution values**
- VS Code: `"SAVED"` / `"REJECTED"` (uppercase strings)
- We: `"accepted"` / `"rejected"` (lowercase)
- **Action:** Uppercase our values for protocol alignment

**Missing fields in response:**
- We lack `result` (enum: "SAVED"/"REJECTED") and `trigger` (enum: "accepted_via_button"/"rejected_via_button"/"closed_via_tool"/"client_disconnected")
- **Action:** Add both fields; `result` can alias `userAction` with uppercase value

**Our advantages over VS Code:**
- Tab close auto-rejects (VS Code blocks indefinitely until session disconnect or `close_diff` tool — our UX is better)
- Initial selection push on SSE connect (VS Code doesn't; our approach immediately shows active file to CLI)
- **Keep both improvements**

#### Selection Tracking & Notifications

Our `selection_changed` notification format is a **perfect match** with VS Code. No changes needed there.

**Recommendation:** Add 200ms debounce (low priority for correctness, medium priority for performance).

---

### Custom Headers in MCP Protocol

**Author:** Live proxy capture  
**Status:** Documented

During named pipe packet capture, identified three custom HTTP headers used by Copilot CLI:
- `X-Copilot-Session-Id` — uniquely identifies the CLI session
- `X-Copilot-PID` — Copilot CLI process ID
- `X-Copilot-Parent-PID` — parent process ID (for tracking spawned CLI instances)

No action required; these are informational headers used for telemetry/debugging.

---

### User Directive: Model Preference

**Date:** 2026-03-05T18:18:52Z  
**User:** Sebastien (via Copilot)  
**Directive:** Use `claude-opus-4.6` as preferred model for all squad agents

This directive overrides team defaults and applies to all future spawns.

## Implementation Decisions (2026-03-06)

### MCP Tool Schema Alignment with VS Code

**Author:** Bishop (Server Dev)  
**Date:** 2026-03-06  
**Status:** Implemented

Aligned all MCP tool response schemas to match VS Code's Copilot Chat extension wire format. The goal is byte-level compatibility so Copilot CLI sees identical JSON shapes from both VS Code and Visual Studio.

#### Key Design Choices

**Snake_case via anonymous objects (not JSON attributes)**  
For `open_diff` and `close_diff` responses, VS Code uses snake_case (`tab_name`, `already_closed`) while our RPC DTOs use PascalCase. Rather than adding `System.Text.Json` to the netstandard2.0 Shared project, the tool layer transforms DTOs to anonymous objects with the correct casing. This keeps the Shared project lean and avoids dual-serializer attribute conflicts (Newtonsoft for StreamJsonRpc vs System.Text.Json for MCP).

**Diagnostics grouped by file**  
`get_diagnostics` now returns a `List<FileDiagnostics>` array at root (not wrapped in an object), matching VS Code's format. Each file group has `uri`, `filePath`, and a `diagnostics` array with `range`, `message`, `severity`, `source`, `code`.

**Severity mapping**  
VS Error List's `vsBuildErrorLevel` enum maps to lowercase strings: High→"error", Medium→"warning", Low→"information". No "hint" mapping since VS Error List doesn't distinguish hints from information.

#### Files Changed

- `Contracts.cs` — SelectionResult, DiagnosticsResult, VsInfoResult, DiffResult, new DTO classes
- `VsServiceRpc.cs` — Populate new structures
- `OpenDiffTool.cs`, `CloseDiffTool.cs`, `GetDiagnosticsTool.cs` — Transform for MCP output
- `McpPipeServer.cs` — Updated PushCurrentSelectionAsync
- `RpcClient.cs` + `Program.cs` — diagnostics_changed push notification plumbing

#### Impact

Server compiles clean. VS extension side now includes diagnostics push subscriptions and timer-based selection debounce. Tests will need updates for new DTO structures (Hudson).

---

### Extension Alignment with VS Code

**Author:** Hicks (Extension Dev)  
**Date:** 2026-03-06  
**Status:** Implemented

Implemented three alignment changes to match VS Code's Copilot CLI /ide behavior.

#### Changes

**1. open_diff Resolution Values**

Changed from lowercase strings to typed tuple `(Result, Trigger)`:
- `"accepted"` → `Result: "SAVED"`, `Trigger: "accepted_via_button"`
- `"rejected"` → `Result: "REJECTED"` with contextual trigger (`rejected_via_button`, `closed_via_tab`, `timeout`, `closed_via_tool`)
- `UserAction` field kept for backward compat (mapped from Result)
- Tab close still resolves as rejected (intentional divergence from VS Code which hangs)

**2. 200ms Selection Debounce**

- `System.Threading.Timer` resets on each selection change
- Data captured eagerly on UI thread (volatile fields), pushed after quiet period
- Existing `_lastSelectionKey` dedup kept as second filter
- Prevents flooding named pipe during rapid cursor movement (arrow key hold)

**3. Diagnostics Push Notifications**

- Subscribes to `BuildEvents.OnBuildDone` + `DocumentEvents.DocumentSaved`
- 200ms debounce, then collects Error List items grouped by URI
- Pushes via `IMcpServerCallbacks.OnDiagnosticsChangedAsync`
- Does NOT subscribe to real-time Roslyn analyzer changes (would need ITableManager/MEF — future enhancement)

#### Impact

- MCP server `RpcClient` updated to handle new `OnDiagnosticsChangedAsync` callback
- DiffResult DTO has new `Result`/`Trigger` fields (additive, non-breaking)
- Timer lifecycle tied to `StopConnection()`/`Dispose()` — no leaks
- 97 tests pass

---

### PathUtils is Protocol-Required, Not a Hack

**Author:** Ripley (Lead)  
**Date:** 2026-03-07  
**Status:** Validated & Enforced

#### Context

Sebastien questioned whether `PathUtils.cs` could be replaced with BCL equivalents (`System.Uri`, `System.IO.Path`).

#### Analysis

`System.Uri` produces `file:///C:/Dev/file.cs` (uppercase drive, literal colon).  
VS Code protocol requires `file:///c%3A/Dev/file.cs` (lowercase drive, encoded `%3A`).  
No BCL method handles either transformation.

Both `ToVsCodeFileUrl` and `ToLowerDriveLetter` are **protocol-required**, not hacky wrappers.

#### Bugs Found & Fixed

Three call sites in `VsServiceRpc.cs` and `CopilotCliIdePackage.cs` used raw `new Uri(path).ToString()` instead of PathUtils:

1. `VsServiceRpc.GetSelectionAsync()` — `FileUrl` and `FilePath` produced wrong format
2. `VsServiceRpc.GetDiagnosticsAsync()` — `Uri` and `FilePath` wrong
3. `CopilotCliIdePackage.CollectDiagnosticsGrouped()` — diagnostics push `Uri` wrong

This caused inconsistency: tool responses (wrong format) vs push notifications (correct format) had different URIs.

**All three sites now use PathUtils consistently. Server builds clean, 109 tests pass.**

#### Rule

**Any code producing file URIs for the MCP protocol MUST use `PathUtils.ToVsCodeFileUrl`, never `new Uri(path).ToString()`.**

---

### Severity Mapping Deduplication

**Author:** Ripley (Lead)  
**Date:** 2026-03-07  
**Status:** Implemented

## Context

`vsBuildErrorLevel` → severity string mapping (`"error"`, `"warning"`, `"information"`) was duplicated:
1. `VsServiceRpc.MapSeverity` — explicit 4-arm switch (private static)
2. `CopilotCliIdePackage.CollectDiagnosticsGrouped` — inline 3-arm switch (missing explicit `Low` case)

The severity strings are wire protocol values that must match VS Code exactly.

## Decision

Promoted `VsServiceRpc.MapSeverity` from `private static` to `internal static`. `CopilotCliIdePackage` now calls it instead of duplicating the logic.

### Alternatives Considered

- **Move to Shared project**: Rejected. `vsBuildErrorLevel` is `EnvDTE80` (VS SDK). Shared is netstandard2.0 with no VS SDK dependency.
- **New utility class in extension**: Rejected. Both callers are in the same project. A new file for a 5-line pure function is over-engineering.
- **Raw int mapping in Shared**: Rejected. Would require callers to cast the enum, adding complexity for no architectural gain.

## Impact

- `VsServiceRpc.cs`: `MapSeverity` visibility changed from `private` to `internal`
- `CopilotCliIdePackage.cs`: Inline switch replaced with `VsServiceRpc.MapSeverity(item.ErrorLevel)`
- Server + extension build clean, 109 tests pass

---

### Decision: Skip dedicated unit test for VsServiceRpc.MapSeverity

**Author:** Hudson (Tester)  
**Date:** 2026-03-07  
**Status:** Accepted

## Context

Ripley promoted `VsServiceRpc.MapSeverity` from `private` to `internal static` in the VSIX project (`CopilotCliIde`, net472). The task asked whether a focused unit test would be valuable.

## Decision

**Do not add a unit test for `MapSeverity` at this time.**

## Rationale

1. **Assembly boundary prevents testing.** `MapSeverity` lives in `CopilotCliIde` (net472, VS SDK), not `CopilotCliIde.Server` (net10.0). The existing test project (`CopilotCliIde.Server.Tests`, net10.0) cannot reference the VSIX project — different target frameworks and VS SDK COM dependencies (`EnvDTE80.vsBuildErrorLevel`). The `InternalsVisibleTo` on the Server project doesn't help here.

2. **Infrastructure cost is disproportionate.** Testing this would require either a new net472 test project with VS SDK references, or moving the method/enum to the Shared project. Both are significant plumbing changes for a 4-line switch expression.

3. **The mapping is trivially correct by inspection.** Three named enum values map to three string literals, with a safe default. Risk of regression is minimal.

4. **Existing coverage provides indirect validation.** `NotificationFormatTests.DiagnosticsChangedNotification_MatchesExpectedFormat` already validates that severity strings appear correctly in serialized notification format.

## If this changes

If the VSIX project ever gets its own test project (or if MapSeverity moves to Shared), a parameterized test covering High→"error", Medium→"warning", Low→"information", and unknown→"information" would be a good addition.

---

### Convert MapSeverity to Extension Method on vsBuildErrorLevel

**Author:** Hicks (Extension Dev)  
**Date:** 2026-03-07  
**Status:** Implemented

## Context

`VsServiceRpc.MapSeverity` was an `internal static` helper that mapped `EnvDTE80.vsBuildErrorLevel` enum values to protocol severity strings (`"error"`, `"warning"`, `"information"`). Called from two places: `VsServiceRpc.GetDiagnosticsAsync` and `CopilotCliIdePackage.CollectDiagnosticsGrouped`.

## Decision

Converted to an extension method `ToProtocolSeverity()` on `vsBuildErrorLevel`, housed in a new `BuildErrorLevelExtensions` class in `src/CopilotCliIde/BuildErrorLevelExtensions.cs`.

Call sites now read `item.ErrorLevel.ToProtocolSeverity()` instead of `VsServiceRpc.MapSeverity(item.ErrorLevel)`.

## Why

- **Natural API.** The conversion belongs to the type, not to `VsServiceRpc`.
- **Focused RPC class.** Keeps `VsServiceRpc` focused on RPC concerns, not utility mapping.
- **Stays in CopilotCliIde.** The method depends on VS SDK's `vsBuildErrorLevel`, so it can't move to the Shared project (netstandard2.0).

## Files Changed

- **Added:** `src/CopilotCliIde/BuildErrorLevelExtensions.cs` — new extension method class
- **Modified:** `src/CopilotCliIde/VsServiceRpc.cs` — removed `MapSeverity` method, updated call site
- **Modified:** `src/CopilotCliIde/CopilotCliIdePackage.cs` — updated call site

## Impact

- Server builds clean
- 109 tests pass
- No behavioral change — same protocol mapping, cleaner API

---

### Extract ErrorListReader for Diagnostics Deduplication

**Author:** Hicks (Extension Dev)  
**Date:** 2026-03-07  
**Status:** Implemented

## Context

`VsServiceRpc.GetDiagnosticsAsync` (RPC on-demand) and `CopilotCliIdePackage.CollectDiagnosticsGrouped` (push notification) contained ~30 lines of identical Error List iteration logic: DTE access, item capping, file grouping, URI generation, 0-based position conversion, and DiagnosticItem construction.

## Decision

Extracted into `ErrorListReader.CollectGrouped()` — a new `internal static` helper class in the extension project. Returns `List<FileDiagnostics>` with optional file filter and configurable max items.

## Rationale

- Eliminates copy-paste divergence risk (different caps, different DTOs hiding identical logic)
- Follows the project pattern of focused utility classes (`BuildErrorLevelExtensions`, `PathUtils`)
- ~30 lines of DTE-dependent logic justifies its own file (unlike `MapSeverity` which was 4 lines)
- Both callers preserve their existing threading contracts and return types

## Implementation

- **New file:** `src/CopilotCliIde/ErrorListReader.cs`
- **Modified:** `VsServiceRpc.cs` (simplified GetDiagnosticsAsync), `CopilotCliIdePackage.cs` (simplified CollectDiagnosticsGrouped)
- Build clean, 0 warnings, 109 tests pass

---

### User Directive: Code Format Enforcement

**Date:** 2026-03-07T11:38:30Z  
**User:** Sebastien (via Copilot)  
**Directive:** Enforce code style by running `dotnet format .\CopilotCliIde.slnx whitespace --verbosity quiet` on a regular basis

This directive was implemented by Hicks via husky pre-commit hook.

---

### Whitespace Enforcement via Husky Pre-Commit Hook

**Author:** Hicks (Extension Dev)  
**Date:** 2026-03-07  
**Status:** Implemented

## Context

Sebastien requested regular whitespace code style enforcement across the solution.

## Decision

Implemented **husky** for a git pre-commit hook (read-only check) rather than CI-only or standalone shell script. Rationale:

- `node_modules` and `package.json` already in repo — husky is a natural fit
- Pre-commit hooks catch violations *before* they enter the repo
- Added npm scripts for manual runs and CI use
- Pre-commit hook uses `--verify-no-changes` (read-only); `npm run format` auto-fixes

## What Changed

| File | Change |
|------|--------|
| `package.json` | Added `husky` devDep, `format` and `format:check` scripts |
| `.husky/pre-commit` | Runs `dotnet format --verify-no-changes` |

## Usage

- `npm run format` — auto-fix whitespace issues
- `npm run format:check` — check without modifying (CI-friendly)
- Committing triggers the check automatically via husky

## Notes

No formatting fixes were needed — the codebase was already clean when `dotnet format` ran.

---

### User Directive: Dispose Method Threading

**Date:** 2026-03-07T14:35Z  
**User:** Sebastien (via Copilot)  
**Directive:** Never use `ThreadHelper.ThrowIfNotOnUIThread()` in Dispose methods. Do not assume Dispose runs on the UI thread.

This directive captured for team memory — enforced in Extension Dev practices.

---

### HTTP Response Framing: Match VS Code Express Server

**Author:** Bishop (Server Dev)  
**Date:** 2026-03-08  
**Status:** Implemented

All HTTP response headers from `McpPipeServer` are now lowercase (matching VS Code's Express server), and POST responses with `text/event-stream` content type use `Transfer-Encoding: chunked` instead of `Content-Length`.

#### Rationale

Traffic captures comparing our MCP server against VS Code's showed 3 framing differences. While HTTP headers are case-insensitive per RFC 7230, matching Express's lowercase output byte-for-byte maximizes compatibility with any Copilot CLI parsing that might be case-sensitive in practice.

#### Rules Going Forward

1. **All new HTTP response headers must be lowercase.** Do not use PascalCase (`Content-Type`) — use `content-type`.
2. **SSE responses (`text/event-stream`) must use chunked encoding.** Only plain-text error responses (400, 401, 404, etc.) use `Content-Length`.
3. **SSE chunk writes must be atomic.** Combine chunk size + data + trailing CRLF into a single `WriteAsync` call. Never split across multiple writes.

#### Affects

- Bishop: Any new HTTP response code in `McpPipeServer`
- Hudson: Test assertions for HTTP headers must use lowercase

**Result:** 131 tests pass. Committed as 7c460b8.

---

### Bishop's Capture Analysis — VS Code v0.38 vs v0.39 vs Our Server

**Author:** Bishop (Server Dev)  
**Date:** 2026-03-07  
**Status:** Analysis Complete

Analyzed traffic captures from VS Code Stable (v0.38) and Insiders (v0.39) to identify protocol differences.

#### Key Findings

**v0.38 vs v0.39:** Protocol-identical. All tools, notifications, and headers match exactly.

**VS Code vs Our Implementation:**

1. **serverInfo.version mismatch** — we return `"1.0.0"`, should be `"0.0.1"` for consistency
2. **Extra tool:** we expose `read_file` (harmless; intentional per project docs)
3. **Casing concerns (P0):** Our DTOs serialize with PascalCase (`SelectionResult`, `VsInfoResult`). VS Code uses camelCase. If the MCP .NET SDK uses PascalCase (unlikely), this breaks compatibility for `get_selection`, `get_vscode_info`, `get_diagnostics`.
4. **get_selection null handling (P1):** VS Code returns literal `"null"` when no editor is active; we return a serialized object with null fields.
5. **get_diagnostics source field (P1):** We include `source`, VS Code's captures omit it.
6. **HTTP framing (P2):** We use `Content-Length`, VS Code uses chunked encoding and lowercase headers.

#### Test Opportunities

Outlined 10 new integration tests to replay exact VS Code traffic and validate compatibility.

#### Action Items

- P0: Verify MCP .NET SDK serialization behavior (camelCase vs PascalCase)
- P1: Align `get_selection` null behavior and `get_diagnostics` field set
- P2: Use chunked encoding and lowercase headers for HTTP responses

**Related:** See "Golden Snapshots Now Sourced from Real VS Code Extension" for protocol gaps discovered during snapshot refresh.

---

### Golden Snapshots Now Sourced from Real VS Code Extension

**Author:** Bishop (Server Dev)  
**Date:** 2026-03-07  
**Status:** Completed

Replaced all 8 golden snapshot files in `src/CopilotCliIde.Server.Tests/Snapshots/` with data extracted directly from VS Code Insiders Copilot Chat extension **v0.39.2026030604** (`dist/extension.js`).

#### Protocol Gaps Discovered

1. **`get_vscode_info` response:** VS Code returns `{version, appName, appRoot, language, machineId, sessionId, uriScheme, shell}`. Our implementation returns completely different fields. When response tests are wired up, these 6 fields will be flagged as missing.

2. **`diagnostics_changed` notification:** VS Code includes `source` per diagnostic. Our push notification omits it. Also, notification entries have only `uri` (no `filePath`), while the `get_diagnostics` tool response has both.

3. **`open_diff` / `close_diff` responses:** VS Code does NOT include an `error` field. Errors use MCP's `{isError: true}` wrapper. Our server includes `error: null` in success responses — harmless extra field under superset comparison, but worth documenting.

#### Snapshot Refresh Process

Documented in `Snapshots/README.md`. Manual process: read `extension.js`, locate `registerTool()` calls and notification broadcasts, extract JSON structures.

**Result:** 112 tests pass (no regressions). Response snapshots are reference files not yet in active tests. When wired in, they will surface the `get_vscode_info` gap.

---

### Proxy-Based Protocol Compatibility Testing

**Author:** Ripley (Lead)  
**Date:** 2026-03-07  
**Status:** Proposed  
**Supersedes:** "Protocol Compatibility Test Architecture" (golden snapshot approach)

#### Decision

Build a named pipe proxy tool that intercepts real Copilot CLI ↔ VS Code traffic. Use captured traffic as the ground truth for compatibility testing, not golden snapshots derived from source code.

#### Architecture

A C# console app (`net10.0`, `tools/PipeProxy/`) that sits between Copilot CLI and VS Code Insiders on the named pipe, logging traffic in NDJSON format.

**Why C#:**
- Reuses `ReadHttpRequestAsync`, `ReadChunkedBodyAsync`, `WriteHttpResponseAsync` from `McpPipeServer.cs` — production-tested, handles all HTTP-on-pipe edge cases
- Same named pipe APIs and lock file format code already exist
- No new runtime dependency; team knows C#

**How It Works:**
1. Scan `~/.copilot/ide/*.lock` to find VS Code Insiders
2. Create proxy named pipe and inject proxy lock file (hide VS Code's lock temporarily to prevent CLI race)
3. Relay all traffic (POST tool calls, GET SSE streams, DELETE disconnect) between CLI and VS Code
4. Log each request/response/event as NDJSON with timestamps, directions, HTTP metadata, parsed JSON bodies

**Log Format:**
```jsonl
{"ts":"2026-03-07T12:34:56.789Z","seq":1,"dir":"cli_to_vscode","type":"request","http":{...},"body":{...}}
```

#### Testing Strategy

**Phase 1 (near term):** Build capture tool, run with Copilot CLI manually, inspect NDJSON output for protocol discovery.

**Phase 2:** Write replay-based comparison tests:
- Read captured NDJSON traffic file
- Spin up our `McpPipeServer` with mocked `IVsServiceRpc`
- Send same CLI requests to our server
- Compare responses structurally (same fields, same types, same nesting) — values can differ

**Phase 3 (future):** Live dual-target comparison mode (optional, requires VS Code + our mock running simultaneously).

#### Phased Rollout

| Phase | Task | Effort | Depends |
|-------|------|--------|---------|
| 1 | Build PipeProxy tool (capture mode) | ~6h | — |
| 2 | First capture with VS Code Insiders | ~1h | Phase 1 |
| 3 | TrafficParser + SchemaComparer utilities | ~3h | Phase 2 |
| 4 | Replay comparison tests | ~4h | Phase 3 |
| 5 | (Future) Live `--compare` mode | ~4h | Phase 4 |

**Total to first capture:** ~6h  
**Total to automated replay tests:** ~14h

#### Key Design Decisions

1. **C# over Node.js/PowerShell** — reuse production-tested HTTP-on-pipe code, not reimplementing
2. **Standalone tool, not test fixture** — proxy runs interactively with a human; replay tests run headless in CI
3. **NDJSON format** — queryable, `grep`-able, diffable, parseable with `System.Text.Json`
4. **Committed captures** — ground truth stored as test data; re-captured on significant VS Code updates
5. **Structural comparison** — schema shape (field names, types, nesting), not values

#### What This Replaces

Supersedes the golden snapshot approach. Existing `Snapshots/*.json` files kept temporarily; replaced when replay tests are ready.

---

### Remove UI Thread Requirement from Dispose

**Author:** Hicks (Extension Dev)  
**Date:** 2026-03-07  
**Status:** Implemented

## Context

`Dispose(bool disposing)` called `ThreadHelper.ThrowIfNotOnUIThread()` and re-fetched `IVsMonitorSelection` via `GetGlobalService`. This is fragile: Dispose can be called during shutdown when the UI thread may not be available, and `GetGlobalService` may return null for a service that was alive during `InitializeAsync`.

## Decision

1. Cache `IVsMonitorSelection` as `_monitorSelection` field, captured in `InitializeAsync` where it's already fetched.
2. Remove `ThreadHelper.ThrowIfNotOnUIThread()` from `Dispose` — the cached reference eliminates the need for a service lookup.
3. Use the cached `_monitorSelection` for `UnadviseSelectionEvents` in Dispose.
4. Null out both `_monitorSelection` and `_selectionMonitorCookie` (set to 0) after unadvising for clean teardown.

## Rationale

- **Dispose shouldn't throw.** `ThrowIfNotOnUIThread()` in a disposal path violates the principle that cleanup should be resilient.
- **Caching avoids stale-service risk.** During VS shutdown, `GetGlobalService` may return null even for services that were healthy at init time.
- **Zeroing the cookie** prevents double-unadvise if Dispose is called more than once.

## Files Changed

- `src/CopilotCliIde/CopilotCliIdePackage.cs` — field added, InitializeAsync updated, Dispose refactored

## Verification

- Server builds clean (0 warnings)
- 109 tests pass

---

### Protocol Compatibility Test Architecture

**Author:** Ripley (Lead)  
**Date:** 2026-03-07  
**Status:** Implemented (Phase 1)  
**Requested by:** Sebastien

## Problem

We reverse-engineered the VS Code Copilot Chat ↔ Copilot CLI protocol via a named pipe proxy. The findings in `decisions.md` document exact tool schemas, notification formats, HTTP headers, SSE transport, and lock file structure. But this knowledge lives in prose — when VS Code Insiders updates its extension, we have no automated way to detect if we've drifted out of compatibility.

## Decision

**Golden snapshot tests in the existing test project, with a full MCP handshake integration test as the centerpiece.**

No new project. No live VS Code dependency. No extension.js parsing.

## Architecture

### Approach: Two-Layer Testing

**Layer 1 — Golden Snapshot Tests** (`ProtocolCompatibilityTests.cs`)  
Compare our server's MCP outputs against golden JSON snapshots captured from real VS Code ↔ Copilot CLI traffic. These are pure, fast, deterministic unit tests that run in CI.

**Layer 2 — MCP Handshake Integration Test** (`McpHandshakeTests.cs`)  
Spin up our actual `McpPipeServer` on a test named pipe with a mocked `IVsServiceRpc`, connect as a client (simulating Copilot CLI), and perform the full protocol exchange: `initialize` → `tools/list` → tool calls → SSE subscription → notification push. This catches integration bugs that per-component tests miss.

### Why Not Other Approaches

| Approach | Rejected Because |
|---|---|
| **Live proxy test (VS Code + CLI running)** | CI nightmare. Requires VS Code Insiders + Copilot CLI + authentication. Flaky. Slow. Can't control update timing. |
| **Extract schemas from extension.js** | Minified JS with obfuscated names. Offsets change every build. Requires VS Code installed. Fragile. |
| **Separate test project** | Our tests need `[InternalsVisibleTo]` access to `McpPipeServer`'s internal HTTP helpers. Same target framework (`net10.0`). Same dependencies. No benefit from a new project. |

Golden snapshots give us 90% of the value at 10% of the complexity. The handshake test gives us integration confidence that no amount of unit tests provides.

## What Exactly To Test

### 1. MCP `initialize` Response Shape

The `initialize` response from our server must match VS Code's:
- `serverInfo.name` = `"vscode-copilot-cli"` (we already do this)
- `capabilities.tools.listChanged` = `true`
- No `taskSupport` in response (or `forbidden` — both acceptable)

### 2. `tools/list` Response Schema

The `tools/list` response defines the contract. Every tool name, description, and input schema must match what VS Code registers.

Golden file: `Snapshots/tools-list.json` — the full `tools/list` response from a real VS Code capture.

### 3. Tool Response Shapes (Per-Tool)

For each tool, call it with representative inputs (via mocked RPC) and validate the response JSON matches the golden shape.

**Important:** Schema comparison, not value comparison. We check that the same keys exist with the same value types and nesting depth. We do NOT check that `filePath` equals a specific string.

### 4. Notification Formats

Push `selection_changed` and `diagnostics_changed` through the full stack and verify the SSE event format matches golden snapshots.

### 5. HTTP Protocol Details

Already partially covered by `HttpParsingTests`, but the handshake test adds:
- Auth header validation (`Authorization: Nonce {uuid}`)
- `Mcp-Session-Id` header on responses
- `Content-Type: text/event-stream` on SSE responses
- 202 Accepted for notifications (not 200)
- `Transfer-Encoding: chunked` on SSE stream

### 6. Lock File Format

Construct a lock file as our extension does, parse as JSON, and verify 8 required fields with correct types.

## File Structure

```
src/CopilotCliIde.Server.Tests/
  Snapshots/
    tools-list.json                    # Captured tools/list response from VS Code
    get-vscode-info-response.json      # Tool response shapes (structure only)
    get-selection-response.json
    get-diagnostics-response.json
    open-diff-response.json
    close-diff-response.json
    selection-changed-notification.json
    diagnostics-changed-notification.json
    lock-file.json
    README.md                          # How snapshots were captured, how to refresh
  ProtocolCompatibilityTests.cs        # Golden snapshot comparison (Layer 1)
  McpHandshakeTests.cs                 # Full pipe integration test (Layer 2)
```

Snapshots are committed to the repo. They're small JSON files (< 1KB each). They change infrequently — only when VS Code updates its protocol.

## Reference Data Management

### Initial Capture

We have the proxy captures from the reverse-engineering sessions. These become the initial golden snapshots.

### Refresh Process

**Manual, triggered by VS Code Insiders updates.**

1. **Detection:** Check `~/.vscode-insiders/extensions/github.copilot-chat-*/package.json` for version changes.
2. **Capture:** Run the named pipe proxy tool between VS Code Insiders and Copilot CLI. Capture one full session.
3. **Update:** Replace golden JSON files. Run `dotnet test` to see what changed. Review diffs. Commit.
4. **Frequency:** Monthly, or when a major Copilot CLI / VS Code Insiders release is announced.

### Refresh Script

A PowerShell script (`scripts/refresh-snapshots.ps1`) that checks VS Code Insiders, reads extension version, launches proxy, captures session, and updates snapshots.

## Phased Rollout

### Phase 1: Golden Schema Infrastructure + `tools/list` Test ✅ COMPLETE

**Effort:** ~2 hours  
**Value:** High — catches tool registration regressions immediately

- ✅ Created `Snapshots/` directory with 8 golden JSON files
- ✅ Wrote `JsonSchemaComparer` helper (structural comparison utility)
- ✅ Wrote `ProtocolCompatibilityTests` with 3 tests
- ✅ Added RpcClient internal constructor test seam

This phase builds the infrastructure that all subsequent tests depend on.

### Phase 2: MCP Handshake Integration Test (NEXT)

**Effort:** ~4 hours  
**Value:** Very high — catches integration bugs, validates full wire protocol

- Write `McpHandshakeTests` that spins up `McpPipeServer` on a test pipe
- Mock `IVsServiceRpc` via NSubstitute
- Test: `initialize` → `tools/list` → `get_vscode_info` call → response validation
- Test: SSE connect → `selection_changed` push → verify SSE event arrives

### Phase 3: Per-Tool Golden Response Tests (DEFERRED)

**Effort:** ~2 hours  
**Value:** Medium — incremental over Phase 2

- Add golden snapshot files for each tool's response shape
- Write parameterized test that calls each tool and compares against snapshot

### Phase 4: Refresh Script (DEFERRED)

**Effort:** ~3 hours  
**Value:** Medium-long-term

- PowerShell script for proxy capture → snapshot update
- `snapshots/VERSION` tracking
- `snapshots/README.md` with instructions

## Test Seam: RpcClient Internal Constructor

`RpcClient.VsServices` is set during `ConnectAsync` (which connects a real pipe). For the handshake test, we need to inject a mock `IVsServiceRpc` without actually connecting the RPC pipe.

**Solution:** Added `internal RpcClient(IVsServiceRpc vsServices)` constructor. It's already `[InternalsVisibleTo]` for the test project. One line of code, no architectural impact.

## Impact on Existing Tests

None. All new files. Existing 109 tests untouched. The `ProtocolCompatibilityTests` (3 new tests) are additive. Phase 2 integration test is also additive. Current test count: **112 passing**.

## Assignments

- **Phase 1** ✅ Hudson — golden infrastructure complete
- **Phase 2** (Next) — Bishop — handshake integration test
- **Phase 3** (Future) — Hudson — per-tool golden tests
- **Phase 4** (Future) — Hicks or Bishop — refresh script
- **RpcClient seam** ✅ Bishop — constructor added

---

## Governance

- All meaningful changes require team consensus
- Document architectural decisions here
- Keep history focused on work, decisions focused on direction
