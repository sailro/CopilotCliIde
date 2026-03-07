# Copilot CLI IDE Bridge for Visual Studio

A Visual Studio extension that enables [GitHub Copilot CLI](https://docs.github.com/copilot/concepts/agents/about-copilot-cli) to interact with Visual Studio via the `/ide` command — the same way it works with VS Code.

<img alt="copilot" src="https://github.com/user-attachments/assets/7f1c42ce-235b-4179-a40e-ba8758a6c6fe" />

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

Once connected, the agent can query solution info, see your selection in real time, propose diffs with Accept/Reject, and check diagnostics from the Error List.

### Diagnostics

The extension logs all bridge activity to a dedicated **"Copilot CLI IDE"** pane in the VS Output Window (`View → Output`). Each log entry is timestamped and includes:

- **MCP tool calls** received from Copilot CLI (e.g., `get_selection`, `open_diff`, `read_file`)
- **Push notifications** sent to the CLI (e.g., selection changes, diagnostics updates)

This real-time log is useful for debugging connectivity issues, verifying that the CLI can see your edits, and monitoring the bridge during active sessions.

## Protocol

For full protocol details — discovery, transport, MCP tools, push notifications, and schemas — see **[doc/protocol.md](doc/protocol.md)**.

## License

MIT
