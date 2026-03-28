# Squad Decisions

## Review Findings — 2026-03-10

### Architecture & Code Quality Review (Ripley)

**Author:** Ripley (Lead)  
**Date:** 2026-03-10  
**Type:** Review findings (no code changes made)

#### HIGH Impact

**H1. Threading hazards in DebouncePusher** (`src/CopilotCliIde/DebouncePusher.cs`)
- `_timer` and `_lastKey` accessed from UI thread and timer callback with no synchronization. `_lastKey` is plain `string?` — reads/writes may not have guaranteed happens-before relationship.
- **Recommendation:** Add `lock` around timer access and `_lastKey`, or make `_lastKey` volatile and use `Interlocked` for timer management.

**H2. ServerProcessManager uses Task.Delay(200) as readiness signal** (`ServerProcessManager.cs:35`)
- `await Task.Delay(200)` only check for MCP server readiness. On slow machines, server may not have created named pipe yet.
- **Recommendation:** Poll for pipe existence or have server signal readiness (e.g., write to stdout).

**H3. VsServiceRpc diff cleanup races** (`VsServiceRpc.cs:37-43, 119-131, 343-365`)
- Three concurrent paths (`OpenDiffAsync`, `CloseDiffByTabNameAsync`, `FrameCloseNotify`) race on same diff state. `ConcurrentDictionary` protects individual ops, not find-then-act sequence.
- **Recommendation:** Use regular `Dictionary` + `lock`, or restructure so each diff lifecycle uses single gate.

**H4. Silent catch blocks hurt debuggability** (throughout extension and server)
- Most are `catch { /* Ignore */ }` with zero logging. When something breaks in production, no trail.
- **Recommendation:** Log to OutputLogger (extension) and stderr (server) at minimum.

#### MEDIUM Impact

**M1. IdeDiscovery.WriteLockFileAsync is sync-over-async** (`IdeDiscovery.cs:15-38`)
- Signature says `Async` but uses `Directory.CreateDirectory`, `File.WriteAllText`.
- **Recommendation:** Use `File.WriteAllTextAsync` or remove `Async` suffix.

**M2. DiagnosticTracker.ComputeDiagnosticsKey incomplete hash** (`DiagnosticTracker.cs:242-257`)
- Hashes `Message`, `Severity`, `Start.Line`, `Start.Character` but omits `End` and `Code` → false dedup.
- **Recommendation:** Include `End.Line`, `End.Character`, `Code` in hash.

**M3. DiagnosticTracker unnecessary UI-thread round-trip** (`DiagnosticTracker.cs:213-239`)
- `OnDebounceElapsed` switches UI thread → back, but `CollectGrouped` only reads thread-safe snapshots.
- **Recommendation:** Verify whether `ITableEntriesSnapshot` APIs require UI thread; if not, remove switch.

**M4. Missing `source` field in DiagnosticItem** (`Contracts.cs:115-121`)
- VS Code's `get_diagnostics` includes `source` (e.g., "typescript", "eslint"). Ours lacks it.
- **Recommendation:** Add `Source` property and populate from Error List table data.

