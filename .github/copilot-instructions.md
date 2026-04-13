# Copilot Instructions for CopilotCliIde

## Project Overview

This is a Visual Studio extension (VSIX) that bridges GitHub Copilot CLI's `/ide` command with Visual Studio — the same way it works natively with VS Code. It consists of three projects communicating over named pipes.

## Architecture

```
Copilot CLI ──(Streamable HTTP over named pipe)──▶ CopilotCliIde.Server (net10.0)
                                                        │
                                                 (StreamJsonRpc over named pipe)
                                                        │
                                                 CopilotCliIde (VS extension, net472)
```

- **CopilotCliIde** (`net472`) — The VS extension package. Loads when a solution opens, manages the connection lifecycle (pipes, server process, lock file), subscribes to DTE events, exposes VS services via `VsServiceRpc`, and hosts an embedded terminal (Microsoft.Terminal.Wpf + ConPTY) for running Copilot CLI inside VS.
- **CopilotCliIde.Server** (`net10.0`) — ASP.NET Core (Kestrel) process hosting the MCP server on a Windows named pipe. Uses `ModelContextProtocol.AspNetCore` for the Streamable HTTP transport — Kestrel handles HTTP/1.1 framing, SSE streaming, and session management. `AspNetMcpPipeServer` is the server entry point; `TrackingSseEventStreamStore` manages SSE stream lifecycle. Contains 7 MCP tools in the `Tools/` folder.
- **CopilotCliIde.Shared** (`netstandard2.0`) — Shared RPC contracts (`IVsServiceRpc`, `IMcpServerCallbacks`) and DTOs used by both the extension and the server.

### Connection Lifecycle

The connection is tied to the VS solution lifecycle (matching VS Code's close-folder behavior):
- **Solution opens** → `StartConnectionAsync()` creates new pipes, launches the MCP server process, and writes a lock file to `~/.copilot/ide/`.
- **Solution closes** → `StopConnection()` removes the lock file, kills the server process, and disposes pipes. Copilot CLI disconnects. The embedded terminal is restarted in the current directory (not stopped).
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
- **`IWpfTextView.Selection.SelectionChanged`** detects cursor/selection changes — replaces `TextEditorEvents.LineChanged`.
- File path is read from `view.TextBuffer.Properties[typeof(ITextDocument)]`. Positions are read directly from `IWpfTextView.Selection` and `view.TextSnapshot`.

`TrackActiveView()` subscribes to the current text view's events and unsubscribes when switching documents or when the view closes. When the active frame is a non-editor window (tool window, start page, all tabs closed), the view is untracked. Temp buffers (file paths that don't exist on disk) are filtered out via `File.Exists`.

The RPC push runs via `Task.Run` off the UI thread. Deduplication via `_lastSelectionKey` prevents redundant sends.

### Initial Selection on Connect

The VS extension may push selection events before Copilot CLI connects. To avoid a stale initial display, the **MCP server** (`AspNetMcpPipeServer.PushInitialStateAsync`) fetches the current selection and diagnostics from VS via RPC when a new SSE stream is created and pushes them immediately.

### MCP Tool Registration

Tools are discovered via `WithToolsFromAssembly()` at startup using the `ModelContextProtocol` SDK's reflection-based discovery. Each tool is a class decorated with `[McpServerToolType]` containing methods decorated with `[McpServerTool]`. Tools receive an `RpcClient` from the DI container to call back into VS.

### RPC Communication

Bidirectional `StreamJsonRpc` over named pipes:
- **VS → Server**: `IMcpServerCallbacks` (e.g., `OnSelectionChangedAsync` for push notifications)
- **Server → VS**: `IVsServiceRpc` (e.g., `OpenDiffAsync`, `GetSelectionAsync`, `GetDiagnosticsAsync`)

### Error Handling

- Never let exceptions propagate to VS — catch and log or silently ignore.
- Use `{ /* Ignore */ }` for non-critical catch blocks (pipe disconnects, COM exceptions during shutdown).
- Diagnostic logs go to the **"Copilot CLI IDE"** pane in the VS Output Window (`View → Output`).

### open_diff Blocking

`OpenDiffAsync` uses a `TaskCompletionSource<string>` that blocks until the user clicks Accept/Reject in the InfoBar or closes the diff tab. The `open_diff` tool call blocks the Streamable HTTP response until the user acts — ASP.NET Core keeps the connection alive with no fixed timeout.

## MCP Tool Compatibility

Tool names and schemas must match VS Code's Copilot Chat extension exactly (`get_vscode_info`, `get_selection`, `open_diff`, `close_diff`, `get_diagnostics`, `read_file`, `update_session_name`) to ensure compatibility with the Copilot CLI `/ide` protocol.

## Embedded Terminal Subsystem

The extension hosts Copilot CLI in a dockable tool window (**Tools → Copilot CLI (Embedded Terminal)**) using Windows ConPTY + `Microsoft.Terminal.Wpf.TerminalControl` — the same native terminal control used by Windows Terminal and VS's own embedded terminal. This is a UI-only feature — it does not interact with the MCP server or RPC layer.

### Architecture

```
TerminalToolWindowControl (WPF, implements ITerminalConnection)
         │                         ▲
         │ attach/detach           │ TerminalOutput event (string)
         ▼                         │
TerminalSessionService (singleton) ─── OutputReceived ──▶ TerminalControl
         │
         ▼
TerminalProcess ──(pipes)──▶ ConPTY pseudo-console ──▶ cmd.exe /c copilot
```

