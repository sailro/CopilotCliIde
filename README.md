# Copilot CLI IDE Bridge for Visual Studio

A Visual Studio extension that enables [GitHub Copilot CLI](https://docs.github.com/copilot/concepts/agents/about-copilot-cli) to interact with Visual Studio via the `/ide` command — the same way it works with VS Code.

<img alt="copilot" src="https://github.com/user-attachments/assets/7f1c42ce-235b-4179-a40e-ba8758a6c6fe" />

## How It Works

1. **Package loads** when a solution is opened in Visual Studio (`ProvideAutoLoad`)
2. **RPC server starts** on a named pipe, exposing VS services (DTE, diff, diagnostics)
3. **MCP server process launches** as a separate net10.0 child process, connecting back to VS via RPC
4. **Lock file written** to `~/.copilot/ide/` with the MCP pipe path, auth nonce, and workspace folders
5. **Copilot CLI discovers** the lock file via `/ide`, connects, and calls MCP tools to interact with VS
6. **Solution changes tracked** — when you close/open solutions, the lock file's workspace folders update automatically
7. **Stale files cleaned** — on startup, lock files and log files from dead processes are removed

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

# Build both the VSSDK package and the MCP server
dotnet build src/CopilotCliIde.Server/CopilotCliIde.Server.csproj
dotnet build src/CopilotCliIde/CopilotCliIde.csproj

# The .vsix is produced at:
# src/CopilotCliIde/bin/Debug/CopilotCliIde.vsix
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
- **Read file content** — from disk, with line range support
- **See your selection** — ask *"What text do I have selected?"*
- **Propose diffs** — the agent opens a real VS diff view using `IVsDifferenceService`
- **Check diagnostics** — get errors and warnings from the VS Error List

## MCP Tools

The extension exposes 7 MCP tools to the Copilot CLI agent:

| Tool | Description |
|------|-------------|
| `get_vscode_info` | Solution path, name, project list |
| `get_selection` | Active editor selection: text, file path, line/column range |
| `get_diagnostics` | Errors and warnings from the VS Error List (filterable by URI) |
| `read_file` | Read file content from disk (supports line ranges) |
| `open_diff` | Open a real VS diff view comparing original with proposed changes. Blocks until user acts. |
| `close_diff` | Close a diff tab by its tab name |
| `update_session_name` | Set the CLI session display name |

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

1. Agent calls `open_diff` with `original_file_path`, `new_file_contents`, and `tab_name` → a real VS diff view opens via `IVsDifferenceService`
2. You review the changes in the native diff viewer
3. Agent calls `close_diff` with the same `tab_name` to close the diff tab

> **Note**: Tool names and schemas match VS Code's Copilot Chat extension exactly (`get_vscode_info`, `get_selection`, `open_diff`, `close_diff`, `get_diagnostics`, `update_session_name`) to ensure full compatibility with the Copilot CLI `/ide` protocol.

## Architecture

### Protocol Stack

- **MCP Transport**: Windows named pipe (`\\.\pipe\mcp-{uuid}.sock`) with HTTP/1.1 + Nonce auth
- **RPC Transport**: Windows named pipe (`\\.\pipe\copilot-cli-rpc-{uuid}`) with StreamJsonRpc
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
