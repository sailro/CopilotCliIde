# Protocol Doc Round 2 Analysis — No Doc Changes Needed

**Author:** Ripley (Lead)
**Date:** 2026-03-08
**Priority:** Informational

## Verdict

All 7 Round 1 protocol.md fixes are **confirmed accurate** against the new vs-1.0.7 capture. No protocol documentation changes required.

## Server Compliance Issues Found (Not Doc Issues)

These are implementation gaps in our server vs the protocol as documented:

### 1. Session ID Rotation (Medium Priority)
Our server generates a **new `mcp-session-id` for every POST response** (7 unique IDs observed in one session). VS Code maintains a single session ID. The MCP Streamable HTTP spec says the server generates one ID on the first POST. The CLI tolerates this but it's technically non-compliant.

**Action:** Investigate `McpPipeServer` session ID generation — should persist one ID per SSE session.

### 2. `get_selection` Partial Response (Low Priority)
When no editor is active, our server returns `{"text":"","current":false}` (partial object). VS Code returns `"null"`. The CLI handles both, but for protocol parity we should return `"null"`.

### 3. Extra `logging` Capability (Low Priority)
Our server advertises `"logging": {}` in capabilities (MCP SDK default). VS Code only advertises `{"tools": {"listChanged": true}}`. Harmless but unnecessary.

## Captures README Minor Items

- Naming convention note says "version refers to the Copilot Chat extension version" but `vs-1.0.7` uses our VSIX version. Should generalize.
- Intro says "between Copilot CLI and VS Code" but now includes VS captures. Should say "IDE" instead of "VS Code."

## Coverage Gap

No capture invokes `get_vscode_info`. Future captures should trigger this tool to validate the response format on the wire.
