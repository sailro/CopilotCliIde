# Protocol Compatibility Snapshots

## What Are These Files?

Golden JSON snapshots representing the expected MCP protocol wire format between
Copilot CLI and the IDE server. These snapshots define the **structural contract**
that our Visual Studio extension must satisfy to remain compatible with Copilot CLI.

## Source: VS Code Insiders Extension

**Extension:** GitHub Copilot Chat (`github.copilot-chat`)
**Version:** `0.39.2026030604`
**Extracted from:** `~/.vscode-insiders/extensions/github.copilot-chat-0.39.2026030604/dist/extension.js`
**Extraction date:** 2026-03-07

Snapshots were extracted by reading the minified `extension.js` and locating each
`registerTool` call, the `bj()` selection helper, the `fna()` diagnostics-per-URI
builder, the `Ana()` severity mapper, the lock file writer, and the notification
`broadcastNotification` calls. Every field name, nesting structure, and type was
taken directly from the running VS Code extension code — not from our own source
or documentation.

## Snapshot Format

```json
{
  "filePath": "string",
  "line": "number",
  "isEmpty": "boolean",
  "nested": {
    "child": "string"
  }
}
```

- `"string"` — expects a JSON string (or null)
- `"number"` — expects a JSON number (or null)
- `"boolean"` — expects a JSON boolean (or null)
- Objects — expects same property names and nesting
- Arrays — expects at least one element matching the item shape (if golden has one)
- Properties named `_comment` are ignored during comparison

## How to Refresh

1. **Find** the installed extension: `~/.vscode-insiders/extensions/github.copilot-chat-*/`
2. **Read** `dist/extension.js` (minified, ~19 MB)
3. **Search** for `registerTool("get_vscode_info"` etc. — each occurrence has the
   full `inputSchema` object and response construction in surrounding code
4. **Search** for `broadcastNotification("selection_changed"` and
   `broadcastNotification("diagnostics_changed"` for notification shapes
5. **Search** for lock file writer (`.copilot/ide`, `socketPath`, `ideName`) for
   the lock file schema
6. **Extract** the relevant JSON structures and normalize to TYPE-PLACEHOLDER format
7. **Replace** the golden files in this directory
8. **Run** `dotnet test src/CopilotCliIde.Server.Tests` to see what changed
9. **Update** the version number above

## When to Refresh

- **Monthly** — or when a major VS Code Insiders / Copilot CLI update ships
- **On CI failure** — if a snapshot test fails, check whether VS Code updated its protocol
- **After Copilot CLI releases** — the protocol may evolve

This is a **manual process**, not automated in CI. The protocol changes infrequently
(a few times per year at most).

## Key Extraction Notes

### `get_vscode_info` response
VS Code returns 8 fields from `vscode.env.*` and `vscode.version`. Our server
returns VS-specific fields (`solutionPath`, `projects`, etc.) which is acceptable
as a superset — but we must include `appName` and `version` at minimum.

### `get_diagnostics` tool vs `diagnostics_changed` notification
The tool response includes both `uri` and `filePath` per file group. The push
notification (`fna()`) includes only `uri` — no `filePath`. Both include `source`.

### `open_diff` / `close_diff` responses
No `error` field in the response object. Errors use MCP's standard `isError`
wrapper (`{content: [{type: "text", text: "..."}], isError: true}`).

### Severity values
`Ana()` maps `DiagnosticSeverity` enum to: `"error"`, `"warning"`, `"information"`,
`"hint"`, `"unknown"`. Our extension only maps error/warning/information (VS Error
List doesn't distinguish hint).

## File Inventory

| File | What It Validates |
|------|------------------|
| `tools-list.json` | 6 VS Code tool names, descriptions, and input parameter schemas |
| `get-vscode-info-response.json` | `get_vscode_info` tool response — VS Code's 8 env fields |
| `get-selection-response.json` | `get_selection` tool response — nested selection with text/fileUrl |
| `get-diagnostics-response.json` | `get_diagnostics` tool response — array grouped by file with range/source/code |
| `open-diff-response.json` | `open_diff` tool response — snake_case, result/trigger, no error field |
| `close-diff-response.json` | `close_diff` tool response — snake_case, no error field |
| `selection-changed-notification.json` | `selection_changed` SSE notification — bj() shape, no `current` field |
| `diagnostics-changed-notification.json` | `diagnostics_changed` SSE notification — fna() shape with `source`, no `filePath` |