**M5. No MSBuild Clean target for published server** (`CopilotCliIde.csproj:59-77`)
- `PublishServerBeforeBuild` publishes to `$(OutputPath)\CopilotCliIde.Server\` but `msbuild /t:Clean` leaves artifacts.
- **Recommendation:** Add `CleanServerArtifacts` target with `BeforeTargets="Clean"`.

#### LOW Impact

- **L1:** VsServiceRpc is 398-line god class — extract diff management to `DiffManager`.
- **L2:** McpPipeServer is 573-line god class — extract HTTP parsing and SSE broadcast.
- **L3:** DiagnosticRange duplicates SelectionRange — consider shared `Range` type.
- **L4:** No unit tests for PathUtils — pure static functions, trivial to test.
- **L5:** No unit tests for DebouncePusher — 36-line class with independent testable logic.
- **L6:** Header byte-by-byte reading in McpPipeServer — inefficient but low priority for local pipes.

---

### VS Extension Improvement Scan (Hicks)

**Author:** Hicks (Extension Dev)  
**Date:** 2026-07-19  
**Scope:** All 11 source files in `src/CopilotCliIde/`

#### HIGH Impact

**HIGH-1. Active diff cleanup on connection teardown** (`VsServiceRpc.cs`, `CopilotCliIdePackage.cs`)
- `VsServiceRpc` owns `_activeDiffs` with pending `TaskCompletionSource` instances. When `StopConnection()` fires, RPC is disposed but `VsServiceRpc` is not — it was created by `JsonRpc.Attach` and has no disposal path.
- **Impact:** Pending diffs orphaned. Their TCS never completes (MCP server hangs). InfoBars remain visible. Temp files never deleted.
- **Suggestion:** Make `VsServiceRpc` implement `IDisposable` and have package call during `StopConnection`, or expose `CleanupAllDiffs()` method.

**HIGH-2. DebouncePusher.Schedule() race condition** (`DebouncePusher.cs:9-14`)
- TOCTOU: two threads calling `Schedule()` simultaneously can both see `_timer == null`, both create timers. One leaks.
- **Suggestion:** Use `Interlocked.CompareExchange` or simple lock. Or create timer once in constructor with `Timeout.Infinite` and only use `Change()`.

#### MEDIUM Impact

- **M1:** CancellationTokenSource leak in OpenDiffAsync (create at L55, catch at L112 doesn't dispose).
- **M2:** Stale lock file cleanup could delete file being written (TOCTOU on parse failure).
- **M3:** GetSelectionAsync uses DTE while push uses native APIs (inconsistent results possible).
- **M4:** ServerProcessManager Task.Delay(200) fragile for slow machines/CI.

#### LOW Impact

- **L1:** SelectionTracker.UntrackView missing thread guard.
- **L2:** Package.Dispose calls Reset on already-disposed SelectionTracker.
- **L3:** IdeDiscovery async methods are synchronous (misleading Async suffix).
- **L4:** InitializeAsync logs only ex.Message, not stack trace.
- **L5:** GetSelectionAsync swallows exception silently.
- **L6:** Temp diff files orphaned on exception in OpenDiffAsync.
- **L7:** PID reuse in stale lock file cleanup (only checks if PID exists).
- **L8:** VsServices singleton properties not thread-safe (should mark volatile).

---

### Server & Shared Review (Bishop)

**Author:** Bishop (Server Dev)  
**Date:** 2026-03-10  
**Type:** Review (no code changes)

Reviewed against VS Code wire captures (vscode-0.38, vscode-0.39, vscode-insiders-0.39) and our vs-1.0.8 capture. 195 tests passing.

#### HIGH Impact

**H1. Missing `cache-control` header on POST SSE responses** (`McpPipeServer.cs:376-386`)
- GET SSE path adds `cache-control: no-cache, no-transform` (line 209).
- POST SSE responses use `WriteHttpResponseAsync` which does NOT add it.
- VS Code includes `cache-control` on ALL SSE responses.
- **Fix:** Add `cache-control` header to `WriteHttpResponseAsync` when `contentType == "text/event-stream"`.

**H2. `postCts.Token` used for success response writes — timeout race** (`McpPipeServer.cs:193-200`)
- After `HandlePostRequestAsync` succeeds, response write uses `postCts.Token`.
- If 30s timeout fires between HandlePost returning and response write, `OperationCanceledException` is thrown even though valid response was produced.
- **Fix:** Use parent `ct` for all response writes (already done for error paths).

**H3. Byte-by-byte header parsing in `ReadHttpRequestAsync`** (`McpPipeServer.cs:260-268`)
- Reads headers one byte at a time (new ReadAsync per byte), calls `sb.ToString(sb.Length - 4, 4)` on every byte.
- For ~400-byte headers: 400 async reads + 400 allocations.
- **Fix:** Read into 4096-byte buffer, scan for `\r\n\r\n` in-memory.

**H4. Per-client `fullChunk` allocation in `PushNotificationAsync`** (`McpPipeServer.cs:529`)
- `fullChunk` is identical for every client but allocated inside `foreach` loop.
- **Fix:** Compute once before loop.

#### MEDIUM Impact

- **M1:** `SseClient.WaitAsync` CancellationTokenRegistration leak (never disposed).
- **M2:** `MemoryStream.ToArray()` unnecessary copy (use `TryGetBuffer()` or `GetBuffer()`).
- **M3:** Fire-and-forget event handlers in Program.cs (unobserved task exceptions).
- **M4:** New MCP transport per pipe connection (vs VS Code's session-shared approach).

#### LOW Impact

- **L1:** Silent tool registration failures (`catch { /* Ignore */ }`).
- **L2:** Mutable DTO classes vs records (not idiomatic for .NET 10).
- **L3:** Inconsistent tool return patterns (some return RPC DTOs, some anonymous objects).
- **L4:** `ReadChunkedBodyAsync` fragile trailer handling (assumes 2-byte CRLF).

---

### Test Coverage Review (Hudson)

**Author:** Hudson (Tester)  
**Date:** 2026-03-10  
**Status:** Review (no code changes)

195 tests passing across 15 test files. **Server project** has strong coverage. **VS extension (net472) and Shared project have zero direct test coverage.**

#### Coverage Gaps — HIGH Priority

**No Test Project for VS Extension (CopilotCliIde)**
- 11 source files, ~1,400 LOC, zero test coverage.
- Critical untested: OpenDiff blocking/timeout/cleanup, CloseDiff races, GetVsInfo assembly, ReadFile pagination, info bar events.
- **Recommendation:** Create `CopilotCliIde.Tests` (net472/net8.0 dual-target) for testable classes (PathUtils, DebouncePusher, IdeDiscovery).

**CopilotCliIde.Shared (Contracts.cs) Has No Direct Tests**
- DTOs tested indirectly through `DtoSerializationTests.cs` (camelCase round-trip).
- Tools use anonymous objects with explicit snake_case.
- **Gap:** No test validates both paths produce compatible output for same scenario.

#### Quality Issues

- **No-op assertions** (2 instances): `Assert.True(comparisons >= 0, ...)` always true. Should be `comparisons > 0`.
- **Duplicate test:** ToolOutputSchemaTests has two identical tests — remove one.
- **Weak assertions:** UpdateSessionNameToolTests uses `Assert.Contains("\"success\":true", json)` — string match, not JSON parse.
- **Overpromise assertion:** McpPipeServerTests.PushNotificationAsync title claims JSON-RPC format validation but only checks `Assert.NotNull(task)`.

#### Missing Test Categories (15 action items, prioritized)

| Priority | Effort | Item |
|----------|--------|------|
| 🔴 HIGH | Low | Fix no-op assertions (`>= 0` → `> 0`) |
| 🔴 HIGH | Low | Remove duplicate ToolOutputSchemaTests |
| 🔴 HIGH | Low | Add PathUtils unit tests (~8 tests) |
| 🔴 HIGH | Low | Add DebouncePusher unit tests (~6 tests) |
| 🔴 HIGH | Med | Extract shared capture discovery helper |
| 🔴 HIGH | Med | Add IdeDiscovery unit tests (~10 tests) |
| 🟡 MED | Low | Strengthen UpdateSessionNameTool assertions |
| 🟡 MED | Low | Fix McpPipeServerTests assertion to validate content |
| 🟡 MED | Med | Add CrossCaptureConsistencyTests for field types |
| 🟡 MED | Med | Add TrafficParser unit tests |
| 🟡 MED | Med | Add malformed HTTP request tests |
| 🟡 MED | High | Create CopilotCliIde.Tests project |
| 🟢 LOW | Low | Add test traits/categories |
| 🟢 LOW | Med | Split TrafficReplayTests.cs |
| 🟢 LOW | High | Investigate VSSDK.TestFramework |

**Impact:** Items 1-6 close real coverage gaps. Items 7-11 improve test quality. Items 12-15 are structural long-term improvements.

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

### Decision: Cross-Session ID Correlation in Multi-Session Captures

**Author:** Hudson (Tester)  
**Date:** 2026-03-09

## Context

During P1+P2 test implementation, discovered that `GetAllToolCallResponses` can match requests from failed sessions (HTTP 400) to responses from later successful sessions when they share the same JSON-RPC ID.

Example: vscode-0.38 sessions 2-4 all fail with 400 errors. A close_diff request with id=3 from session 2 (seq=65) matches the response with id=3 from session 5 (seq=105), because both have the same ID and the parser finds the first response after the request's sequence number.

## Impact

- `GetAllToolCallResponses` may return duplicate references to the same response when multiple failed sessions had the same request IDs.
- Response count can exceed actual successful sessions.
- Test D4 accounts for this by asserting `responses.Count <= requestCount` rather than exact equality.

## Decision

This is a **known limitation**, not a bug. The parser's sequence-scoped ID matching is the correct design for the common case (single session or all sessions succeeding). True session-aware correlation would require detecting session boundaries in the request stream, which adds complexity with minimal benefit for test coverage.

## Action Required

None — this is informational. If future captures expose actual test failures from this behavior, consider adding session boundary detection to `GetAllToolCallResponses`.

---

### Decision: Error List Table API for Diagnostics

**Author:** Bishop (Server Dev)  
**Date:** 2026-03-10

## Context

The `get_diagnostics` tool and `diagnostics_changed` push notification were missing error codes (e.g., CS1585, IDE1007) because the DTE `ErrorItem` interface doesn't expose them. VS Code returns error codes on every diagnostic.

## Decision

`ErrorListReader` now prefers the `IErrorList` / `IWpfTableControl2` table control API (via `SVsErrorList` service) which exposes `StandardTableKeyNames.ErrorCode`. Falls back to DTE `ErrorItems` when the table control is unavailable.

## End Position Limitation

VS's Error List surface (both DTE and Table Manager) only stores start-line/column. End-of-diagnostic span is not exposed. VS Code gets accurate spans because it accesses Roslyn's diagnostics directly. Our extension sets `end = start`. This is a known VS limitation and cannot be resolved without bypassing the Error List entirely (e.g., hosting a Roslyn workspace directly, which is a major scope expansion).

## Impact

- Both `get_diagnostics` RPC tool and `diagnostics_changed` push now return error codes when table control is available
- No behavioral change when table control unavailable (DTE fallback)
- Server tests unaffected (173 passing)

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

---

## Capture Analysis Session: 2026-03-07T20-07-47Z

### Capture Analysis: Code Changes

**Author:** Bishop (Server Dev)  
**Date:** 2026-03-08  
**Sources:** `vs-1.0.7.ndjson`, `vscode-0.38.ndjson`, `vscode-insiders-0.39.ndjson`  

#### Server Code Changes Required (COMPLETED)

**1. `get_selection` tool response: Always include `text` field**

- **Evidence:** VS 1.0.7 omits `text` when empty; VS Code 0.39 includes `"text": ""` in all cases
- **Fix Applied (Bishop):** `GetSelectionTool.cs` now transforms result to always set `text = result.Text ?? ""`
- **Status:** ✅ MERGED

**2. `diagnostics_changed` notification: Include `source` field**

- **Evidence:** Our notifications omit `source`; tool responses include it when available
- **Fix Applied (Bishop):** `PushDiagnosticsChangedAsync` now includes `source = d.Source` in diagnostic anonymous objects
- **Status:** ✅ MERGED

#### Extension-Side Data Gaps (non-code, lower priority)

**3. `code` field in diagnostics always null** — Extension doesn't extract error code column from Error List  
**4. POST SSE responses lack `cache-control` header** — Minor cosmetic difference

**Outcome:** 2 critical fixes deployed. Build clean, 140+ tests pass.

---

### Protocol Documentation: Accuracy & Gaps

**Author:** Ripley (Lead)  
**Date:** 2026-03-08  
**Captures analyzed:** `vs-1.0.7.ndjson`, `vscode-0.38.ndjson`, `vscode-insiders-0.39.ndjson`  
**Document analyzed:** `doc/protocol.md`  

#### Accurate Sections ✅

- Protocol version, server name, tool names/descriptions, tool parameter schemas, `taskSupport: forbidden` execution mode
- `selection_changed` notification format

---

### Log Format Consistency & Position Accuracy in VsServiceRpc

**Author:** Hicks (Extension Dev)
**Date:** 2026-07-19
**Status:** Implemented
**Scope:** `src/CopilotCliIde/VsServiceRpc.cs`

#### Context

The Output pane showed two inconsistencies when comparing push event logs (from SelectionTracker/DiagnosticTracker) with tool invocation logs (from VsServiceRpc):

1. **Separator ambiguity:** Push events used `:` after the event name (`Push selection_changed: file L1:5 → L1:20`), but tool logs used `→` (`Tool get_selection → file L1:5 → L1:20`). The arrow was overloaded — it served as both the name/details separator AND the position range separator, making logs hard to scan.

2. **Column mismatch:** For the same cursor position, push events and tool results reported different column numbers. Push events (SelectionTracker) compute character offset via buffer positions (`selection.Start.Position - startLine.Start.Position`), counting each character including tabs as 1. Tool results (VsServiceRpc) used `DisplayColumn`, which expands tabs to their visual width (typically 4), inflating column numbers by 3 per tab.

#### Decision

**Format: Use `:` as the universal name/details separator**

All log lines now follow the pattern `{Type} {name}: {details}`:
- `Push selection_changed: VsServiceRpc.cs L238:20 → L238:37`
- `Tool get_selection: VsServiceRpc.cs L238:20 → L238:37`

The `→` arrow is reserved exclusively for position ranges (start → end).

**Position: Use `LineCharOffset` instead of `DisplayColumn`**

Changed `sel.TopPoint.DisplayColumn - 1` to `sel.TopPoint.LineCharOffset - 1` in `GetSelectionAsync`. Both are 1-based DTE properties, but:

| Property | Tab handling | Use case |
|---|---|---|
| `DisplayColumn` | Tab = N visual columns (tab width) | Cursor rendering |
| `LineCharOffset` | Tab = 1 character | Character-based offset (matches buffer math) |

`LineCharOffset - 1` produces the same 0-based offset as SelectionTracker's `position - lineStart`, ensuring push events and tool results agree.

#### Consequences

- Output pane logs are now visually consistent and unambiguous across push events and tool calls.
- Column numbers from `get_selection` tool match push event column numbers for the same cursor position, even in files using tabs.
- Copilot CLI receives consistent position data regardless of whether it reads from a push event or polls via the `get_selection` tool.
- File URI convention (`file:///c%3A/...`)
- Capabilities (`tools.listChanged: true`)
- CLI request methods and SSE wire format
- Custom HTTP headers

