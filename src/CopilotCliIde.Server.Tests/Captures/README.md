# Protocol Captures

Real MCP wire traffic captured via the PipeProxy tool between Copilot CLI and VS Code.
Used as ground truth for protocol compatibility tests.

## How to capture a new session

1. **Open VS Code** (or VS Code Insiders) with a workspace

2. **Start the proxy** in a terminal:
   ```bash
   dotnet run --project tools/PipeProxy -- capture --output src/CopilotCliIde.Server.Tests/Captures/vscode-insiders-X.XX.ndjson --verbose
   ```

3. **Connect Copilot CLI** in another terminal:
   ```bash
   copilot
   > /ide
   ```

4. **Trigger tool calls** by asking questions:
   - `what file am I looking at?` → get_selection, get_vscode_info
   - `are there any errors?` → get_diagnostics
   - Move cursor / select text → selection_changed notifications
   - Build the project → diagnostics_changed notifications

5. **Stop the proxy** with Ctrl+C — lock files are restored automatically

6. **Run tests** to validate the capture:
   ```bash
   dotnet test src/CopilotCliIde.Server.Tests
   ```

Tests automatically discover all `.ndjson` files in this directory.

## Naming convention

`{ide}-{version}.ndjson` — e.g. `vscode-insiders-0.39.ndjson`, `vscode-0.38.ndjson`

The version refers to the Copilot Chat extension version (`github.copilot-chat`).

## What the tests check

- All tool names are from the known set (no unexpected tools)
- All notification methods are from the known set (no unexpected events)
- Initialize response has expected structure
- Tool input schemas have expected shape
- Tool call responses have expected structure
- Push notifications have expected structure
- Our server's tools/list is a superset of VS Code's
