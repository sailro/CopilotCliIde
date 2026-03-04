# Copilot CLI IDE Bridge for Visual Studio

A Visual Studio extension that enables [GitHub Copilot CLI](https://docs.github.com/copilot/concepts/agents/about-copilot-cli) to interact with Visual Studio via the `/ide` command — the same way it works with VS Code.

<img alt="copilot" src="https://github.com/user-attachments/assets/7f1c42ce-235b-4179-a40e-ba8758a6c6fe" />

## How It Works

1. **Package loads** when a solution is opened in Visual Studio (`ProvideAutoLoad`)
2. **RPC server starts** on a named pipe, exposing VS services (DTE, diff, diagnostics)
3. **MCP server process launches** as a separate net10.0 child process, connecting back to VS via bidirectional RPC
4. **Lock file written** to `~/.copilot/ide/` with the MCP pipe path, auth nonce, and workspace folders
5. **Copilot CLI discovers** the lock file via `/ide`, connects, and calls MCP tools to interact with VS
6. **Real-time selection events** — when you switch files or move your cursor, the CLI is notified via SSE
7. **Solution changes tracked** — when you close/open solutions, the lock file's workspace folders update automatically
8. **Stale files cleaned** — on startup, lock files and log files from dead processes are removed

## Getting Started

### Prerequisites

- Visual Studio 2022 (v17.x) or Visual Studio 2026 (v18.x)
- [GitHub Copilot CLI](https://docs.github.com/copilot/concepts/agents/about-copilot-cli) installed
- An active Copilot subscription
- .NET 10.0 SDK (for the MCP server process)

### Build & Install

```bash
# Clone and build
git clone https://github.com/sailro/CopilotCliIde

# Build the MCP server (dotnet)
dotnet build src/CopilotCliIde.Server/CopilotCliIde.Server.csproj

# Build the VS extension (requires MSBuild from VS)
msbuild src/CopilotCliIde/CopilotCliIde.csproj /p:Configuration=Debug

# The .vsix is produced at:
# src/CopilotCliIde/bin/Debug/CopilotCliIde.vsix
```

> **Note**: The VSIX project must be built with MSBuild (`msbuild`), not `dotnet build`.

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
- **Read file content** — from disk, with line range support
- **See your selection** — the CLI receives real-time selection change notifications
- **Propose diffs** — opens a VS diff view with Accept/Reject buttons; blocks until you act
- **Check diagnostics** — get errors and warnings from the VS Error List

## MCP Tools

The extension exposes 7 MCP tools to the Copilot CLI agent:

| Tool | Description |
|------|-------------|
| `get_vscode_info` | Solution path, name, project list |
| `get_selection` | Active editor selection: text, file path, line/column range |
| `get_diagnostics` | Errors and warnings from the VS Error List (filterable by URI) |
| `read_file` | Read file content from disk (supports line ranges) |
| `open_diff` | Open a VS diff view with Accept/Reject InfoBar. Blocks until user acts. |
| `close_diff` | Close a diff tab by its tab name |
| `update_session_name` | Set the CLI session display name |

All tools include `execution.taskSupport: "forbidden"` metadata, matching VS Code's protocol.

### Tool Details

#### `get_vscode_info`
Returns information about the current Visual Studio instance:
```json
{
  "ideName": "Visual Studio",
  "solutionPath": "C:\\Dev\\myproject\\MyProject.sln",
  "solutionName": "MyProject",
  "solutionDirectory": "C:\\Dev\\myproject",
  "projects": [{ "name": "MyProject", "fullName": "..." }],
  "processId": 12345
}
```

#### `get_selection`
Returns the current editor selection, read on-demand from DTE:
```json
{
  "current": true,
  "filePath": "C:\\Dev\\myproject\\Program.cs",
  "selectedText": "Console.WriteLine(\"Hello\");",
  "startLine": 10,
  "startColumn": 8,
  "endLine": 10,
  "endColumn": 35,
  "isEmpty": false
}
```

#### `get_diagnostics`
Returns errors and warnings from the Visual Studio Error List. Accepts an optional `uri` parameter to filter by file:
```json
{
  "diagnostics": [
    {
      "severity": "vsBuildErrorLevelHigh",
      "message": "The name 'x' does not exist in the current context.",
      "file": "C:\\Dev\\myproject\\Program.cs",
      "line": 12,
      "column": 5,
      "project": "MyProject"
    }
  ]
}
```

#### `read_file`
Reads file content from disk with optional line range:
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

1. Agent calls `open_diff` with `original_file_path`, `new_file_contents`, and `tab_name`
2. VS opens a native diff view via `IVsDifferenceService` with an Accept/Reject InfoBar
3. The MCP call **blocks** until you click Accept, Reject, or close the diff tab
4. The result (`"accepted"` or `"rejected"`) is returned to the agent
5. Agent can call `close_diff` with the same `tab_name` to programmatically close the diff

> **Note**: Tool names and schemas match VS Code's Copilot Chat extension exactly (`get_vscode_info`, `get_selection`, `open_diff`, `close_diff`, `get_diagnostics`, `update_session_name`) to ensure full compatibility with the Copilot CLI `/ide` protocol.

## Real-time Notifications

The MCP server supports a **GET /mcp** SSE (Server-Sent Events) stream for server-to-client push notifications:

### `selection_changed`
Pushed when the user switches files or moves the cursor in VS:
```json
{
  "jsonrpc": "2.0",
  "method": "selection_changed",
  "params": {
    "text": "",
    "filePath": "c:\\Dev\\myproject\\Program.cs",
    "fileUrl": "file:///c%3A/Dev/myproject/Program.cs",
    "selection": {
      "start": { "line": 10, "character": 0 },
      "end": { "line": 10, "character": 0 },
      "isEmpty": true
    }
  }
}
```

Notifications are debounced (150ms for window activation, 50ms for cursor movement) and deduplicated to avoid flooding.

### Protocol Stack

- **MCP Transport**: Windows named pipe (`\\.\pipe\mcp-{uuid}.sock`) with Streamable HTTP + Nonce auth
- **RPC Transport**: Windows named pipe (`\\.\pipe\copilot-cli-rpc-{uuid}`) with bidirectional StreamJsonRpc
- **SSE Stream**: Chunked transfer encoding over GET /mcp for `selection_changed` notifications
- **Discovery**: Lock files in `~/.copilot/ide/{uuid}.lock`
- **Diagnostics**: Per-process log files in `~/.copilot/ide/` (`vs-error-{pid}.log`, `vs-connection-{pid}.log`)

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

## License

MIT
