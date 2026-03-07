---
name: "protocol-golden-testing"
description: "Pattern for testing MCP protocol compatibility using golden JSON snapshots"
domain: "testing"
confidence: "high"
source: "manual — designed during protocol compatibility test architecture session"
---

## Context

When building an MCP server that must remain compatible with a reference implementation (VS Code Copilot Chat), protocol drift is the primary risk. The reference implementation updates independently and isn't available in CI. This skill covers how to test protocol compatibility without the reference running.

## Patterns

### Golden Structural Schema Comparison

Store reference JSON responses as snapshot files. Compare using **structural superset checks**:
- Same property names at each level
- Same value types (string/number/boolean/null/array/object)
- Same nesting structure
- Extra properties in actual output are **allowed** (superset OK)
- Missing properties that exist in the golden reference **fail**

This avoids brittle value comparisons while catching structural regressions.

### MCP Handshake Integration Tests

Test the full protocol exchange over real named pipes with mocked backend services:
1. Create `McpPipeServer` with mocked `IVsServiceRpc`
2. Connect via `NamedPipeClientStream` as Copilot CLI would
3. Send HTTP requests (`initialize`, `tools/list`, tool calls)
4. Verify responses match expected protocol shapes
5. Open SSE stream, trigger notification pushes, verify arrival format

### Test Seam for RPC Mocking

Add an `internal` constructor to `RpcClient` accepting a pre-configured `IVsServiceRpc`. Since the test project already has `[InternalsVisibleTo]`, this enables mocked integration tests without a real VS pipe connection.

### Reference Data Management

- Golden JSON snapshots committed to repo in `Snapshots/` directory
- Refreshed manually via proxy capture tool (not automated in CI)
- `Snapshots/VERSION` tracks the VS Code extension version they were captured from
- Monthly refresh cadence, or on major protocol changes

## Examples

```csharp
// Structural superset check (pseudo-code)
void AssertSchemaMatch(JsonElement actual, JsonElement golden)
{
    foreach (var prop in golden.EnumerateObject())
    {
        Assert.True(actual.TryGetProperty(prop.Name, out var actualProp));
        Assert.Equal(prop.Value.ValueKind, actualProp.ValueKind);
        if (prop.Value.ValueKind == JsonValueKind.Object)
            AssertSchemaMatch(actualProp, prop.Value); // recurse
    }
}
```

## Anti-Patterns

- **Live dependency on VS Code in CI** — flaky, slow, requires authentication, blocks on external updates
- **Parsing minified extension.js** — offsets change every build, fragile regex patterns
- **Exact value comparison in snapshots** — breaks on any data difference even when protocol is fine
- **Separate test project for protocol tests** — unnecessary when existing project already has InternalsVisibleTo access and matching target framework
