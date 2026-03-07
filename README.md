# Copilot CLI IDE Bridge for Visual Studio

A Visual Studio extension that enables [GitHub Copilot CLI](https://docs.github.com/copilot/concepts/agents/about-copilot-cli) to interact with Visual Studio via the `/ide` command — the same way it works with VS Code.

<img alt="copilot" src="https://github.com/user-attachments/assets/7f1c42ce-235b-4179-a40e-ba8758a6c6fe" />

## How It Works

1. **Package loads** when a solution is opened in Visual Studio (`ProvideAutoLoad`)
2. **RPC server starts** on a named pipe, exposing VS services (DTE, diff, diagnostics)
3. **MCP server process launches** as a separate net10.0 child process, connecting back to VS via bidirectional RPC
4. **Lock file written** to `~/.copilot/ide/` with the MCP pipe path, auth nonce, and workspace folders
5. **Copilot CLI discovers** the lock file via `/ide`, connects, and calls MCP tools to interact with VS
6. **Real-time selection events** — when you switch files or move your cursor, the CLI is notified via SSE. When Copilot CLI first connects, the MCP server pushes the current selection immediately so the display is correct from the start.
7. **Solution lifecycle** — closing a solution tears down the connection (removes lock file, kills MCP server process); opening a new solution creates a fresh connection with new pipes and lock file — matching VS Code's close-folder behavior
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
| `get_vscode_info` | VS instance info: version, solution path, project list |
| `get_selection` | Active editor selection: text, file URL, nested position range |
| `get_diagnostics` | Errors and warnings from the VS Error List, grouped by file (filterable by URI) |
| `read_file` | Read file content from disk (supports line ranges) |
| `open_diff` | Open a VS diff view with Accept/Reject InfoBar. Blocks until user acts. |
| `close_diff` | Close a diff tab by its tab name |
| `update_session_name` | Set the CLI session display name |

All tools include `execution.taskSupport: "forbidden"` metadata, matching VS Code's protocol. The first 6 tools match VS Code's Copilot Chat extension; `read_file` is an additional capability.

### Tool Details

#### `get_vscode_info`
Returns information about the current Visual Studio instance:
```json
{
  "ideName": "Visual Studio",
  "appName": "Visual Studio",
  "version": "17.13.35931.197",
  "solutionPath": "C:\\Dev\\myproject\\MyProject.sln",
  "solutionName": "MyProject",
  "solutionDirectory": "C:\\Dev\\myproject",
  "projects": [{ "name": "MyProject", "fullName": "..." }],
  "processId": 12345
}
```

#### `get_selection`
Returns the current editor selection with nested position range, matching VS Code's format:
```json
{
  "current": true,
  "filePath": "C:\\Dev\\myproject\\Program.cs",
  "fileUrl": "file:///c%3A/Dev/myproject/Program.cs",
  "text": "Console.WriteLine(\"Hello\");",
  "selection": {
    "start": { "line": 9, "character": 8 },
    "end": { "line": 9, "character": 35 },
    "isEmpty": false
  }
}
```

#### `get_diagnostics`
Returns errors and warnings from the Visual Studio Error List, grouped by file. Accepts an optional `uri` parameter to filter. Returns a JSON array at root (matching VS Code's format):
```json
[
  {
    "uri": "file:///c%3A/Dev/myproject/Program.cs",
    "filePath": "C:\\Dev\\myproject\\Program.cs",
    "diagnostics": [
      {
        "message": "The name 'x' does not exist in the current context.",
        "severity": "error",
        "range": {
          "start": { "line": 11, "character": 4 },
          "end": { "line": 11, "character": 4 }
        },
        "source": "MyProject",
        "code": null
      }
    ]
  }
]
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
4. Returns `result` (`"SAVED"` or `"REJECTED"`) and `trigger` (e.g. `"accepted_via_button"`, `"rejected_via_button"`, `"closed_via_tab"`, `"closed_via_tool"`, `"timeout"`)
5. Agent can call `close_diff` with the same `tab_name` to programmatically close the diff

> **Note**: Tool names and schemas match VS Code's Copilot Chat extension (`get_vscode_info`, `get_selection`, `open_diff`, `close_diff`, `get_diagnostics`, `update_session_name`) to ensure full compatibility with the Copilot CLI `/ide` protocol. `read_file` is an additional tool not present in VS Code.

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

### `diagnostics_changed`
Pushed after builds complete and documents are saved, when the Error List content changes:
```json
{
  "jsonrpc": "2.0",
  "method": "diagnostics_changed",
  "params": {
    "uris": [
      {
        "uri": "file:///c%3A/Dev/myproject/Program.cs",
        "diagnostics": [
          {
            "range": {
              "start": { "line": 11, "character": 4 },
              "end": { "line": 11, "character": 4 }
            },
            "message": "The name 'x' does not exist in the current context.",
            "severity": "error",
            "code": null
          }
        ]
      }
    ]
  }
}
```

Both notifications use a **200ms debounce** — the push fires once the editor has been quiet for 200ms, matching VS Code's behavior. Notifications are also deduplicated: if the content hasn't changed, no notification is sent. The RPC calls run off the UI thread to keep VS responsive. Temp buffers (paths that don't exist on disk) are filtered out from selection events.

When Copilot CLI first connects to the SSE stream, the MCP server fetches the current selection from VS via RPC and pushes it immediately — ensuring the display is correct even if the extension loaded before Copilot CLI was running.

### Protocol Stack

- **MCP Transport**: Windows named pipe (`\\.\pipe\mcp-{uuid}.sock`) with Streamable HTTP + Nonce auth
- **RPC Transport**: Windows named pipe (`\\.\pipe\copilot-cli-rpc-{uuid}`) with bidirectional StreamJsonRpc
- **SSE Stream**: Chunked transfer encoding over GET /mcp for `selection_changed` and `diagnostics_changed` notifications
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
