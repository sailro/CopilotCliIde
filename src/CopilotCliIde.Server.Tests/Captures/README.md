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

4. **Trigger tool calls** by asking questions, here is a script:
    - _trigger selection changes (full file, only cursor move, 1 line, 1 word), change files_.
    - _trigger diagnostic changes (error in file, rebuild, then fix, rebuild), then keep a syntax error on purpose_.
    - `What is my current selection ?`
    - `What are my current diagnostics ?`
    - `What are my current diagnostics in Program.cs ?`
    - `We have mcp tools exposed by the connected IDE (from where you already got selection and diagnostics). Make sure to call them all to test the behavior: open_diff, close_diff, get_vscode_info, update_session_name. I need to have them called. If necessary use the named pipe listed in the lock file ~\.copilot\ide directly. All the protocol details are in "doc\protocol.md".`
    - `retry open_diff tool, I'm going to hit reject this time.`
    - `retry open_diff tool, then 5 sec after, call close_diff to cancel the diff view yourself.`

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
