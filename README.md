# Copilot CLI IDE Bridge for Visual Studio

A Visual Studio extension that enables [GitHub Copilot CLI](https://docs.github.com/copilot/concepts/agents/about-copilot-cli) to interact with Visual Studio via the `/ide` command — the same way it works with VS Code.

## How It Works

```
Visual Studio                          Copilot CLI
┌──────────────────────┐               ┌──────────────┐
│  CopilotCliIde.vsix  │◄─named pipe──│  /ide command │
│                      │   (HTTP/MCP)  │              │
│  MCP Server          │               │  Agent       │
│  \\.\pipe\mcp-{id}   │               │              │
└──────────┬───────────┘               └──────────────┘
           │
           ▼
  ~/.copilot/ide/{id}.lock  ← discovery file
```

1. **Extension loads** when a solution is opened in Visual Studio (`LoadedWhen = SolutionState.Exists`)
2. **MCP server starts** on a Windows named pipe, listening for HTTP requests
3. **Lock file written** to `~/.copilot/ide/` with the pipe path, auth nonce, and workspace folders
4. **Copilot CLI discovers** the lock file via `/ide`, connects, and calls MCP tools to interact with VS

## Getting Started

### Prerequisites

- Visual Studio 2025 (v18.x) with the VisualStudio.Extensibility workload
- [GitHub Copilot CLI](https://docs.github.com/copilot/concepts/agents/about-copilot-cli) installed
- An active Copilot subscription

### Build & Install

```bash
# Clone and build
git clone <repo-url>
cd vsext
dotnet build

# The .vsix is produced at:
# src/CopilotCliIde/bin/Debug/net8.0-windows8.0/CopilotCliIde.vsix
```

Double-click the `.vsix` to install, or use F5 in Visual Studio to debug in the experimental instance.

### Usage

1. **Open a solution** in Visual Studio — the extension activates automatically
2. **Open Copilot CLI** in a terminal, with the working directory matching the solution folder
3. **Run `/ide`** in Copilot CLI — it discovers Visual Studio and connects

```
$ copilot
> /ide
  ✓ Connected to Visual Studio (CopilotCliIde)
```

Once connected, the agent can:

- **Query solution info** — ask *"What solution is open?"*
- **Read open file content** — including unsaved editor buffers
- **See your selection** — ask *"What text do I have selected?"*
- **Propose diffs** — the agent opens both original and proposed files in VS for comparison
- **Check diagnostics** — ask *"Are there any build errors?"*

## MCP Tools

The extension exposes 7 MCP tools to the Copilot CLI agent:

| Tool | Description |
|------|-------------|
| `get_vs_info` | Solution path, name, project list, open documents |
| `get_selection` | Active editor selection: text, file path, line/column range |
| `get_diagnostics` | Open files with dirty status and line counts |
| `read_file` | Read file content via VS document API (supports line ranges) |
| `open_diff` | Open original + proposed content side-by-side in VS |
| `close_diff` | Accept (apply changes) or reject (discard) a proposed diff |
| `update_session_name` | Set the CLI session display name |

### Tool Details

#### `get_vs_info`
Returns information about the current Visual Studio instance:
```json
{
  "ideName": "Visual Studio",
  "solutionPath": "C:\\Dev\\myproject\\MyProject.sln",
  "solutionName": "MyProject",
  "solutionDirectory": "C:\\Dev\\myproject\\",
  "projects": [{ "name": "MyProject", "path": "..." }],
  "openDocuments": ["C:\\Dev\\myproject\\Program.cs", "..."]
}
```

#### `get_selection`
Returns the current editor selection, tracked in real-time via `ITextViewChangedListener`:
```json
{
  "current": true,
  "filePath": "C:\\Dev\\myproject\\Program.cs",
  "text": "Console.WriteLine(\"Hello\");",
  "selection": {
    "start": { "line": 10, "character": 8 },
    "end": { "line": 10, "character": 35 },
    "isEmpty": false
  }
}
```

#### `read_file`
Reads file content through the VS document API, including unsaved changes:
```json
{
  "filePath": "C:\\Dev\\myproject\\Program.cs",
  "content": "using System;\n...",
  "totalLines": 42,
  "startLine": 1,
  "linesReturned": 10
}
```

#### `open_diff` / `close_diff`
The diff workflow lets the agent propose changes that you review in VS:

1. Agent calls `open_diff` → both original and proposed files open in VS
2. You compare the files side-by-side
3. Agent calls `close_diff` with `action: "accept"` to apply changes, or `"reject"` to discard

## Architecture

### Protocol Stack

```
Copilot CLI  ←→  HTTP/1.1 over Named Pipe  ←→  MCP Streamable HTTP  ←→  VS Extension
```

- **Transport**: Windows named pipe (`\\.\pipe\mcp-{uuid}.sock`)
- **Protocol**: HTTP/1.1 with Nonce authentication
- **MCP**: Streamable HTTP transport (JSON-RPC 2.0 over SSE)
- **Discovery**: Lock files in `~/.copilot/ide/{uuid}.lock`

### Lock File Format

```json
{
  "socketPath": "\\\\.\\pipe\\mcp-{uuid}.sock",
  "scheme": "pipe",
  "headers": { "Authorization": "Nonce {uuid}" },
  "pid": 12345,
  "ideName": "Visual Studio",
  "timestamp": 1672531200000,
  "workspaceFolders": ["C:\\Dev\\myproject"],
  "isTrusted": true
}
```

### VS Extensibility APIs Used

- **`VisualStudioExtensibility.Workspaces()`** — solution/project queries via Project Query API
- **`VisualStudioExtensibility.Documents()`** — open/read/close documents
- **`ITextViewChangedListener`** — real-time editor selection tracking
- **`LoadedWhen = SolutionState.Exists`** — auto-activation on solution load

### Dependencies

- `Microsoft.VisualStudio.Extensibility.Sdk` 17.14 — VS out-of-proc extension model
- `ModelContextProtocol` 0.8.0 — MCP SDK (StreamableHttpServerTransport, McpServerTool)

## Limitations

- **Diagnostics**: Full Error List access requires in-process hosting (`DiagnosticViewerService`). The extension reports open file status; for build errors, use `dotnet build` in the terminal.
- **Diff viewer**: VS Extensibility out-of-proc doesn't expose `IVsDifferenceService`. The extension opens original and proposed files side-by-side as a workaround.
- **Selection**: Tracked via `ITextViewChangedListener` events. The selection is cached and returned on the next tool call — it's not a live snapshot at the exact moment of the call.

## Development

### Project Structure

```
src/CopilotCliIde/
├── CopilotCliIdeExtension.cs      # Extension entry point
├── SelectionTracker.cs            # Real-time editor selection tracking
├── Server/
│   ├── McpPipeServer.cs           # Named pipe HTTP server + MCP routing
│   └── IdeDiscovery.cs            # Lock file management
└── Tools/
    ├── GetVsInfoTool.cs           # Solution/project info
    ├── GetSelectionTool.cs        # Editor selection
    ├── GetDiagnosticsTool.cs      # Build diagnostics
    ├── ReadFileTool.cs            # File content reading
    ├── OpenDiffTool.cs            # Diff proposal
    ├── CloseDiffTool.cs           # Diff accept/reject
    └── UpdateSessionNameTool.cs   # Session naming
```

### Debugging

1. Set `CopilotCliIde` as the startup project
2. F5 launches the VS experimental instance
3. Open a solution in the experimental instance
4. Run `copilot` in a terminal, then `/ide` to connect
5. Check `~/.copilot/ide/vs-error.log` if the extension fails to start
6. Check `~/.copilot/ide/vs-connection.log` for connection-level errors

## License

MIT