#### Documentation Updates Applied (7 edits)

**P0 — `get_selection` null response**
- **Gap:** Doc doesn't mention tool can return `null` when no editor active
- **Updated:** §3 get_selection — "When no editor is active and no cached selection exists, the tool returns `null`"
- **Status:** ✅ MERGED

**P1 — HTTP response codes (200 vs 202)**
- **Gap:** Doc mentions 202 Accepted but doesn't clearly distinguish when
- **Updated:** §2 Transport — `200 OK` with SSE body for requests with results; `202 Accepted` for notifications
- **Status:** ✅ MERGED

**P1 — serverInfo version field**
- **Gap:** Doc shows `"1.0.0"`; VS Code captures show `"0.0.1"`
- **Updated:** §3 Server Info — Note: VS Code currently sends `"0.0.1"`, implementation-defined
- **Status:** ✅ MERGED

**P1 — serverInfo `title` field**
- **Gap:** All captures include `"title": "VS Code Copilot CLI"` but doc omits it
- **Updated:** §3 Server Info — Document `title` as optional field
- **Status:** ✅ MERGED

**P1 — HTTP Transfer-Encoding**
- **Gap:** Doc says `Content-Length`, captures show `Transfer-Encoding: chunked`
- **Updated:** §2 HTTP Headers — Either `Content-Length` or `Transfer-Encoding: chunked`
- **Status:** ✅ MERGED

