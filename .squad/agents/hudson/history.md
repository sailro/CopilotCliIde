# Project Context

- **Owner:** Sebastien
- **Project:** CopilotCliIde — A Visual Studio extension (VSIX) bridging GitHub Copilot CLI's /ide command with Visual Studio via MCP over named pipes. Three C# projects: CopilotCliIde (VS extension, net472), CopilotCliIde.Server (MCP server, net10.0), CopilotCliIde.Shared (contracts, netstandard2.0).
- **Stack:** C#, .NET, MSBuild, VSSDK, StreamJsonRpc, MCP, Windows named pipes
- **Created:** 2026-03-05

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->

- **Test project:** `CopilotCliIde.Server.Tests` (xUnit v3, net10.0) in `src/CopilotCliIde.Server.Tests/`. Run with `dotnet test src\CopilotCliIde.Server.Tests\CopilotCliIde.Server.Tests.csproj`. 94 tests covering HTTP parsing, chunked encoding, response writing, DTO serialization, tool discovery, RPC client events, and service provider DI.
- **Central Package Management:** All package versions must be in `Directory.Packages.props` — `PackageReference` in `.csproj` files must NOT have `Version` attributes. Test packages (xunit.v3 3.2.2, xunit.runner.visualstudio 3.1.4, Microsoft.NET.Test.Sdk 17.14.1, coverlet.collector 6.0.4, NSubstitute 5.3.0) are registered there.
- **xUnit v3 migration (2026-03-05):** Migrated from xUnit v2 (2.9.3) to xUnit v3 (3.2.2). Required changes: replace `xunit` package with `xunit.v3` in Directory.Packages.props, update PackageReference in test csproj, and add `<OutputType>Exe</OutputType>` to test project PropertyGroup. All 94 tests passed without any test code changes (basic xUnit v2 features are fully compatible with v3).
- **InternalsVisibleTo:** The server project exposes internals to `CopilotCliIde.Server.Tests` via `<InternalsVisibleTo>` in the csproj. Three HTTP helper methods in `McpPipeServer` were changed from `private static` to `internal static` for testability: `ReadHttpRequestAsync`, `ReadChunkedBodyAsync`, `WriteHttpResponseAsync`.
- **MCP tool names are a compatibility contract:** The 7 tool names (`get_vscode_info`, `get_selection`, `open_diff`, `close_diff`, `get_diagnostics`, `read_file`, `update_session_name`) must match VS Code's Copilot Chat extension exactly. `ToolDiscoveryTests` enforces this.
- **UpdateSessionNameTool is the only pure tool:** It has no RPC dependency and can be tested directly. All other tools are thin RPC forwarders.
- **ReadChunkedBodyAsync throws EndOfStreamException on truncated streams:** Due to `ReadExactlyAsync` usage for trailing `\r\n` — this is intentional behavior.
