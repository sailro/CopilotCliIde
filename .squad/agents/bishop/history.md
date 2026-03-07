# Project Context

- **Owner:** Sebastien
- **Project:** CopilotCliIde — A Visual Studio extension (VSIX) bridging GitHub Copilot CLI's /ide command with Visual Studio via MCP over named pipes. Three C# projects: CopilotCliIde (VS extension, net472), CopilotCliIde.Server (MCP server, net10.0), CopilotCliIde.Shared (contracts, netstandard2.0).
- **Stack:** C#, .NET, MSBuild, VSSDK, StreamJsonRpc, MCP, Windows named pipes
- **Created:** 2026-03-05

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->

- **InternalsVisibleTo in server project:** The server project now exposes internals to `CopilotCliIde.Server.Tests` via `<InternalsVisibleTo>` in the csproj. Three HTTP helper methods in `McpPipeServer` are `internal static`: `ReadHttpRequestAsync`, `ReadChunkedBodyAsync`, `WriteHttpResponseAsync`. When modifying these methods, consider test impacts. See Hudson's learnings for test project details.
- **VS Code Copilot Chat MCP tool inventory (v0.38.2026022303):** VS Code registers exactly 6 MCP tools: `get_vscode_info`, `get_selection`, `open_diff`, `close_diff`, `get_diagnostics`, `update_session_name`. `read_file` is NOT an MCP tool — it's an internal agent tool. Our 7th tool (`read_file`) is an extra we provide.
- **VS Code push notifications:** VS Code broadcasts 2 notifications: `selection_changed` (we implement) and `diagnostics_changed` (we don't). Both are debounced at 200ms. There's also a targeted `add_file_reference` notification for specific sessions.
- **Response schema differences with VS Code:** Our `get_selection` uses flat fields (`selectedText`, `startLine`, `startColumn`) while VS Code uses nested `selection` object and `text`/`fileUrl` field names. Our `get_diagnostics` returns a flat list while VS Code groups by file with full `range`/`source`/`code` per diagnostic. Our `get_vscode_info` returns VS-specific solution info with no field overlap with VS Code's response. Full comparison in `.squad/decisions.md` — "MCP Tool Schemas & Compatibility" section (merged from decision inbox 2026-03-06).
- **VS Code MCP server identity:** Server name is `"vscode-copilot-cli"`, version `"0.0.1"`, label `"VS Code Copilot CLI"`. Our server uses the same name/label but version `"1.0.0"`. VS Code does NOT set `taskSupport` on tools (undefined); we set `forbidden`.
- **Critical MCP schema alignment needs (2026-03-06):** Identified 8 tool schema differences across priority tiers: add `appName`/`version` to `get_vscode_info`, align `get_selection` field names/structure, restructure `get_diagnostics` grouped-by-file, add `result`/`trigger` to `open_diff`, uppercase resolution values, match snake_case casing. Full prioritized list in `.squad/decisions.md` under "MCP Tool Schemas & Compatibility".
- **MCP schema alignment implemented (2026-03-06):** All 6 critical/moderate schema differences resolved. `SelectionResult` now uses nested `Selection` object with `Text`/`FileUrl` field names matching VS Code. `DiagnosticsResult` groups by file with `Uri`/`FilePath`/`Diagnostics` per group, using lowercase severity strings and `DiagnosticRange`. `DiffResult` has `Result` ("SAVED"/"REJECTED") and `Trigger` fields. `CloseDiffTool` and `OpenDiffTool` transform to snake_case (`tab_name`, `already_closed`) via anonymous objects for MCP output. `VsInfoResult` includes `AppName` and `Version`. `diagnostics_changed` push notification is wired end-to-end (callback → RpcClient event → Program.cs → McpPipeServer broadcast). The tool layer transforms DTOs to anonymous objects when MCP JSON casing differs from RPC casing — this pattern avoids adding System.Text.Json attributes to the netstandard2.0 Shared project.
- **Protocol doc audit (2026-03-06):** Audited `doc/protocol.md` against all server implementation code. Found one discrepancy: `get_diagnostics` response example was missing the `filePath` field in diagnostic file groups — both our `FileDiagnostics` DTO and VS Code include it. Fixed. All other aspects verified accurate: lock file schema (8 fields match `IdeDiscovery.cs`), all 6 tool names and response shapes, `open_diff` blocking behavior (30s timeout skip), push notification schemas (`selection_changed` and `diagnostics_changed`), transport details (HTTP parsing, SSE chunked encoding, session ID handling), server name/version/capabilities. Note: `diagnostics_changed` push in `Program.cs` omits `source` field from diagnostic items — minor gap vs `get_diagnostics` tool output but not a doc error since the doc example also omits it.

- **Documentation Session Completion (2026-03-07):** Parallel audit with Ripley. Completed protocol.md verification — confirmed all lock file fields, tool names/schemas, blocking behavior, notifications, transport details, server capabilities. Fixed get_diagnostics response example. All documentation now fully aligned with implementation.

- **PathUtils URI Protocol Requirement (2026-03-07):** Critical team rule established. Code producing file URIs for MCP protocol MUST use `PathUtils.ToVsCodeFileUrl` and `PathUtils.ToLowerDriveLetter`, never raw `Uri.ToString()`. BCL's System.Uri produces incompatible formats (uppercase drive + literal colon); VS Code protocol requires lowercase drive + URL-encoded colon (`%3A`). Ripley found three VsServiceRpc call sites violating this rule, causing URI inconsistency between tool responses and push notifications. All three fixed. See `.squad/decisions.md` — "PathUtils is Protocol-Required, Not a Hack" section. **Team rule:** Enforce this in all future file URI code.

- **Severity mapping centralization (2026-03-07):** Ripley centralized duplicate `vsBuildErrorLevel` → severity string mapping. Promoted `VsServiceRpc.MapSeverity` to internal static. Extension's `CollectDiagnosticsGrouped` now calls it for consistent severity strings in `diagnostics_changed` push notifications. Server tests remain at 109 passing (no VSIX test impact).

### 2026-03-07T11:24:48Z — ErrorListReader Deduplication (Extension Refactor)

Hicks consolidated duplicate Error List iteration logic from `VsServiceRpc.GetDiagnosticsAsync` (RPC on-demand path, 100-item cap) and `CopilotCliIdePackage.CollectDiagnosticsGrouped` (push notification path, 200-item cap) into a new `ErrorListReader.CollectGrouped()` helper class.

**Impact on diagnostics data flow:**
- Both `get_diagnostics` RPC tool and `diagnostics_changed` push notification now use identical collection logic via `ErrorListReader.CollectGrouped()`
- Returns `List<FileDiagnostics>` (the MCP-format DTO); push path projects to `DiagnosticsChangedUri` type via LINQ
- URI generation, severity mapping (via `ToProtocolSeverity()`), and position conversion now centralized — changes to these aspects apply to both paths automatically
- RPC and push paths remain separate at cap level (100 vs 200 items) but share canonical collection algorithm
- No functional change to MCP protocol output — both paths produce same DiagnosticItem field values

**Implication for server:** If server-side diagnostics logic ever needs updating (e.g., field additions, filtering), the extension's shared reader establishes a canonical format that both tool and notification paths conform to.