**P2 — `source` field in diagnostics**
- **Gap:** Doc example shows `source: "typescript"` but captures show it's typically null
- **Updated:** §3 get_diagnostics — Note `source` may not be present depending on language service
- **Status:** ✅ MERGED

**P2 — Session ID generation**
- **Gap:** Doc says server generates; VS Code appears to use client's session ID
- **Updated:** §2 Session ID — Note format and origin are implementation-defined
- **Status:** ✅ MERGED

**Outcome:** All P0/P1/P2 gaps addressed. Documentation now matches capture reality.

---

### Test Gap Analysis: Coverage Review

**Author:** Hudson (Tester)  
**Date:** 2026-03-07  

#### Test Gap Analysis Summary

- **131 tests currently passing** across 11 test files
- **3 capture files analyzed** (119 total entries): vs-1.0.7 (42), vscode-0.38 (38), vscode-insiders-0.39 (39)
- **28 proposed tests** across 6 categories (A–F)

#### Key Findings

1. **Schema drift detected:** `get_diagnostics.uri` type changed from `"string"` (v0.38/v0.39) to `["string","null"]` (v1.0.7) — no test catches this
2. **Capability evolution:** v1.0.7 adds `logging` capability absent in earlier versions
3. **4 of 7 tools never called:** `get_vscode_info`, `open_diff`, `close_diff`, `read_file` — response schemas unvalidated
4. **Duplicate tools/list calls:** Every capture has exactly 2 — idempotency not tested
5. **get_selection schema variance:** v1.0.7 omits `text`; v0.39 includes it
6. **Request-response ID correlation:** Parser uses fallback correlation for truncated HTTP frames — path not validated
7. **Empty text notifications dominate:** 10+ per capture with `isEmpty: true` — edge case not specifically tested
8. **Test 7 validates tool names only** — doesn't check initialize response shape, input schemas, or auth rejection

