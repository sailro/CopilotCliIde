# Project Context

- **Owner:** Sebastien
- **Project:** CopilotCliIde — A Visual Studio extension (VSIX) bridging GitHub Copilot CLI's /ide command with Visual Studio via MCP over named pipes. Three C# projects: CopilotCliIde (VS extension, net472), CopilotCliIde.Server (MCP server, net10.0), CopilotCliIde.Shared (contracts, netstandard2.0).
- **Stack:** C#, .NET, MSBuild, VSSDK, StreamJsonRpc, MCP, Windows named pipes
- **Created:** 2026-03-05

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
