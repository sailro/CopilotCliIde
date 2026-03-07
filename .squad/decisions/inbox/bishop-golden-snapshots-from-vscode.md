# Golden Snapshots Now Sourced from Real VS Code Extension

**Author:** Bishop (Server Dev)
**Date:** 2026-03-07

## Context

Sebastien flagged that testing our code against snapshots derived from our own implementation is circular. The golden snapshot files must come from the actual VS Code Insiders extension — the authoritative protocol source.

## What Changed

Replaced all 8 golden snapshot files in `src/CopilotCliIde.Server.Tests/Snapshots/` with data extracted directly from VS Code Insiders Copilot Chat extension **v0.39.2026030604** (`dist/extension.js`).

## Protocol Gaps Discovered

These are real differences between VS Code's actual wire format and what we had in our snapshots:

1. **`get_vscode_info` response:** VS Code returns `{version, appName, appRoot, language, machineId, sessionId, uriScheme, shell}`. We return completely different fields. When response tests are wired up, `appRoot`, `language`, `machineId`, `sessionId`, `uriScheme`, `shell` will be flagged as missing from our response.

2. **`diagnostics_changed` notification:** VS Code includes `source` per diagnostic. Our push notification omits it. Also, notification entries have only `uri` (no `filePath`), while the `get_diagnostics` tool has both.

3. **`open_diff` / `close_diff` responses:** VS Code does NOT include an `error` field. Errors use MCP's `{isError: true}` wrapper. Our server includes `error: null` in success responses — harmless extra field under superset comparison, but worth knowing.

## Impact

- **112 tests pass** — no regressions
- Response snapshots are reference files not yet in active tests. When wired in, they will surface the `get_vscode_info` gap.
- Team should prioritize adding response schema tests (Hudson's domain) to leverage these golden files.

## Refresh Process

Documented in `Snapshots/README.md`. Manual process: read `extension.js`, locate `registerTool()` calls and notification broadcasts, extract JSON structures.