#### Top 5 Priority Tests (COMPLETED)

| Test | Category | What | Why | Status |
|------|----------|------|-----|--------|
| **A1** | Schema Consistency | `AllCaptures_ToolInputSchemas_AreConsistentOrDocumented` | Catches real schema drift (uri type) | ✅ MERGED |
| **B1** | Tool Responses | `VsCodeGetSelectionResponse_HasExpectedStructure` | Called in all captures, never validated | ✅ MERGED |
| **B2** | Tool Responses | `VsCodeUpdateSessionNameResponse_HasExpectedStructure` | Called in all captures, never validated | ✅ MERGED |
| **E1** | Integration | `OurServer_InitializeResponse_MatchesCaptureStructure` | Test 7 only validates names | ✅ MERGED |
| **C1** | Correlation | `AllCaptures_RequestResponseIds_AreCorrelated` | Validates JSON-RPC pairing | ✅ MERGED |

#### Remaining Tests (23 proposed)

**Medium priority (B3–B6, C2–C3, D1–D5):** Edge cases, consistency checks, parser coverage  
**Nice-to-have (E2–E5, F1–F4):** Sanity checks, robustness, minor gaps  

**Recommendation:** Add remaining tests in future sprints. All P0 gaps now covered.

#### Missing Capture Coverage

Tools never called in any capture:
- `get_vscode_info`, `open_diff`, `close_diff`, `read_file`

**Action:** Create new captures exercising these tools (future sprint).

**Outcome:** 5 critical tests added. Current count: 140 passing (131 existing + 5 new + 4 prior work). All P0 gaps covered.

---


---

# Single Source of Truth for Capture File Discovery

**Date:** 2026-03-08  
**Decided by:** Hudson (Tester)  
**Status:** Implemented

## Context

`TrafficReplayTests.cs` had 4 separate locations calling `Directory.GetFiles(FindCapturesDir(), "*.ndjson")`:
1. Line 25 in `CaptureFiles()` TheoryData method
2. Line 349 in `AllCaptures_ToolInputSchemas_AreConsistent()` [Fact]
3. Line 822 in `AllCaptures_RequestResponseIds_AreCorrelated()` [Fact]
4. Line 918 in `OurToolsList_MatchesVsCodeToolNames()` [Fact]

This duplication meant any changes to capture file discovery logic (filters, ordering, exclusions) would require updates in 4 places.

## Decision

Extracted a single private helper method `GetCaptureFiles()` that returns `string[]` from `Directory.GetFiles(FindCapturesDir(), "*.ndjson")`. All 4 locations now call this helper instead of inlining the file discovery logic.

```csharp
/// <summary>
/// Returns all .ndjson capture files from the Captures/ directory.
/// </summary>
private static string[] GetCaptureFiles()
{
	return Directory.GetFiles(FindCapturesDir(), "*.ndjson");
}
```

## Rationale

- **Single source of truth:** Capture file discovery logic lives in exactly one place
- **Future-proof:** Adding exclusions (e.g., skip broken captures, filter by name pattern) requires one change, not four
- **No behavioral change:** Tests continue to pass exactly as before (142/142)
- **Minimal footprint:** Single helper method, no new abstractions or complexity

