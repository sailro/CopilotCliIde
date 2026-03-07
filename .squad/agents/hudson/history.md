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
- **MCP schema alignment tests (2026-03-06):** Added 29 tests (97→126) for VS Code schema alignment. New file `ToolOutputSchemaTests.cs` validates snake_case keys in `open_diff`/`close_diff` tool outputs and `get_diagnostics` array-at-root format. Extended `DtoSerializationTests` with `VsInfoResult.AppName`/`Version`, `DiffResult.Result` SAVED/REJECTED uppercase, trigger values, `SelectionResult` JSON key shapes, diagnostics grouping, and `DiagnosticItem.Code`/`Source`. Extended `RpcClientTests` with `DiagnosticsChanged` event tests. Extended `NotificationFormatTests` with `diagnostics_changed` JSON-RPC format.
- **RpcClient is sealed:** Cannot be mocked with NSubstitute. Tool methods (other than `UpdateSessionNameTool`) can only be tested via output schema validation on the anonymous objects they produce, not via direct invocation with mocked dependencies.
- **Severity mapping centralization (2026-03-07):** Ripley promoted `VsServiceRpc.MapSeverity` to internal static, refactored `CopilotCliIdePackage.CollectDiagnosticsGrouped` to call it. No new VSIX unit test added (disproportionate infrastructure cost; indirect coverage via `NotificationFormatTests` sufficient). All 109 tests pass.
- **Team Notification (2026-03-07T11:41:21Z):** Hicks implemented husky pre-commit hook for whitespace enforcement. All team members should run `npm run format` before committing and `npm run format:check` in CI pipelines. The pre-commit hook validates all .NET files. See `.squad/decisions.md` — "Whitespace Enforcement via Husky Pre-Commit Hook" for details.
- **Protocol compatibility test infrastructure — Phase 1 (2026-03-07):** Created golden JSON snapshot infrastructure in `Snapshots/` directory (8 JSON files + README) with TYPE-PLACEHOLDER format for structural comparison. Built `JsonSchemaComparer` utility for superset comparison (walks JSON trees, checks property names/types, allows extras in actual, fails on missing). Added `ProtocolCompatibilityTests` with 3 tests: `ToolsList_ContainsAllVsCodeTools` (verifies 6 VS Code tool names present), `ToolsList_ToolInputSchemas_MatchGolden` (verifies each tool's parameter names and types match golden file), `LockFile_Schema_MatchesVsCode` (verifies 8 required fields with correct types). Updated csproj with `<Content Include="Snapshots\**" CopyToOutputDirectory="PreserveNewest" />`. All 112 tests pass (109 existing + 3 new). Phase 2 (McpHandshakeTests integration) and Phase 3 (per-tool response golden tests) are next.

### 2026-03-07T17:02:27Z — Protocol Compatibility Phase 1 Orchestration Complete

Orchestration log written to `.squad/orchestration-log/2026-03-07T17-02-27Z-hudson.md`. This spawn delivered the Phase 1 golden snapshot infrastructure for protocol compatibility test automation.

**Outcome:** ✅ SUCCESS. Golden Snapshots/ directory created with 8 JSON files, JsonSchemaComparer utility implemented, ProtocolCompatibilityTests class with 3 passing tests. Current test count: 112 passing (109 existing + 3 new). No regressions.

**Phase 1 complete.** Bishop's test seam (RpcClient constructor) is now in place. Phase 2 (handshake integration test, ~4 hours) can proceed using both Bishop's infrastructure and the golden snapshot framework completed here.

**Next:** Phase 2 — Bishop to lead handshake integration tests. Phase 3 and 4 deferred (per-tool golden tests and refresh script).

