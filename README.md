# Copilot CLI IDE Bridge for Visual Studio

A Visual Studio extension that enables [GitHub Copilot CLI](https://docs.github.com/copilot/concepts/agents/about-copilot-cli) to interact with Visual Studio via the `/ide` command — the same way it works with VS Code.

## How It Works

```
Visual Studio (devenv.exe)             Copilot CLI
┌──────────────────────┐               ┌──────────────┐
│  CopilotCliIde.vsix  │               │  /ide command │
│  (VSSDK AsyncPackage) │               │              │
│       │               │               │  Agent       │
│       │ RPC (pipe)    │               │              │
│       ▼               │               └──────┬───────┘
│  CopilotCliIde.Server │◄─named pipe─────────┘
│  (net8.0 process)     │   (HTTP/MCP)
│  \\.\pipe\mcp-{id}    │
└──────────┬────────────┘
           │
           ▼
  ~/.copilot/ide/{id}.lock  ← discovery file
```

1. **Package loads** when a solution is opened in Visual Studio (`ProvideAutoLoad`)
2. **RPC server starts** on a named pipe, exposing VS services (DTE, diff, diagnostics)
3. **MCP server process launches** as a separate net8.0 child process, connecting back to VS via RPC
4. **Lock file written** to `~/.copilot/ide/` with the MCP pipe path, auth nonce, and workspace folders
5. **Copilot CLI discovers** the lock file via `/ide`, connects, and calls MCP tools to interact with VS

## Getting Started

### Prerequisites

- Visual Studio 2022 (v17.x) or Visual Studio 2025/2026 (v18.x)
- [GitHub Copilot CLI](https://docs.github.com/copilot/concepts/agents/about-copilot-cli) installed
- An active Copilot subscription
- .NET 8.0 SDK (for the MCP server process)

### Build & Install

```bash
# Clone and build
git clone <repo-url>
cd vsext

# Build both the VSSDK package and the MCP server
dotnet build src/CopilotCliIde.Server/CopilotCliIde.Server.csproj
dotnet build src/CopilotCliIde/CopilotCliIde.csproj -p:DeployExtension=false

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
| `get_vs_info` | Solution path, name, project list |
| `get_selection` | Active editor selection: text, file path, line/column range |
| `get_diagnostics` | Errors and warnings from the VS Error List |
| `read_file` | Read file content from disk (supports line ranges) |
| `open_diff` | Open a real VS diff view comparing original with proposed changes |
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
Returns errors and warnings from the Visual Studio Error List:
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

1. Agent calls `open_diff` → a real VS diff view opens via `IVsDifferenceService`
2. You review the changes in the native diff viewer
3. Agent calls `close_diff` with `action: "accept"` to apply changes, or `"reject"` to discard

## Architecture

### Two-Process Design

The extension uses a two-process architecture to avoid assembly version conflicts between the MCP SDK (which requires modern `Microsoft.Extensions.*` packages) and Visual Studio's AppDomain:

- **VSSDK Package** (`CopilotCliIde`, net472) — runs in-proc in `devenv.exe`, provides access to VS services via DTE and COM interfaces
- **MCP Server** (`CopilotCliIde.Server`, net8.0) — runs as a separate child process, hosts the MCP protocol over named pipes

The two processes communicate via **StreamJsonRpc** over a named pipe. The MCP server calls back to VS for operations like opening diff views, reading the error list, or querying the active selection.

### Protocol Stack

```
Copilot CLI  ←→  HTTP/1.1 over Named Pipe  ←→  MCP (JSON-RPC/SSE)  ←→  MCP Server Process
                                                                            │
                                                                     RPC (StreamJsonRpc)
                                                                            │
                                                                     VS Package (DTE, IVsDifferenceService)
```

- **MCP Transport**: Windows named pipe (`\\.\pipe\mcp-{uuid}.sock`) with HTTP/1.1 + Nonce auth
- **RPC Transport**: Windows named pipe (`\\.\pipe\copilot-cli-rpc-{uuid}`) with StreamJsonRpc
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

### VS APIs Used

- **`DTE2`** — solution/project queries, active document, selection
- **`IVsDifferenceService`** — native diff view (`OpenComparisonWindow2`)
- **`ErrorList.ErrorItems`** — build diagnostics from the Error List
- **`ProvideAutoLoad`** — auto-activation when a solution is loaded

### Dependencies

- `Microsoft.VisualStudio.SDK` 17.14 + `Microsoft.VSSDK.BuildTools` — legacy VSSDK extension model
- `StreamJsonRpc` 2.24 — inter-process RPC communication
- `ModelContextProtocol` 0.8.0 — MCP SDK (in the server process only)

## Development

### Project Structure

```
src/
├── CopilotCliIde/                    # VSSDK Package (net472, in-proc)
│   ├── CopilotCliIdePackage.cs       # AsyncPackage entry point
│   ├── VsServiceRpc.cs              # IVsServiceRpc implementation (DTE, diff, diagnostics)
│   ├── ServerProcessManager.cs       # Manages MCP server child process
│   ├── Server/
│   │   └── IdeDiscovery.cs           # Lock file management
│   └── source.extension.vsixmanifest
│
├── CopilotCliIde.Server/            # MCP Server (net8.0, child process)
│   ├── Program.cs                    # Entry point
│   ├── McpPipeServer.cs             # Named pipe HTTP server + MCP routing
│   ├── RpcClient.cs                 # StreamJsonRpc client to VS
│   └── Tools/                        # MCP tool implementations
│       ├── OpenDiffTool.cs
│       ├── CloseDiffTool.cs
│       ├── GetVsInfoTool.cs
│       ├── GetSelectionTool.cs
│       ├── GetDiagnosticsTool.cs
│       ├── ReadFileTool.cs
│       └── UpdateSessionNameTool.cs
│
└── CopilotCliIde.Shared/            # Shared contracts (netstandard2.0)
    └── Contracts.cs                  # IVsServiceRpc interface + DTOs
```

### Debugging

1. Set `CopilotCliIde` as the startup project
2. F5 launches the VS experimental instance with the extension deployed
3. Open a solution in the experimental instance
4. Run `copilot` in a terminal, then `/ide` to connect
5. Check `~/.copilot/ide/vs-error.log` if the extension fails to start
6. Check `~/.copilot/ide/vs-connection.log` for MCP connection-level errors
7. Attach a second debugger to the `CopilotCliIde.Server` process for MCP/tool debugging

## License

MIT
