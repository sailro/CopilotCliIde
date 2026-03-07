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

## Governance

- All meaningful changes require team consensus
- Document architectural decisions here
- Keep history focused on work, decisions focused on direction
