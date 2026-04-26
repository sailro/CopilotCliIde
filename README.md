# Copilot CLI IDE Bridge for Visual Studio
[![Build status](https://github.com/sailro/CopilotCliIde/workflows/CI/badge.svg)](https://github.com/sailro/CopilotCliIde/actions?query=workflow%3ACI)

A Visual Studio extension that enables [GitHub Copilot CLI](https://docs.github.com/copilot/concepts/agents/about-copilot-cli) to interact with Visual Studio via the `/ide` command — the same way it works with VS Code.

The MCP server uses **[ModelContextProtocol.AspNetCore](https://github.com/modelcontextprotocol/csharp-sdk)** with Kestrel hosting over a Windows named pipe — ASP.NET Core handles all HTTP and SSE transport.

<img alt="copilot-cli with VS ide support" src="https://github.com/user-attachments/assets/d65ce04c-2158-4587-aea0-443f2b195141" />

*Copilot CLI running in an external terminal, connected to Visual Studio via `/ide`*

<img alt="copilot-cli embedded in VS" src="https://github.com/user-attachments/assets/ecb2f839-079e-47ca-af5d-462c8a11cb9d" />

*Copilot CLI embedded directly inside Visual Studio as a dockable tool window*

## Getting Started

### Prerequisites

- Visual Studio 2022 (v17.x) or Visual Studio 2026 (v18.x)
- [GitHub Copilot CLI](https://docs.github.com/copilot/concepts/agents/about-copilot-cli) installed
- An active Copilot subscription
- .NET 10.0 SDK (for the MCP server process)

### Build & Install

```bash
# Clone and build (requires MSBuild from VS — not dotnet build)
git clone https://github.com/sailro/CopilotCliIde
msbuild /restore src/CopilotCliIde/CopilotCliIde.csproj /p:Configuration=Debug

# The .vsix is produced at:
# src/CopilotCliIde/bin/Debug/CopilotCliIde.vsix
```

> **Note**: The MCP server is automatically published and bundled into the VSIX during the build.

Double-click the `.vsix` to install, or use F5 in Visual Studio to debug in the experimental instance.

### Usage

1. **Open a solution** in Visual Studio — the extension activates automatically
2. **Launch Copilot CLI** using one of:
   - **Tools → Show Copilot CLI (Embedded Terminal)** — opens a dockable terminal inside VS with Copilot CLI running (native Microsoft.Terminal.Wpf control)
   - **Tools → Launch Copilot CLI (External Terminal)** — opens Copilot CLI in an external terminal window. Configurable via **Settings → Copilot CLI IDE Bridge → External Terminal** (defaults to `cmd.exe /k copilot`; supports `wt.exe`, `pwsh.exe`, etc., with `{WorkspaceFolder}` placeholder).
   - Open a terminal manually in the solution folder
3. **Run `/ide`** in Copilot CLI — it discovers Visual Studio and connects

```
$ copilot
> /ide
  ✓ Connected to Visual Studio (CopilotCliIde)
```

Once connected, the agent can query solution info, see your selection in real time, propose diffs with Accept/Reject, and check diagnostics from the Error List.

> **Tip**: The embedded terminal (Tools → Show Copilot CLI (Embedded Terminal)) is dockable — position it alongside your editor for a side-by-side workflow.

### Diagnostics

The extension logs all bridge activity to a dedicated **"Copilot CLI IDE"** pane in the VS Output Window (`View → Output`). Each log entry is timestamped and includes:

- **MCP tool calls** received from Copilot CLI (e.g., `get_selection`, `open_diff`, `read_file`)
- **Push notifications** sent to the CLI (e.g., selection changes, diagnostics updates)

This real-time log is useful for debugging connectivity issues, verifying that the CLI can see your edits, and monitoring the bridge during active sessions.

<img alt="output pane in VS" src="https://github.com/user-attachments/assets/dfec519f-13fc-450c-afb5-c6b22eee079f" />

## Architecture

```
Copilot CLI ──(Streamable HTTP over named pipe)──▶ CopilotCliIde.Server (net10.0)
                                                        │
                                                 (StreamJsonRpc over named pipe)
                                                        │
                                                 CopilotCliIde (VS extension, net472)
```

- **CopilotCliIde.Server** (`net10.0`) — ASP.NET Core (Kestrel) process hosting the MCP server on a Windows named pipe via `ModelContextProtocol.AspNetCore`. Handles Streamable HTTP transport (POST/GET/DELETE), SSE push notifications, and session tracking. Contains 7 MCP tools in the `Tools/` folder.
- **CopilotCliIde** (`net472`) — The VS extension package. Manages the connection lifecycle, subscribes to editor events, exposes VS services over StreamJsonRpc, and hosts an embedded terminal (Microsoft.Terminal.Wpf + ConPTY) for running Copilot CLI inside VS.
- **CopilotCliIde.Shared** (`netstandard2.0`) — Shared RPC contracts and DTOs.

## Protocol

For full protocol details — discovery, transport, MCP tools, push notifications, and schemas — see **[doc/protocol.md](doc/protocol.md)**.

## Changelog

See **[CHANGELOG.md](CHANGELOG.md)** for a detailed history of changes.

## License

MIT
