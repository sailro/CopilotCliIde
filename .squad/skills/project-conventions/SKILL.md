---
name: "project-conventions"
description: "Core conventions and patterns for this codebase"
domain: "project-conventions"
confidence: "medium"
source: "template"
---

## Context

> **This is a starter template.** Replace the placeholder patterns below with your actual project conventions. Skills train agents on codebase-specific practices — accurate documentation here improves agent output quality.

## Patterns

### [Pattern Name]

Describe a key convention or practice used in this codebase. Be specific about what to do and why.

### Error Handling

<!-- Example: How does your project handle errors? -->
<!-- - Use try/catch with specific error types? -->
<!-- - Log to a specific service? -->
<!-- - Return error objects vs throwing? -->

### Testing

- Test framework: xUnit v3 (3.2.2) with Microsoft.NET.Test.Sdk
- Test location: `src/CopilotCliIde.Server.Tests/`
- Run command: `dotnet test src\CopilotCliIde.Server.Tests\CopilotCliIde.Server.Tests.csproj`
- Mocking: NSubstitute 5.3.0
- Server project exposes internals to test project via `InternalsVisibleTo`
- Tool name compatibility is enforced by `ToolDiscoveryTests` — never rename MCP tools without checking VS Code parity

### Code Style

<!-- Example: Linting, formatting, naming conventions -->
<!-- - Linter: ESLint config? -->
<!-- - Formatter: Prettier? -->
<!-- - Naming: camelCase, snake_case, etc.? -->

### File Structure

<!-- Example: How is the project organized? -->
<!-- - src/ — Source code -->
<!-- - test/ — Tests -->
<!-- - docs/ — Documentation -->

## Examples

```
// Add code examples that demonstrate your conventions
```

## Anti-Patterns

<!-- List things to avoid in this codebase -->
- **[Anti-pattern]** — Explanation of what not to do and why.
