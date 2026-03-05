# Project Context

- **Owner:** Sebastien
- **Project:** CopilotCliIde — A Visual Studio extension (VSIX) bridging GitHub Copilot CLI's /ide command with Visual Studio via MCP over named pipes. Three C# projects: CopilotCliIde (VS extension, net472), CopilotCliIde.Server (MCP server, net10.0), CopilotCliIde.Shared (contracts, netstandard2.0).
- **Stack:** C#, .NET, MSBuild, VSSDK, StreamJsonRpc, MCP, Windows named pipes
- **Created:** 2026-03-05

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->

- **InternalsVisibleTo in server project:** The server project now exposes internals to `CopilotCliIde.Server.Tests` via `<InternalsVisibleTo>` in the csproj. Three HTTP helper methods in `McpPipeServer` are `internal static`: `ReadHttpRequestAsync`, `ReadChunkedBodyAsync`, `WriteHttpResponseAsync`. When modifying these methods, consider test impacts. See Hudson's learnings for test project details.
