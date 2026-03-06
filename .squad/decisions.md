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

## Governance

- All meaningful changes require team consensus
- Document architectural decisions here
- Keep history focused on work, decisions focused on direction
