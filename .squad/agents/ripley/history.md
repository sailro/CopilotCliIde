# Project Context

- **Owner:** Sebastien
- **Project:** CopilotCliIde — A Visual Studio extension (VSIX) bridging GitHub Copilot CLI's /ide command with Visual Studio via MCP over named pipes. Three C# projects: CopilotCliIde (VS extension, net472), CopilotCliIde.Server (MCP server, net10.0), CopilotCliIde.Shared (contracts, netstandard2.0).
- **Stack:** C#, .NET, MSBuild, VSSDK, StreamJsonRpc, MCP, Windows named pipes
- **Created:** 2026-03-05

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->

### 2026-03-06 — VS Code Lock File Reverse Engineering

Reverse-engineered the Copilot Chat extension (`github.copilot-chat-0.38.2026022303/dist/extension.js`) to compare lock file formats.

**Key findings:**
- Our lock file schema is **identical** to VS Code's — same 8 fields, same names, same types. No alignment needed.
- Both use `~/.copilot/ide/{uuid}.lock` path, `\\.\pipe\mcp-{uuid}.sock` pipe names, `Nonce {uuid}` auth.
- Both do PID-based stale lock cleanup on startup.
- **One difference:** VS Code updates its lock file when workspace folders change or trust is granted. We write once. Low priority for VS since it's single-solution.
- The "Watching IDE lock file" log from Copilot CLI is on the **CLI side**, not the extension. The extension only writes/updates/deletes.
- VS Code's MCP server is an Express HTTP server listening on the named pipe, not StreamJsonRpc. The `/mcp` fragment in the URI maps to the HTTP route.
- Decompiled key functions: `vSn` (create lock), `_Sn` (stale cleanup), `B1i` (pipe name), `iCt` class (lock file model with update/remove).

**Decision merged:** `.squad/decisions.md` — "Lock File Format Compatibility" section. Verdict: no action required on schema; low-priority enhancement for workspace folder tracking.