### Key Files

- **`ConPty.cs`** — P/Invoke wrapper for `CreatePseudoConsole`, `ResizePseudoConsole`, `ClosePseudoConsole` and related Win32 APIs. Requires Windows 10 1809+. The `ConPty.Session` class holds all native handles and disposes them in the correct order (close pseudo-console first to signal EOF, then pipes, then process/thread handles).
- **`TerminalProcess.cs`** — Manages the `ConPty.Session` lifecycle. Spawns `cmd.exe /c copilot` in the solution directory. Reads output on a dedicated background thread with 16ms batching (60fps) via `Timer` + `StringBuilder`. Fires `OutputReceived` and `ProcessExited` events.
- **`TerminalSessionService.cs`** — Package-level singleton (created in `InitializeAsync`, stored in `VsServices.Instance`). The tool window control attaches/detaches from this service — the process survives window hide/show cycles. Supports `StartSession`, `StopSession`, `RestartSession`, `WriteInput`, `Resize`.
- **`TerminalToolWindow.cs`** — `ToolWindowPane` shell. Overrides `PreProcessMessage` to prevent VS from intercepting arrow keys, Tab, Escape, Enter, etc. — lets them reach the native `TerminalControl`.
- **`TerminalToolWindowControl.cs`** — WPF `UserControl` implementing `ITerminalConnection`. Hosts `Microsoft.Terminal.Wpf.TerminalControl` directly — zero marshaling overhead. `WriteInput` → session service, `Resize` → session start or resize, `TerminalOutput` event ← output received.
- **`TerminalThemer.cs`** — Reads VS color theme and maps it to a `TerminalTheme` struct for the native control.
- **`TerminalSettings.cs`** — Reads terminal font family and size from `WritableSettingsStore` (path `CopilotCliIde\Terminal`). Provides static `FontFamily` and `FontSize` accessors with defaults (`Cascadia Code`, 12pt).
- **`TerminalSettingsProvider.cs`** — Implements `IExternalSettingsProvider` for VS Unified Settings. Dynamically enumerates installed monospace fonts via GDI+ `InstalledFontCollection` + character-width measurement. The font dropdown uses `allowsFreeformInput` so users can type arbitrary names.
- **`registration.json`** — VS Unified Settings manifest declaring the `copilotCliIde.terminal` category with `fontFamily` (enum + freeform) and `fontSize` (integer) properties. Uses `"type": "external"` with a callback to `TerminalSettingsProvider`.

### Unified Settings

The extension exposes terminal font configuration through VS's Unified Settings API (**Settings → Copilot CLI IDE Bridge → Terminal**):

- `[ProvideSettingsManifest]` on the package registers `registration.json` as a settings manifest.
- `[ProvideService(typeof(TerminalSettingsProvider))]` proffers the external settings provider.
- `registration.json` declares properties with `"type": "external"` and a callback containing the package and service GUIDs.
- `GetEnumChoicesAsync` **must** use `await Task.Yield()` — VS silently drops enum choices from synchronous Task returns.
- Settings are persisted in `WritableSettingsStore` under `CopilotCliIde\Terminal` and read by `TerminalSettings` at theme application time.

### Lifecycle

- **Package init** → `TerminalSessionService` created and stored in `VsServices.Instance.TerminalSession`.
- **Tool window opened** → `TerminalToolWindowControl` creates `TerminalControl`, sets `Connection = this`. Process is **not** started until the control fires `Resize` (so ConPTY gets correct initial dimensions).
- **Solution opens** → `_terminalSession.RestartSession(workspaceFolder)` re-launches the CLI in the new solution directory.
- **Solution closes** → `_terminalSession.RestartSession(GetWorkspaceFolder())` restarts the CLI in the current directory (keeps terminal alive across solution switches).
- **Process exits** → User sees `[Process exited. Press Enter to restart.]`; pressing Enter calls `RestartSession()`.
- **Package dispose** → `_terminalSession.Dispose()` tears down everything.

### Threading

- `TerminalProcess.ReadLoop` runs on a dedicated `IsBackground = true` thread.
- Output is delivered to `TerminalControl` via the `TerminalOutput` event (native control handles its own threading).
- Session start is dispatched via `Dispatcher.BeginInvoke` from the `Resize` callback to access DTE on the UI thread.

### Independence from MCP/Connection System

The terminal subsystem is **completely independent** of the MCP server, RPC pipes, and lock file discovery. It is a direct ConPTY → native terminal bridge. The only shared touchpoints are:
- `VsServices.Instance` (service locator for the singleton)
- `CopilotCliIdePackage` (lifecycle management — solution open/close hooks)
- `GetWorkspaceFolder()` (shared utility for solution directory)

### Terminal.Wpf Dependency

The extension references `Microsoft.Terminal.Wpf.dll` which ships with Visual Studio — no additional runtime dependency is needed. The DLL location varies by VS channel: `CommonExtensions\Microsoft\Terminal\` (Community/Insiders) or `CommonExtensions\Microsoft\Terminal\Terminal.Wpf\` (Canary). The csproj probes both paths at build time, and the `AssemblyResolve` handler in `CopilotCliIdePackage` does the same at runtime. The reference uses `Private=false` so the DLL is not copied to output (it's already loaded in the VS AppDomain).
