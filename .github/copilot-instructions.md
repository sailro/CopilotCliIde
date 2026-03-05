# Copilot Instructions for CopilotCliIde

## Project Overview

This is a Visual Studio extension (VSIX) that bridges GitHub Copilot CLI's `/ide` command with Visual Studio — the same way it works natively with VS Code. It consists of three projects communicating over named pipes.

## Architecture

```
Copilot CLI ──(MCP over named pipe)──▶ CopilotCliIde.Server (net10.0)
                                            │
                                     (StreamJsonRpc over named pipe)
                                            │
                                     CopilotCliIde (VS extension, net472)
```

- **CopilotCliIde** (`net472`) — The VS extension package. Loads when a solution opens, manages the connection lifecycle (pipes, server process, lock file), subscribes to DTE events, and exposes VS services via `VsServiceRpc`.
- **CopilotCliIde.Server** (`net10.0`) — A standalone child process hosting an MCP server on a Windows named pipe. Receives tool calls from Copilot CLI and forwards them to VS via RPC. Contains 7 MCP tools in the `Tools/` folder.
- **CopilotCliIde.Shared** (`netstandard2.0`) — Shared RPC contracts (`IVsServiceRpc`, `IMcpServerCallbacks`) and DTOs used by both the extension and the server.

### Connection Lifecycle

The connection is tied to the VS solution lifecycle (matching VS Code's close-folder behavior):
- **Solution opens** → `StartConnectionAsync()` creates new pipes, launches the MCP server process, and writes a lock file to `~/.copilot/ide/`.
- **Solution closes** → `StopConnection()` removes the lock file, kills the server process, and disposes pipes. Copilot CLI disconnects.
- **Solution switches** → `StopConnection()` then `StartConnectionAsync()` — a fresh connection for the new workspace.

### Discovery

Lock files in `~/.copilot/ide/{uuid}.lock` contain the pipe path, auth nonce, PID, and workspace folders. Copilot CLI discovers VS by scanning these files. Stale lock files from dead processes are cleaned on startup.

## Build

The VS extension **must** be built with MSBuild, not `dotnet build`:

```bash
# Build the extension (includes server via PublishServerBeforeBuild target)
msbuild src/CopilotCliIde/CopilotCliIde.csproj /p:Configuration=Debug

# Build only the server (dotnet is fine here)
dotnet build src/CopilotCliIde.Server/CopilotCliIde.Server.csproj
```

The VSIX build target automatically runs `dotnet publish` on the server project and bundles it under `McpServer/` in the VSIX.

## Code Style

- **Indentation**: Tabs for C# files (see `.editorconfig`)
- **Naming**: `_camelCase` for private/internal fields, `PascalCase` for constants
- **`var`**: Use `var` everywhere
- **`this.`**: Avoid unless absolutely necessary
- **Nullable**: Enabled across all projects (`<Nullable>enable</Nullable>`)
- **Language version**: Latest C# (`<LangVersion>latest</LangVersion>`)
- **Braces**: Allman style (new line before every open brace)
- **Usings**: `dotnet_sort_system_directives_first = true`

## Key Patterns

### Threading in VS Extension

- Always use `ThreadHelper.ThrowIfNotOnUIThread()` or `await JoinableTaskFactory.SwitchToMainThreadAsync()` before accessing DTE or any VS service.
- Use `JoinableTaskFactory.RunAsync()` to bridge sync event handlers (like `SolutionEvents.Opened`) into async code.
- Errors in background tasks should be caught and logged (via `LogError`), never crash VS.

### Selection Tracking

Selection notifications use **native VS editor APIs**, not DTE COM interop:
- **`IVsMonitorSelection`** (`SEID_WindowFrame`) detects active window changes — replaces `WindowEvents.WindowActivated`.
- **`IWpfTextView.Selection.SelectionChanged`** and **`Caret.PositionChanged`** detect cursor/selection changes — replaces `TextEditorEvents.LineChanged`.
- **`ITextDocument.FilePath`** and **`ITextSelection`** read the selection state — replaces `DTE.ActiveDocument` + `TextDocument.Selection`.

`TrackActiveView()` subscribes to the current text view's events and unsubscribes when switching documents or when the view closes. The RPC push runs via `Task.Run` off the UI thread. Deduplication via `_lastSelectionKey` prevents redundant sends.

### MCP Tool Registration

Tools are discovered via reflection at startup. Each tool is a class decorated with `[McpServerToolType]` containing methods decorated with `[McpServerTool]`. Tools receive an `RpcClient` from the DI container to call back into VS.

### RPC Communication

Bidirectional `StreamJsonRpc` over named pipes:
- **VS → Server**: `IMcpServerCallbacks` (e.g., `OnSelectionChangedAsync` for push notifications)
- **Server → VS**: `IVsServiceRpc` (e.g., `OpenDiffAsync`, `GetSelectionAsync`, `GetDiagnosticsAsync`)

### Error Handling

- Never let exceptions propagate to VS — catch and log or silently ignore.
- Use `{ /* Ignore */ }` for non-critical catch blocks (pipe disconnects, COM exceptions during shutdown).
- Diagnostic logs go to `~/.copilot/ide/vs-error-{pid}.log` and `vs-connection-{pid}.log`.

### open_diff Blocking

`OpenDiffAsync` uses a `TaskCompletionSource<string>` that blocks until the user clicks Accept/Reject in the InfoBar or closes the diff tab. The MCP server skips its 30s timeout for `open_diff` calls specifically.

## MCP Tool Compatibility

Tool names and schemas must match VS Code's Copilot Chat extension exactly (`get_vscode_info`, `get_selection`, `open_diff`, `close_diff`, `get_diagnostics`, `read_file`, `update_session_name`) to ensure compatibility with the Copilot CLI `/ide` protocol.