## Verification

- Build: `dotnet build src\CopilotCliIde.Server.Tests\CopilotCliIde.Server.Tests.csproj` ✅
- Tests: `dotnet test src\CopilotCliIde.Server.Tests\CopilotCliIde.Server.Tests.csproj --no-build` — 142/142 pass ✅


---

# Hudson — Refactor + Round 2 Priority Tests

**Date:** 2026-03-08
**Author:** Hudson (Tester)

## Changes Made

### Refactor: FindAllCaptureFiles removed
- `FindAllCaptureFiles()` eliminated. Cross-capture `[Fact]` tests inline `Directory.GetFiles(FindCapturesDir(), "*.ndjson")`.
- All per-capture tests use `[Theory] [MemberData(nameof(CaptureFiles))]` with `TheoryData<string>`.
- `FindCapturesDir()` retained as shared infrastructure.

### Test 6 Extended: diagnostic `source`/`code` validation
- `VsCodeDiagnosticsChanged_HasExpectedStructure` now validates `source` (string|null) and `code` (string|null) fields when present on diagnostic items.
- Cross-capture safe: checks type only when field exists.

### Test E2: get_selection integration
- `OurServer_GetSelectionResponse_HasExpectedStructure` — full pipe roundtrip with mocked `IVsServiceRpc`.
- Validates all 5 top-level fields and selection sub-fields with realistic mock data.

### Test E3: Auth rejection
- `OurServer_InvalidNonce_Returns401` — verifies wrong nonce gets HTTP 401.
- Tests the sole security boundary of the MCP pipe server.

### Test B1 Fix
- Pre-existing failure on vs-1.0.7: `current: false` responses omit `filePath`/`fileUrl`/`selection`.
- Added early return for `current: false` case.

## Impact
- Test count: 140 → 142
- All 142 passing
- No production code changes


---

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


---

# Protocol Doc Round 2 Analysis — No Doc Changes Needed

**Author:** Ripley (Lead)
**Date:** 2026-03-08
**Priority:** Informational

## Verdict

All 7 Round 1 protocol.md fixes are **confirmed accurate** against the new vs-1.0.7 capture. No protocol documentation changes required.

## Server Compliance Issues Found (Not Doc Issues)

These are implementation gaps in our server vs the protocol as documented:

### 1. Session ID Rotation (Medium Priority)
Our server generates a **new `mcp-session-id` for every POST response** (7 unique IDs observed in one session). VS Code maintains a single session ID. The MCP Streamable HTTP spec says the server generates one ID on the first POST. The CLI tolerates this but it's technically non-compliant.

**Action:** Investigate `McpPipeServer` session ID generation — should persist one ID per SSE session.

### 2. `get_selection` Partial Response (Low Priority)
When no editor is active, our server returns `{"text":"","current":false}` (partial object). VS Code returns `"null"`. The CLI handles both, but for protocol parity we should return `"null"`.

### 3. Extra `logging` Capability (Low Priority)
Our server advertises `"logging": {}` in capabilities (MCP SDK default). VS Code only advertises `{"tools": {"listChanged": true}}`. Harmless but unnecessary.

## Captures README Minor Items

- Naming convention note says "version refers to the Copilot Chat extension version" but `vs-1.0.7` uses our VSIX version. Should generalize.
- Intro says "between Copilot CLI and VS Code" but now includes VS captures. Should say "IDE" instead of "VS Code."

## Coverage Gap

No capture invokes `get_vscode_info`. Future captures should trigger this tool to validate the response format on the wire.


---

# Normalize All RPC Path/URI Inputs via PathUtils.NormalizeFileUri

**Author:** Bishop (Server Dev)
**Date:** 2026-03-07

## Decision

All `VsServiceRpc` methods that accept file paths or URIs from CLI tools MUST normalize the input with `PathUtils.NormalizeFileUri()` before using it. This converts `file:///c%3A/path` URIs to local Windows paths and passes raw paths through unchanged.

## Rationale

CLI tools may send either raw file paths (`c:\Dev\file.cs`) or file URIs (`file:///c%3A/Dev/file.cs`). Without normalization, URI inputs cause `FileNotFoundException` or incorrect VS API behavior. The fix was initially applied only to `GetDiagnosticsAsync`; Sebastien directed it be applied uniformly.

## Scope

Methods now normalized:
- `GetDiagnosticsAsync(uri)` — already had it
- `ReadFileAsync(filePath)` — added
- `OpenDiffAsync(originalFilePath)` — added
- `CloseDiffByTabNameAsync(tabName)` — not applicable (tab name, not a path)

## Team Rule

**Any new RPC method that accepts a file path or URI from a CLI tool must apply `PathUtils.NormalizeFileUri()` at the top of the method body.** Pattern: `paramName = PathUtils.NormalizeFileUri(paramName) ?? paramName;`


---

## Capture Deep Inspection: 2026-03-09 Multi-Agent Analysis

