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

### 2026-03-07T11:41:21Z — Team Notification: Husky Pre-Commit Hook Installed

Hicks implemented whitespace enforcement via husky pre-commit hook (Sebastien's directive). **All team members should adopt the following practices immediately:**

- Before committing: Run `npm run format` to auto-fix any whitespace violations
- In CI pipelines: Use `npm run format:check` to verify without modifying
- Git commit now automatically triggers the pre-commit hook running `dotnet format --verify-no-changes`

**No action required from Bishop or Server code.** The hook validates all .NET files across the solution. See `.squad/decisions.md` — "Whitespace Enforcement via Husky Pre-Commit Hook" for full details.

### 2026-03-07 — RpcClient Test Seam Constructor

Added an `internal RpcClient(IVsServiceRpc vsServices)` constructor to `RpcClient.cs` as a test seam for MCP handshake integration tests. The constructor sets `VsServices` directly, bypassing the real named-pipe connection. The default `public RpcClient()` constructor and the `ConnectAsync` flow are untouched.

**McpPipeServer.StartAsync needs no changes.** It receives an `RpcClient` and accesses `_rpcClient.VsServices` — it never calls `ConnectAsync` itself (that's `Program.cs`'s job). A pre-configured `RpcClient` with `VsServices` already set works seamlessly with `StartAsync`.

**Key detail:** `_pipe` and `_rpc` remain null when using the test constructor. `Dispose()` already null-checks both, so cleanup is safe. The test project has `InternalsVisibleTo` access (established earlier by Hudson).

### 2026-03-07T17:02:27Z — Protocol Compatibility Phase 1 Orchestration Complete

Orchestration log written to `.squad/orchestration-log/2026-03-07T17-02-27Z-bishop.md`. This spawn delivered the RpcClient test seam for Phase 2 handshake integration tests.

**Outcome:** ✅ SUCCESS. RpcClient constructor in place, no McpPipeServer changes needed, 109 existing tests all passing. Test seam ready for Hudson and Bishop to use in Phase 2 `McpHandshakeTests`.

**Next:** Phase 2 (handshake integration test) — Bishop to lead, with Hudson's Phase 1 golden infrastructure as foundation.

### 2026-03-07 — Golden Snapshots Replaced with Real VS Code Extension Data

Extracted protocol reference data directly from VS Code Insiders Copilot Chat extension v0.39.2026030604 (`dist/extension.js`, ~19MB minified). Replaced all 8 golden snapshot files in `src/CopilotCliIde.Server.Tests/Snapshots/` with the actual wire-format structures VS Code produces.

**Extraction method:** Read minified `extension.js`, located each `registerTool()` call, the `bj()` selection helper, `fna()` diagnostics-per-URI builder, `Ana()` severity mapper, and `broadcastNotification()` calls. Every field name, nesting, and type taken directly from the extension code.

**Differences found vs previous snapshots (which were our own interpretations):**

1. **`get-vscode-info-response.json` — MAJOR CHANGE:** Previous snapshot had our VS-specific fields (`ideName`, `solutionPath`, `projects`, `processId`). VS Code actually returns 8 `vscode.env.*` fields: `version`, `appName`, `appRoot`, `language`, `machineId`, `sessionId`, `uriScheme`, `shell`. When these snapshots get wired into response tests, this WILL catch that our `get_vscode_info` response is missing `appRoot`, `language`, `machineId`, `sessionId`, `uriScheme`, `shell`.

2. **`open-diff-response.json` — removed `error` field:** VS Code doesn't include `error` in the success response object. Errors use MCP's standard `{isError: true}` wrapper.

3. **`close-diff-response.json` — removed `error` field:** Same as open_diff — no `error` field in the response.

4. **`diagnostics-changed-notification.json` — added `source` field:** VS Code's `fna()` includes `source` (from `diagnostic.source`) in each diagnostic item. Our previous snapshot was missing it. Also confirmed: notification entries do NOT include `filePath` (only `uri`), unlike the `get_diagnostics` tool response which has both.

5. **`tools-list.json` — added descriptions:** Enriched with VS Code's exact tool description strings for documentation. No structural change to parameters.

6. **`get-selection-response.json`, `get-diagnostics-response.json`, `selection-changed-notification.json` — confirmed correct:** These were already accurate.

**Test results:** All 112 tests pass. This is expected because only `tools-list.json` is used in active tests (parameter schema check), and parameters didn't change. The response snapshots are golden reference files not yet wired into tests — when they are, they'll surface real protocol drift.

**Key protocol insights from v0.39.2026030604:**
- `Ana()` severity mapper includes `"hint"` and `"unknown"` values (we only map error/warning/information)
- `diagnostics_changed` notification omits `filePath` (only `uri`) while `get_diagnostics` tool includes both
- `selection_changed` notification does NOT include `current` field (that's only on the tool response)
- `open_diff` error cases use MCP's `isError` wrapper, not a field in the response object
- Lock file schema confirmed identical: `{socketPath, scheme, headers, pid, ideName, timestamp, workspaceFolders, isTrusted}`

### 2026-03-07 — PipeProxy Capture Tool (Phase 1)

Built `tools/PipeProxy/` — a net10.0 console app that sits between Copilot CLI and VS Code on the named pipe, capturing all MCP traffic as NDJSON. This is Phase 1 of Ripley's proxy-based compatibility testing design.

**Architecture:**
```
Copilot CLI ──(HTTP-on-pipe)──→ PipeProxy ──(HTTP-on-pipe)──→ VS Code Insiders
             ←─────────────────           ←─────────────────
                                    │
                                    ▼
                              traffic.ndjson
```

**Files created:**
- `tools/PipeProxy/PipeProxy.csproj` — net10.0 console app, references `CopilotCliIde.Server` for HTTP helpers
- `tools/PipeProxy/Program.cs` — CLI entry point with `capture` subcommand, `--output`, `--verbose` flags
- `tools/PipeProxy/LockFileManager.cs` — Scans `~/.copilot/ide/*.lock` for VS Code instances, writes proxy lock, hides original, restores on dispose
- `tools/PipeProxy/ProxyRelay.cs` — Bidirectional HTTP-on-pipe relay. Handles POST/DELETE (request-response forwarding) and GET (SSE chunked stream relay). Contains all HTTP read/write helpers for proxy forwarding.
- `tools/PipeProxy/TrafficLogger.cs` — Thread-safe NDJSON logger. Parses bodies as JSON (including SSE `data:` extraction). Verbose mode shows traffic on stderr.

**Key implementation decisions:**
- Added `InternalsVisibleTo Include="PipeProxy"` to Server csproj to reuse `McpPipeServer.ReadHttpRequestAsync` and `ReadChunkedBodyAsync`
- Each CLI pipe connection gets a matching VS Code client connection — requests relayed in lockstep
- SSE relay reads individual chunks from VS Code and forwards raw bytes to CLI while parsing SSE events for logging
- Lock file hiding: original VS Code lock renamed to `.proxy-hidden`, restored on dispose/Ctrl+C
- POST response bodies in SSE format (`event: message\ndata: {...}`) are parsed to extract JSON for structured logging

**Build/test results:** Clean build, `--help` works, `capture` correctly reports "no VS Code found" when none running. All 112 existing server tests pass. Format check clean.

### 2026-03-08 — PipeProxy SSE POST Response Fix

Fixed critical bug in `ProxyRelay.HandleConnectionAsync` where POST responses with `Content-Type: text/event-stream` (SSE) were broken. The MCP Streamable HTTP transport can return SSE chunked streams from POST requests (e.g., `initialize`, `tools/call`) — not just GET.

**Root cause:** The proxy's POST handler called `ReadHttpResponseAsync` which consumed the entire chunked body via `ReadChunkedBodyAsync`, then `WriteHttpResponseAsync` stripped `Transfer-Encoding: chunked` and re-wrote the response with `Content-Length`. This broke the SSE contract — the CLI expects chunked event-stream framing.

**Fix:** After reading VS Code's response headers, the proxy now checks `Content-Type`:
- **`text/event-stream`**: Forward raw header bytes + relay chunked stream transparently via `RelayChunkedStreamAsync` (same as the GET/SSE path). Headers including `Transfer-Encoding: chunked` and `Mcp-Session-Id` are preserved byte-for-byte.
- **Other** (e.g., `202 Accepted` for notifications): Read full body via new `ReadResponseBodyAsync` helper, then forward with `WriteHttpResponseAsync` as before.

**Files changed:** `tools/PipeProxy/ProxyRelay.cs` — modified `HandleConnectionAsync` POST/DELETE section, added `ReadResponseBodyAsync` private helper.

**Build/test results:** PipeProxy builds clean. All 112 server tests pass.

### 2026-03-08 — HTTP Response Framing Aligned to VS Code Express Server

Aligned `McpPipeServer`'s HTTP response framing to match VS Code's Express server exactly, based on traffic captures comparing both servers. Three changes:

1. **Lowercase HTTP headers:** All response header names in `WriteHttpResponseAsync` and the SSE GET handler changed from PascalCase (`Content-Type`, `Cache-Control`, `Connection`, `Mcp-Session-Id`, `Transfer-Encoding`) to lowercase (`content-type`, `cache-control`, `connection`, `mcp-session-id`, `transfer-encoding`). HTTP headers are case-insensitive per RFC 7230, but matching VS Code's Express output byte-for-byte ensures maximum compatibility.

2. **Chunked Transfer-Encoding for SSE POST responses:** `WriteHttpResponseAsync` now uses `Transfer-Encoding: chunked` instead of `Content-Length` when `contentType` is `text/event-stream`. Non-SSE error responses (400, 401, 404, etc.) still use `Content-Length`. The chunked body is wrapped with hex-length prefix and `0\r\n\r\n` terminator.

3. **Single-write SSE notification chunks:** `PushNotificationAsync` now combines the chunk size line, SSE event data, and trailing CRLF into a single `byte[]` via `Buffer.BlockCopy` before writing. Previously these were 3 separate `WriteAsync` calls, causing 3 separate pipe reads on the client side.

**Files changed:** `src/CopilotCliIde.Server/McpPipeServer.cs`, `src/CopilotCliIde.Server.Tests/HttpResponseTests.cs`, `src/CopilotCliIde.Server.Tests/TrafficReplayTests.cs`

**Test updates:** Updated 6 `HttpResponseTests` assertions for lowercase headers. Updated `TrafficReplayTests.ReadHttpResponseAsync` helper to handle chunked responses (since POST SSE responses no longer use Content-Length). All 131 tests pass. Format check clean.

