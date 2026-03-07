# Proxy-Based Protocol Compatibility Testing

**Author:** Ripley (Lead)  
**Date:** 2026-03-07  
**Status:** Proposed  
**Supersedes:** "Protocol Compatibility Test Architecture" (golden snapshot approach)  
**Requested by:** Sebastien

## Why the Previous Approach Was Wrong

The golden snapshot architecture was circular. We extracted schemas from our own code and VS Code's minified `extension.js`, wrote them as JSON files, then tested our code against them. That tests consistency with ourselves, not compatibility with the real protocol.

Sebastien's directive: **"I want tests really using vscode-insiders. Using a named pipe proxy to capture traffic."**

## Decision

**Build a named pipe proxy tool that intercepts real Copilot CLI ↔ VS Code traffic. Use captured traffic as the ground truth for compatibility testing.**

No golden snapshots derived from source code. No extension.js decompilation. The wire is the spec.

## Architecture: The Proxy

### What It Is

A C# console app (`net10.0`) that sits between Copilot CLI and VS Code Insiders on the named pipe.

```
Copilot CLI ──(HTTP-on-pipe)──→ PipeProxy ──(HTTP-on-pipe)──→ VS Code Insiders
             ←─────────────────           ←─────────────────
                                    │
                                    ▼
                              traffic.ndjson
```

### Why C#

- Reuses `ReadHttpRequestAsync`, `WriteHttpResponseAsync`, `ReadChunkedBodyAsync` from `McpPipeServer.cs` — these are already `internal static`, tested with 100+ test cases, and handle all the HTTP-on-pipe edge cases (chunked encoding, SSE streams, header parsing).
- Same named pipe APIs (`NamedPipeServerStream`, `NamedPipeClientStream`) already proven in production.
- Same lock file format code already exists in `IdeDiscovery.cs`.
- No new runtime dependency. Team already knows C#.

Node.js or PowerShell would mean reimplementing HTTP-on-pipe parsing from scratch. That's busywork, not engineering.

### Where It Lives

```
tools/
  PipeProxy/
    PipeProxy.csproj          ← net10.0 console app
    Program.cs                ← Entry point, CLI args, orchestration
    ProxyRelay.cs             ← Bidirectional pipe relay with logging
    LockFileManager.cs        ← Read VS Code locks, write proxy lock, cleanup
    TrafficLogger.cs          ← NDJSON structured logging
```

**Not** in `src/` — this is a developer/test tool, not shipped in the VSIX. **Not** a script — proper named pipe handling and HTTP parsing require real async code.