**Date:** 2026-03-09T20:31:14Z  
**Agents:** Ripley (Protocol Lead), Bishop (Server Dev), Hudson (Tester)  
**Mode:** Parallel background analysis  
**Status:** ✅ COMPLETE

### Executive Summary

Three agents performed deep inspection of 4 updated capture files (vs-1.0.8, vscode-0.38, vscode-0.39, vscode-insiders-0.39) to identify protocol gaps, server alignment issues, and test coverage shortfalls. **Key finding:** Our extension is very well aligned overall. 1 critical bug found (session ID rotation); 5 actionable alignment issues identified; 11 test gaps documented.

---

### Protocol Analysis: Ripley (Lead)

**Finding:** 15 protocol observations across 4 captures. Protocol **stable 0.38→0.39**. 8 updates to protocol.md needed, 2 code bugs identified.

#### Key Findings (Protocol Documentation)

| # | Finding | Severity | Action |
|---|---------|----------|--------|
| 1 | `execution.taskSupport` nested location wrong in doc | Medium | Fix schema location |
| 2 | `annotations` documented but never observed | Low | Remove or clarify |
| 4 | DELETE /mcp timing (introduced 0.39) | Low | Add version note |
| 5 | LLM retry pattern (400 errors on missing session ID) | Medium | New section |
| 6 | HTTP 406 Not Acceptable error | Low | Add error table |
| 12 | Multi-session lifecycle not documented | High | New subsection |
| 13 | `copilot-cli-readonly:/` virtual URI scheme | Low | Expand docs |
| 14 | Duplicate `tools/list` calls (×2 every session) | Low | Add note |

#### Key Findings (Code Issues)

| # | Finding | Severity | Action |
|---|---------|----------|--------|
| 8 | **Session ID rotation bug** | **P1-Critical** | Server generates new ID per response; should maintain single session ID |
| 9 | Diagnostics `code` field missing | P2 | Extract from DTE error code |
| 9 | Diagnostics `range.end` always zeroed | P2 | Use actual end position |

#### Protocol Stability Conclusion

- vscode-0.38 and vscode-0.39 are **identical** in tool schemas, capabilities, and versioning
- VS 1.0.8 correctly implements the protocol with intentional IDE-specific divergences
- Multi-session pattern (SSE session + re-initialize per turn + DELETE cleanup) is the designed behavior

**Status:** Findings documented in `.squad/decisions/inbox/ripley-protocol-findings.md`

---

### Server Alignment Analysis: Bishop (Server Dev)

**Finding:** Our v1.0.8 server is **very well aligned** with VS Code. Tiered findings: 5 actionable, 3 cosmetic, 6 confirmed-correct.

#### Tier 1: Actionable Differences

| # | Issue | Priority | Effort |
|---|-------|----------|--------|
| 1 | `get_diagnostics` missing `code` field | P1 | Medium |
| 2 | `get_diagnostics` range.end always (0,0) | P1 | Medium |
| 3 | POST SSE responses missing `cache-control` header | P2 | Trivial |
| 4 | `get_diagnostics` uri schema has extra `default: ""` | P3 | Low |
| 5 | Tool schemas missing `additionalProperties`/`$schema` metadata | P3 | Low |

#### Tier 2: Cosmetic (Non-Breaking)

- `open_diff` message uses filename vs full path
- `close_diff` message format differs
- HTTP 202 response framing (both valid)

#### Tier 3: Confirmed Correct ✅

- `selection_changed` notification format — **PERFECT MATCH** across all captures
- `open_diff` / `close_diff` tool response format — **PERFECT MATCH** (field names, snake_case, values)
- `get_selection` response format — **PERFECT MATCH**
- `update_session_name` response — **PERFECT MATCH**
- `get_vscode_info` fields intentionally IDE-specific — **BY DESIGN**
- Initialize response extra `logging` capability — **HARMLESS**

**Status:** Findings documented in `.squad/decisions/inbox/bishop-server-alignment.md`

---

### Test Coverage Analysis: Hudson (Tester)

**Finding:** 173 tests passing as baseline. 11 test gaps identified: 2 P1-Critical, 6 P2-Important, 2 P3-Nice-to-have, 1 resolved.

#### P1-Critical Gaps (Must Address)

| # | Gap | Why Critical | Effort |
|---|-----|--------------|--------|
| 1 | DELETE /mcp disconnect validation | Session cleanup signal; affects protocol compliance | Small |
| 2 | HTTP 400 retry sequence not tested | LLM error path; vs-1.0.8 should have zero 400s | Medium |

#### P2-Important Gaps (Should Address)

