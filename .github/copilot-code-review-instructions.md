# Copilot Code Review Instructions

## What to Focus On

### Threading Safety (Critical)
- Any code accessing DTE, `IVsWindowFrame`, `IVsInfoBarHost`, or other COM services **must** be on the UI thread. Check for `ThreadHelper.ThrowIfNotOnUIThread()` or `SwitchToMainThreadAsync()`.
- Background tasks that touch VS services without switching to the main thread first are bugs.

### Connection Lifecycle
- `StopConnection()` must fully tear down all resources (CTS, RPC, pipe, server process, lock file) before `StartConnectionAsync()` creates new ones.
- Lock files must be removed when the solution closes — orphaned lock files cause Copilot CLI to attempt connections to dead pipes.

### Named Pipe Cleanup
- Every `NamedPipeServerStream` or `NamedPipeClientStream` must be disposed on all code paths (including cancellation and exceptions).
- The MCP server process monitors stdin — when the parent (VS) closes stdin, the server exits. Verify this contract is maintained.

### MCP Protocol Compatibility
- Tool names, parameter names, and response shapes must match VS Code's Copilot Chat extension exactly. Breaking these breaks the Copilot CLI `/ide` protocol.
- All tools must include `execution.taskSupport: "forbidden"` metadata.

### Error Handling
- Never let exceptions from pipe operations, RPC calls, or COM interop propagate to VS event handlers — always catch and log.
- `OperationCanceledException` and `ObjectDisposedException` are expected during shutdown — catch them silently.

## What to Ignore

- The `IsExternalInit.cs` file exists only to enable `init` accessors on `net472`. Do not flag it.
- `#pragma warning disable VSTHRD003` on `_rpc.Completion` is intentional — it's a long-running task representing the RPC lifetime.
- `catch { /* Ignore */ }` blocks are intentional for non-critical operations (pipe disconnects, cleanup during shutdown).
- Tool names like `get_vscode_info` are deliberately named to match VS Code — do not suggest renaming.
