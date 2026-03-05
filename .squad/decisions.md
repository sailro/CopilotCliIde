# Squad Decisions

## Active Decisions

### xUnit v3 Migration

**Author:** Hudson (Tester)
**Date:** 2026-03-05

Migrated test project from xUnit v2 (2.9.3) to xUnit v3 (3.2.2).

**Key Points:**
- Updated `Directory.Packages.props`: `xunit` → `xunit.v3` (2.9.3 → 3.2.2)
- Updated `CopilotCliIde.Server.Tests.csproj`: PackageReference, added `<OutputType>Exe</OutputType>`
- All 94 tests passed without code changes — basic xUnit features (attributes, assertions) are fully compatible
- Test infrastructure unchanged; `xunit.runner.visualstudio` (3.1.4) handles discovery and execution

**Impact:** Team should run `dotnet restore` to fetch xunit.v3. No impact on test authoring or CI/CD.

### Server Test Project Structure

**Author:** Hudson (Tester)
**Date:** 2025-07-17

Created `CopilotCliIde.Server.Tests` (xUnit, net10.0) with 94 tests across 10 test files.

**Key Points:**
- Added `InternalsVisibleTo` to the server project; 3 private static HTTP helpers (`ReadHttpRequestAsync`, `ReadChunkedBodyAsync`, `WriteHttpResponseAsync`) changed to `internal static` for testability
- All test package versions in `Directory.Packages.props` (Central Package Management)
- Tool discovery tests enforce MCP tool name compatibility contract with VS Code
- Run tests: `dotnet test src\CopilotCliIde.Server.Tests\CopilotCliIde.Server.Tests.csproj`

**Impact:** All team members should run `dotnet test` before pushing server changes. Tool name tests catch accidental schema breaks.

## Governance

- All meaningful changes require team consensus
- Document architectural decisions here
- Keep history focused on work, decisions focused on direction
