# Project Context

- **Owner:** Sebastien
- **Project:** CopilotCliIde — A Visual Studio extension (VSIX) bridging GitHub Copilot CLI's /ide command with Visual Studio via MCP over named pipes. Three C# projects: CopilotCliIde (VS extension, net472), CopilotCliIde.Server (MCP server, net10.0), CopilotCliIde.Shared (contracts, netstandard2.0).
- **Stack:** C#, .NET, MSBuild, VSSDK, StreamJsonRpc, MCP, Windows named pipes
- **Created:** 2026-03-05

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->

### 2026-03-06 — VS Code Extension Reverse-Engineering

Reverse-engineered `github.copilot-chat-0.38.2026022303/dist/extension.js` (minified 18MB bundle) to compare VS Code's Copilot CLI /ide implementation with ours.

**Key findings:**
- VS Code registers exactly 6 MCP tools: `get_vscode_info`, `get_selection`, `open_diff`, `close_diff`, `get_diagnostics`, `update_session_name`. We have 7 (extra `read_file`).
- **open_diff**: VS Code uses virtual URI content providers (scheme `copilot-cli-readonly`) + `vscode.diff` command. Accept/Reject are **editor title bar icons** (✓/✕), not an InfoBar. Resolution values are `"SAVED"`/`"REJECTED"` (uppercase) with a `trigger` field — ours use `"accepted"`/`"rejected"` (lowercase). **Must align**: uppercase values and add `result`/`trigger` fields.
- **Critical difference**: Closing a diff tab in VS Code does NOT resolve the open_diff promise — it blocks until button click, close_diff tool, or session disconnect. Our implementation resolves as "rejected" on tab close (better UX — keep this).
- **Selection tracking**: VS Code debounces at 200ms; we have no debounce (dedup only). VS Code does NOT push current selection on client connect; we do (better UX — keep this). **Should align**: add 200ms debounce for performance.
- **Connection lifecycle**: VS Code ties MCP server to window lifecycle (extension activation), not folder/solution lifecycle. Uses Express.js + official `StreamableHTTPServerTransport`. Single pipe architecture (everything in-process).
- **Push notifications**: VS Code broadcasts `diagnostics_changed` (200ms debounce) — we don't implement this. **Recommendation**: medium-effort feature (enables real-time error feedback).
- **Custom headers** (from live proxy capture): Copilot CLI sends `X-Copilot-Session-Id`, `X-Copilot-PID`, `X-Copilot-Parent-PID` headers for session/process tracking.

**Decisions merged:** `.squad/decisions.md` — "open_diff Implementation & Selection Tracking" and "Custom Headers in MCP Protocol" sections. Key decisions: uppercase resolution values (P0), add `diagnostics_changed` notification (P2), add 200ms debounce (P2).

### 2026-03-06 — VS Extension Alignment Implementation

Implemented three alignment changes to match VS Code behavior:

**1. open_diff resolution values (VsServiceRpc.cs):**
- Changed `TaskCompletionSource<string>` → `TaskCompletionSource<(string Result, string Trigger)>` for typed resolution.
- Accept → `("SAVED", "accepted_via_button")`, Reject → `("REJECTED", "rejected_via_button")`, Tab close → `("REJECTED", "closed_via_tab")`, Timeout → `("REJECTED", "timeout")`, close_diff tool → `("REJECTED", "closed_via_tool")`.
- Added `Result` and `Trigger` to `DiffResult` DTO; `UserAction` kept for backward compat.

**2. 200ms selection debounce (CopilotCliIdePackage.cs):**
- `System.Threading.Timer` captures data eagerly on UI thread (volatile fields), pushes after 200ms quiet period. Dedup kept as second filter.

**3. Diagnostics push (CopilotCliIdePackage.cs):**
- `BuildEvents.OnBuildDone` + `DocumentEvents.DocumentSaved` → 200ms debounce → collect Error List → push via `OnDiagnosticsChangedAsync`. Grouped by URI using Bishop's `DiagnosticsChangedUri` type.
- Updated `RpcClient.McpServerCallbacks` + `IMcpServerCallbacks` contract.

### 2026-03-06 — VS Extension Alignment Implementation

Implemented three alignment changes to match VS Code behavior:

