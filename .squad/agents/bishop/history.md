# Project Context

- **Owner:** Sebastien
- **Project:** CopilotCliIde — A Visual Studio extension (VSIX) bridging GitHub Copilot CLI's /ide command with Visual Studio via MCP over named pipes. Three C# projects: CopilotCliIde (VS extension, net472), CopilotCliIde.Server (MCP server, net10.0), CopilotCliIde.Shared (contracts, netstandard2.0).
- **Stack:** C#, .NET, MSBuild, VSSDK, StreamJsonRpc, MCP, Windows named pipes
- **Created:** 2026-03-05

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->

- **InternalsVisibleTo in server project:** The server project now exposes internals to `CopilotCliIde.Server.Tests` via `<InternalsVisibleTo>` in the csproj. Three HTTP helper methods in `McpPipeServer` are `internal static`: `ReadHttpRequestAsync`, `ReadChunkedBodyAsync`, `WriteHttpResponseAsync`. When modifying these methods, consider test impacts. See Hudson's learnings for test project details.
- **VS Code Copilot Chat MCP tool inventory (v0.38.2026022303):** VS Code registers exactly 6 MCP tools: `get_vscode_info`, `get_selection`, `open_diff`, `close_diff`, `get_diagnostics`, `update_session_name`. `read_file` is NOT an MCP tool — it's an internal agent tool. Our 7th tool (`read_file`) is an extra we provide.
- **VS Code push notifications:** VS Code broadcasts 2 notifications: `selection_changed` (we implement) and `diagnostics_changed` (we don't). Both are debounced at 200ms. There's also a targeted `add_file_reference` notification for specific sessions.
- **Response schema differences with VS Code:** Our `get_selection` uses flat fields (`selectedText`, `startLine`, `startColumn`) while VS Code uses nested `selection` object and `text`/`fileUrl` field names. Our `get_diagnostics` returns a flat list while VS Code groups by file with full `range`/`source`/`code` per diagnostic. Our `get_vscode_info` returns VS-specific solution info with no field overlap with VS Code's response. Full comparison in `.squad/decisions.md` — "MCP Tool Schemas & Compatibility" section (merged from decision inbox 2026-03-06).
- **VS Code MCP server identity:** Server name is `"vscode-copilot-cli"`, version `"0.0.1"`, label `"VS Code Copilot CLI"`. Our server uses the same name/label but version `"1.0.0"`. VS Code does NOT set `taskSupport` on tools (undefined); we set `forbidden`.
- **Critical MCP schema alignment needs (2026-03-06):** Identified 8 tool schema differences across priority tiers: add `appName`/`version` to `get_vscode_info`, align `get_selection` field names/structure, restructure `get_diagnostics` grouped-by-file, add `result`/`trigger` to `open_diff`, uppercase resolution values, match snake_case casing. Full prioritized list in `.squad/decisions.md` under "MCP Tool Schemas & Compatibility".
