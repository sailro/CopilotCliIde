# Project Context

- **Owner:** Sebastien
- **Project:** CopilotCliIde — A Visual Studio extension (VSIX) bridging GitHub Copilot CLI's /ide command with Visual Studio via MCP over named pipes. Three C# projects: CopilotCliIde (VS extension, net472), CopilotCliIde.Server (MCP server, net10.0), CopilotCliIde.Shared (contracts, netstandard2.0).
- **Stack:** C#, .NET, MSBuild, VSSDK, StreamJsonRpc, MCP, Windows named pipes
- **Created:** 2026-03-05

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->

### 2026-03-06 — VS Code Extension Reverse-Engineering

Reverse-engineered `github.copilot-chat-0.38.2026022303/dist/extension.js` (minified 18MB bundle) to compare VS Code's Copilot CLI /ide implementation with ours.

**Key findings:**
- VS Code registers exactly 6 MCP tools: `get_vscode_info`, `get_selection`, `open_diff`, `close_diff`, `get_diagnostics`, `update_session_name`. We have 7 (extra `read_file`).
- **open_diff**: VS Code uses virtual URI content providers (scheme `copilot-cli-readonly`) + `vscode.diff` command. Accept/Reject are **editor title bar icons** (✓/✕), not an InfoBar. Resolution values are `"SAVED"`/`"REJECTED"` (uppercase) with a `trigger` field — ours use `"accepted"`/`"rejected"` (lowercase). **Must align**: uppercase values and add `result`/`trigger` fields.
- **Critical difference**: Closing a diff tab in VS Code does NOT resolve the open_diff promise — it blocks until button click, close_diff tool, or session disconnect. Our implementation resolves as "rejected" on tab close (better UX — keep this).
- **Selection tracking**: VS Code debounces at 200ms; we have no debounce (dedup only). VS Code does NOT push current selection on client connect; we do (better UX — keep this). **Should align**: add 200ms debounce for performance.
- **Connection lifecycle**: VS Code ties MCP server to window lifecycle (extension activation), not folder/solution lifecycle. Uses Express.js + official `StreamableHTTPServerTransport`. Single pipe architecture (everything in-process).
- **Push notifications**: VS Code broadcasts `diagnostics_changed` (200ms debounce) — we don't implement this. **Recommendation**: medium-effort feature (enables real-time error feedback).
- **Custom headers** (from live proxy capture): Copilot CLI sends `X-Copilot-Session-Id`, `X-Copilot-PID`, `X-Copilot-Parent-PID` headers for session/process tracking.

**Decisions merged:** `.squad/decisions.md` — "open_diff Implementation & Selection Tracking" and "Custom Headers in MCP Protocol" sections. Key decisions: uppercase resolution values (P0), add `diagnostics_changed` notification (P2), add 200ms debounce (P2).