**1. open_diff resolution values (VsServiceRpc.cs):**
- Changed `TaskCompletionSource<string>` → `TaskCompletionSource<(string Result, string Trigger)>` for typed resolution.
- Accept → `("SAVED", "accepted_via_button")`, Reject → `("REJECTED", "rejected_via_button")`, Tab close → `("REJECTED", "closed_via_tab")`, Timeout → `("REJECTED", "timeout")`, close_diff tool → `("REJECTED", "closed_via_tool")`.
- Added `Result` and `Trigger` to `DiffResult` DTO; `UserAction` kept for backward compat.

**2. 200ms selection debounce (CopilotCliIdePackage.cs):**
- `System.Threading.Timer` captures data eagerly on UI thread (volatile fields), pushes after 200ms quiet period. Dedup kept as second filter.

**3. Diagnostics push (CopilotCliIdePackage.cs):**
- `BuildEvents.OnBuildDone` + `DocumentEvents.DocumentSaved` → 200ms debounce → collect Error List → push via `OnDiagnosticsChangedAsync`. Grouped by URI using Bishop's `DiagnosticsChangedUri` type.
- Updated `RpcClient.McpServerCallbacks` + `IMcpServerCallbacks` contract.

**Build:** Server + Shared compile clean. 97 tests pass (3 new).

### 2026-03-07 — URI Consistency Fix in VsServiceRpc

Critical bug discovered by Ripley: three call sites in VsServiceRpc.cs produced protocol-incompatible file URIs by bypassing PathUtils.

**Affected methods:**
- `GetSelectionAsync()` — FileUrl field used raw `Uri.ToString()` instead of `PathUtils.ToVsCodeFileUrl`
- `GetDiagnosticsAsync()` — Uri field same issue
- (Server side) `CollectDiagnosticsGrouped()` — diagnostics push Uri same issue

**Impact:** Tool responses (get_selection, get_diagnostics) returned different URI formats than push notifications (selection_changed, diagnostics_changed), causing Copilot CLI to see inconsistent file references.

**Fix:** All three sites now use `PathUtils.ToVsCodeFileUrl` and `PathUtils.ToLowerDriveLetter`. This enforces the protocol requirement: file URIs must have lowercase drive letters + URL-encoded colons.

**Team rule established:** Any code producing file URIs for MCP protocol MUST use PathUtils, never raw Uri.ToString(). See `.squad/decisions.md` — "PathUtils is Protocol-Required, Not a Hack" section.

**Build status:** 109 tests pass.

### 2026-03-07T10:44:04Z — PathUtils XML Documentation

Added inline XML doc comments to `PathUtils.cs` to document the protocol requirement in source code, making the team rule discoverable in IDE tooltips.

**Changes:**
- Class-level docs explaining System.Uri gap (uppercase drive, literal colon) vs VS Code protocol (lowercase drive, URL-encoded colon)
- `ToVsCodeFileUrl()` remarks documenting custom transformation necessity
- `ToLowerDriveLetter()` remarks documenting BCL absence

**Build:** Clean compile, 109 tests pass.

### 2026-03-07T105114Z — Severity Mapping Centralization (Team Context)

Ripley centralized `vsBuildErrorLevel` → severity string mapping that was duplicated in two places (`VsServiceRpc.MapSeverity` private switch vs `CopilotCliIdePackage.CollectDiagnosticsGrouped` inline switch). Promoted `MapSeverity` to internal static, refactored caller. Hudson verified all 109 tests pass. 

**Impact on extension:** `CopilotCliIdePackage.CollectDiagnosticsGrouped` now uses single canonical mapping for diagnostics push notifications and tool responses. No functional change — ensures consistency across all severity string production.

### 2026-03-07T105840Z — MapSeverity Extension Method Refactor

Converted static `MapSeverity` helper to extension method `ToProtocolSeverity()` on `vsBuildErrorLevel`. New class `BuildErrorLevelExtensions.cs` houses the extension; call sites in `VsServiceRpc.cs` and `CopilotCliIdePackage.cs` updated to use natural API (`item.ErrorLevel.ToProtocolSeverity()`).

**Rationale:** Extension method reads more naturally at call site; keeps `VsServiceRpc` focused on RPC concerns, not utility mapping. Method stays in CopilotCliIde (not Shared) because it depends on VS SDK's `vsBuildErrorLevel` enum.

**Build:** Server compiles clean, 109 tests pass.

See `.squad/decisions.md` — "Convert MapSeverity to Extension Method" decision section.