| # | Gap | Impact | Effort |
|---|-----|--------|--------|
| 3 | HTTP 406 Not Acceptable error | Content negotiation failure; indicates Accept header enforcement | Small (part of #2) |
| 4 | `body: 0` parser robustness | Non-object body handling; regression risk on refactor | Small |
| 5 | GET /mcp SSE stream initiation | Push notification subscription; if broken, no notifications | Medium |
| 6 | Multi-session boundary regression | Core parser fix from recent update; regression risk | Medium |
| 8 | open_diff → close_diff lifecycle pairing | Complex blocking semantics; incomplete coverage | Medium |
| 9 | Cross-capture output format consistency | Response format divergence detection | Medium |

#### P3-Nice-to-Have

- HTTP 202 Accepted response validation (Small)
- `read_file` tool capture (Deferred — never called in captures)

**Estimated Effort:** 8-10 new test methods covering ~15-20 test executions

**Status:** Findings documented in `.squad/decisions/inbox/hudson-test-gaps.md`

---

### Consolidated Action Items

#### Immediate (Sprint Priority)

**Ripley (Protocol):**
- Update protocol.md with multi-session lifecycle section (High priority)
- Fix execution schema location documentation
- Document LLM retry pattern and 400/406 errors

**Bishop (Server):**
- Investigate VS Error List API for `code` field and range end position (Tier 1-P1)
- Add `cache-control: no-cache, no-transform` header to POST SSE responses (Tier 1-P2)

**Hudson (Tester):**
- Implement P1-Critical tests: DELETE /mcp (Gap 1), HTTP 400/406 retry (Gap 2)
- Implement P2-Important tests: Multi-session boundary (Gap 6), lifecycle pairing (Gap 8)

#### Medium-Term

- Fix session ID rotation bug in server (Ripley finding #8)
- Implement remaining P2-Important tests
- Populate `code` field in diagnostics (Ripley finding #9)

#### Long-Term

- Implement P3-Nice-to-have tests
- Add captures exercising `get_vscode_info`, `open_diff`, `close_diff` tools
- Protocol.md refresh script automation

---

### Multi-Agent Decision: Test Coverage Prioritization

**Consensus:** Focus on P1-Critical tests first (DELETE /mcp, HTTP 400 retry). These close the most significant gap (session disconnect and error resilience). P2-Important tests provide incremental coverage of protocol edge cases and parser robustness.

**Orchestration logs written:** `2026-03-09T20-31-14Z-ripley.md`, `2026-03-09T20-31-14Z-bishop.md`, `2026-03-09T20-31-14Z-hudson.md`

---

### Multi-Session Capture Support

**Author:** Bishop (Server Dev)
**Date:** 2026-03-08
**Status:** Implemented

Fixed 4 test failures caused by expanded captures containing multiple MCP sessions per file (2-4 sessions). TrafficParser refactored to isolate session-scoped ID matching via sequence numbers.

Key changes: FindToolCallRequestId returns (requestId, requestSeq); GetToolCallResponse now scopes matching with Seq > requestSeq predicate; new GetAllToolCallResponses method. Test C1 rewritten for sequential pairing, Test B2 handles missing update_session_name calls gracefully.

Result: All 143 tests pass. Build clean.

---

### Copilot Directive: Capture Source Truth

**Date:** 2026-03-08T17:00Z
**Directive:** vs-1.0.7.ndjson is our VSIX capture. vscode-0.38.ndjson and vscode-insiders-0.39.ndjson are ground truth (real VS Code sessions). All test decisions flow from this.

---

### Test Coverage Gaps Identified

**Author:** Hudson (Tester)
**Date:** 2026-03-08

New tool invocations in expanded captures: open_diff (3-6 per capture, 3 patterns), close_diff (1-4 per capture, 2 patterns), get_vscode_info (1-4 per capture). Proposed P1 tests: VsCodeOpenDiffResponse_HasExpectedStructure, VsCodeCloseDiffResponse_HasExpectedStructure, VsCodeGetVsCodeInfoResponse_HasExpectedStructure. Deferred pending suite stabilization.

---

### Protocol Compatibility: Multi-Session Capture Analysis

**Author:** Ripley (Lead)
**Date:** 2026-03-08
**Status:** Complete

Sebastien's contract changes verified correct: DiffOutcome and DiffTrigger constants match captures exactly. Removed fields confirmed absent. Tool schemas fully aligned with VS Code. 4 test failures were infrastructure issues (multi-session), not code issues. Minor cosmetic differences documented (message text, version strings). Recommendations: Multi-session ID collision FIXED by Bishop; new response tests pending; close_diff message cosmetic alignment optional.


---

## Research: VS API for Design-Time Build Diagnostic Change Notifications

**Author:** Ripley (Lead)
**Date:** 2025-07-19
**Status:** Research complete — recommendation ready

### Problem Statement

Our extension only pushes diagnostics_changed after explicit builds (BuildEvents.OnBuildDone) and file saves (DocumentEvents.DocumentSaved). VS Code gets real-time diagnostic updates from Roslyn's LSP 	extDocument/publishDiagnostics. We need the VS equivalent to match that experience.

We previously tried subscribing to Error List WPF table changes, but the WPF DataGrid's binding/formatting/sorting operations triggered excessive events, making it CPU-intensive. We need to go below the WPF layer.

### API Options Evaluated

**Option 1: IVsSolutionBuildManager / IVsUpdateSolutionEvents**
- Fires for explicit user-initiated builds only, not Roslyn's background design-time compilation
- Already what we have via BuildEvents.OnBuildDone
- ❌ No improvement

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