The project references `CopilotCliIde.Server` to access the HTTP parsing helpers (they're `internal static`, already `[InternalsVisibleTo]` pattern established). Alternative: extract helpers to Shared if the reference feels too heavy.

### How It Works

**Step 1: Find VS Code**

Scan `~/.copilot/ide/*.lock` files. Find one where `ideName` contains `"Code"` and `pid` is alive. Extract `socketPath` (the real pipe) and `headers.Authorization` (the nonce).

**Step 2: Hijack Discovery**

Create a new named pipe: `mcp-proxy-{uuid}.sock`. Write a proxy lock file to `~/.copilot/ide/` with:
- `socketPath` → proxy's pipe
- `headers.Authorization` → same nonce as VS Code's (so CLI auth works)
- `pid` → proxy's PID
- `ideName` → `"Visual Studio Code - Insiders"` (same as VS Code's, so CLI picks it)
- `workspaceFolders`, `isTrusted`, etc. → copied from VS Code's lock file

**Problem:** Copilot CLI might pick VS Code's original lock file instead of ours.
**Solution:** Temporarily rename VS Code's lock file (e.g., append `.proxy-hidden`). Restore on exit. Crude but effective. Alternative: give the proxy lock a newer `timestamp`.

**Step 3: Relay Traffic**

When Copilot CLI connects to the proxy pipe:

1. **POST /mcp** (tool calls, initialize, etc.):
   - Read full HTTP request from CLI
   - Log it (direction: `cli→vscode`)
   - Open connection to VS Code's real pipe, forward the request
   - Read VS Code's HTTP response
   - Log it (direction: `vscode→cli`)
   - Forward response back to CLI

2. **GET /mcp** (SSE stream):
   - Read GET request from CLI
   - Log it
   - Open GET to VS Code's pipe, forward request
   - Relay SSE events in real-time from VS Code → CLI, logging each event
   - This is a long-lived connection — use async streaming relay

3. **DELETE /mcp** (disconnect):
   - Forward, log, relay response

**Step 4: Clean Up**

On exit (Ctrl+C or pipe disconnect):
- Delete proxy lock file
- Restore VS Code's original lock file (if renamed)
- Flush and close log file

### Log Format: NDJSON

One JSON object per line. Structured, machine-parseable, `grep`-friendly.

```jsonl
{"ts":"2026-03-07T12:34:56.789Z","seq":1,"dir":"cli_to_vscode","type":"request","http":{"method":"POST","path":"/mcp","headers":{"Content-Type":"application/json","Authorization":"Nonce abc123"}},"body":{"jsonrpc":"2.0","id":1,"method":"initialize","params":{...}}}
{"ts":"2026-03-07T12:34:56.812Z","seq":2,"dir":"vscode_to_cli","type":"response","http":{"statusCode":200,"headers":{"Content-Type":"text/event-stream","Mcp-Session-Id":"..."}},"body":{"jsonrpc":"2.0","id":1,"result":{"serverInfo":{"name":"vscode-copilot-cli"},...}}}
{"ts":"2026-03-07T12:34:57.100Z","seq":3,"dir":"cli_to_vscode","type":"request","http":{"method":"GET","path":"/mcp","headers":{...}},"body":null}
{"ts":"2026-03-07T12:35:01.500Z","seq":4,"dir":"vscode_to_cli","type":"sse_event","event":"message","data":{"jsonrpc":"2.0","method":"notifications/message","params":{"method":"selection_changed","params":{...}}}}
```

Fields:
- `ts` — ISO 8601 timestamp with milliseconds
- `seq` — monotonic sequence number (for ordering)
- `dir` — `cli_to_vscode` or `vscode_to_cli`
- `type` — `request`, `response`, `sse_event`
- `http` — parsed HTTP metadata (method, path, status code, relevant headers)
- `body` — parsed JSON body (not raw bytes — we want queryable structure)
- `event`/`data` — for SSE events, the event type and parsed data

### CLI Usage

```bash
# Basic capture — finds VS Code automatically, logs to stdout
dotnet run --project tools/PipeProxy -- capture

# Capture to file
dotnet run --project tools/PipeProxy -- capture --output traffic.ndjson

# Capture with verbose console output (show parsed traffic in real-time)
dotnet run --project tools/PipeProxy -- capture --output traffic.ndjson --verbose
```

## How This Feeds Into Testing

### Phase 1: Capture & Manual Review (No Automation)

Run the proxy while using `copilot /ide` interactively. Inspect the NDJSON output to:
- Understand VS Code's exact wire format
- Discover protocol details missed during reverse engineering
- Build intuition before writing tests

This alone has value. The previous reverse engineering was reading minified JS. This is reading the actual wire protocol.

### Phase 2: Replay-Based Comparison Tests

A test class in `CopilotCliIde.Server.Tests` that:

1. Reads a captured NDJSON traffic file (committed to repo under `Captures/`)
2. Extracts request→response pairs grouped by MCP method (`initialize`, `tools/list`, `tools/call` per tool)
3. Starts our `McpPipeServer` on a test pipe with mocked `IVsServiceRpc`
4. Sends the same requests to our server
5. Compares our responses against VS Code's responses **structurally**

**Structural comparison rules:**
- VS Code response has field X → our response MUST have field X (superset OK)
- VS Code field X is type T → our field X must be same type
- VS Code nests `{start: {line, character}}` → we must nest the same way
- Values can differ (different workspace, different content — that's expected)
- Extra fields in our response → OK (e.g., `read_file` tool)

```
Captures/
  README.md
  vscode-insiders-0.39.ndjson     ← traffic captured from VS Code Insiders v0.39
```

**Test file:** `TrafficReplayTests.cs`

```csharp
[Fact]
public async Task OurToolsListMatchesVsCodeStructure()
{
    var vsCodeResponse = TrafficParser.ExtractResponse("tools/list", "captures/vscode-insiders-0.39.ndjson");
    var ourResponse = await SendToOurServer("tools/list", vsCodeResponse.Request);
    SchemaComparer.AssertStructuralMatch(vsCodeResponse.Body, ourResponse.Body);
}
```

### Phase 3 (Optional): Live Dual-Target Comparison

This is the "no snapshots at all" variant Sebastien hinted at. Instead of replaying captured traffic:

1. Proxy sends each CLI request to BOTH VS Code and our server simultaneously
2. Compares responses in real-time
3. Reports mismatches immediately

**Pros:** No captured traffic files to maintain. Always tests against latest VS Code.
**Cons:** Requires VS Code Insiders running. Can't run in CI. Our server needs a mock VS backend running simultaneously.

**Verdict:** Design the proxy to support this as a future mode (`--compare`), but don't build it yet. Phase 2 replay tests give 90% of the value with CI-friendly determinism.

### What Happens to Existing Golden Snapshots

The 8 JSON files in `Snapshots/` and `ProtocolCompatibilityTests.cs` stay until Phase 2 replay tests replace them. They're not wrong — they test real things — they're just not authoritative. Once we have captured traffic, the replay tests supersede them and the golden files can be deleted.

## Phased Rollout

| Phase | What | Who | Effort | Depends On |
|-------|------|-----|--------|------------|
| **1** | Build PipeProxy tool (capture mode only) | Bishop | ~6h | Nothing |
| **2** | First capture session — run proxy with VS Code Insiders, capture tool calls | Any dev | ~1h | Phase 1 |
| **3** | TrafficParser + SchemaComparer utilities | Hudson | ~3h | Phase 2 (needs captured data) |
| **4** | Replay comparison tests | Hudson | ~4h | Phase 3 |
| **5** | (Future) Live dual-target `--compare` mode | Bishop | ~4h | Phase 4 |

**Total to first useful output (capture tool):** ~6h  
**Total to automated tests:** ~14h

## Key Design Decisions

### 1. C# over Node.js / PowerShell

Not debatable. We have production-tested HTTP-on-pipe parsing in C#. Reimplementing it is waste.

### 2. Standalone tool over test fixture

The proxy must run interactively with a human driving Copilot CLI. It can't be a test fixture that runs headless in CI (Copilot CLI needs authentication, VS Code needs a workspace open, the user needs to trigger actions). Capture is manual; replay is automated.

### 3. NDJSON over raw bytes or SQLite

- Raw bytes: not queryable, requires another parser
- SQLite: overkill for sequential log data, harder to diff/commit
- NDJSON: one line per event, `grep`-able, parseable with `System.Text.Json`, diffable in git, streamable

### 4. Committed captures over generated-on-demand

Captured traffic is committed to the repo as test data. This is NOT the same as golden snapshots derived from source code — these are real wire captures from a real VS Code instance. They serve as regression anchors. Re-captured when VS Code Insiders ships a significant update.

### 5. Structural comparison over value comparison

We compare schema shape (field names, types, nesting), not values. Our `get_vscode_info` returns `"Visual Studio"` where VS Code returns `"Visual Studio Code"`. That's expected. But if VS Code returns `{selection: {start: {line: 1}}}` and we return `{startLine: 1}`, that's a structural mismatch the test catches.

## Risks

| Risk | Mitigation |
|------|------------|
| VS Code's lock file discovery race (CLI picks VS Code's lock instead of proxy's) | Rename VS Code's lock temporarily. Restore on exit. |
| SSE relay complexity (long-lived chunked streams) | Reuse existing chunked body parsing. Async streaming relay with cancellation. |
| Copilot CLI auth might not work through proxy | Proxy copies the exact nonce. If CLI validates server identity beyond nonce, we'll discover it during Phase 2. |
| Captured traffic becomes stale | Monthly refresh, same as the golden snapshot refresh cadence. But now it's running one command, not reading minified JS. |

## What This Replaces

- **Golden snapshot approach** from `.squad/decisions.md` "Protocol Compatibility Test Architecture" — superseded. The approach was sound but the data source was wrong.
- **Bishop's extension.js extraction** from `.squad/decisions/inbox/bishop-golden-snapshots-from-vscode.md` — superseded. Still fragile, still not real wire traffic.
- **Existing `Snapshots/*.json` files** — kept temporarily, replaced when replay tests are ready.

## Summary

The proxy is a small, focused tool that gives us the one thing we couldn't get before: ground truth. Everything else — test infrastructure, comparison logic, CI integration — builds on top of real captured traffic. No more arguing about whether our reference data is accurate. It came off the wire.
