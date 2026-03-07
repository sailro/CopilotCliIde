# Bishop's Capture Analysis — VS Code v0.38 vs v0.39 vs Our Server

**Date**: 2026-03-07
**Analyst**: Bishop (Server Dev)
**Captures**:
- `vscode-0.38.ndjson` — VS Code Stable (Copilot Chat extension v0.38), 38 messages
- `vscode-insiders-0.39.ndjson` — VS Code Insiders (Copilot Chat extension v0.39), 39 messages

---

## 1. Protocol Summary (Both Captures Identical)

### Initialize Response

```json
{
  "protocolVersion": "2025-11-25",
  "capabilities": { "tools": { "listChanged": true } },
  "serverInfo": {
    "name": "vscode-copilot-cli",
    "title": "VS Code Copilot CLI",
    "version": "0.0.1"
  }
}
```

### Tools List — 6 Tools

| # | Tool Name            | Input Properties                                     | Required                                             |
|---|----------------------|------------------------------------------------------|------------------------------------------------------|
| 1 | `get_vscode_info`    | _(none)_                                             | _(none)_                                             |
| 2 | `get_selection`      | _(none)_                                             | _(none)_                                             |
| 3 | `open_diff`          | `original_file_path`, `new_file_contents`, `tab_name`| `original_file_path`, `new_file_contents`, `tab_name`|
| 4 | `close_diff`         | `tab_name`                                           | `tab_name`                                           |
| 5 | `get_diagnostics`    | `uri`                                                | _(none)_                                             |
| 6 | `update_session_name`| `name`                                               | `name`                                               |

All tools carry `"execution": { "taskSupport": "forbidden" }`.
Tools with parameters include `"additionalProperties": false` and `"$schema": "http://json-schema.org/draft-07/schema#"`.

### HTTP Headers

**CLI → VS Code (requests):**
- `X-Copilot-Session-Id`, `X-Copilot-PID`, `X-Copilot-Parent-PID`
- `Authorization: Nonce <uuid>`
- `accept: application/json, text/event-stream` (POST) or `accept: text/event-stream` (GET)
- `content-type: application/json`
- `mcp-protocol-version: 2025-11-25` (after initialize)
- `mcp-session-id: <uuid>` (after initialize)
- `Transfer-Encoding: chunked`

**VS Code → CLI (responses):**
- `X-Powered-By: Express`
- `cache-control: no-cache` (POST tool responses) or `no-cache, no-transform` (SSE GET)
- `content-type: text/event-stream`
- `mcp-session-id: <uuid>`
- `Transfer-Encoding: chunked`
- `Keep-Alive: timeout=5` (on 202 Accepted only)

---

## 2. v0.38 vs v0.39 Comparison

**Result: The two captures are protocol-identical.** No differences in:
- Initialize response (same protocolVersion, capabilities, serverInfo)
- Tools list (same 6 tools, same schemas, same execution settings)
- Notification structure (same fields for selection_changed and diagnostics_changed)
- HTTP headers (same set from both sides)
- Tool response wrapping (all use `content[{type:"text", text:"..."}]`)

The only difference is the data content (different workspaces, different files open). The protocol wire format is identical between v0.38 and v0.39.

---

## 3. Tool Response Structures (from captures)

### `get_selection`

**v0.38 (no editor open):** Returns `null` as the text content.
```json
{ "result": { "content": [{ "type": "text", "text": "null" }] } }
```

**v0.39 (editor open):** Returns JSON object with 5 fields.
```json
{
  "text": "",
  "filePath": "c:\\Dev\\Shellify\\Shellify.Tests\\LibHandlerTest.cs",
  "fileUrl": "file:///c%3A/Dev/Shellify/Shellify.Tests/LibHandlerTest.cs",
  "selection": {
    "start": { "line": 27, "character": 1 },
    "end": { "line": 27, "character": 1 },
    "isEmpty": true
  },
  "current": true
}
```

Fields: `text`, `filePath`, `fileUrl`, `selection` (start/end/isEmpty), `current`

### `update_session_name`

Both captures: `{ "success": true }`

### `get_diagnostics`

**Empty result:** `[]`

**With errors (v0.38):** Array of file objects:
```json
[
  {
    "uri": "file:///c%3A/...",
    "filePath": "c:\\Dev\\...",
    "diagnostics": [
      {
        "message": "The type or namespace name 'rrr' could not be found...",
        "severity": "error",
        "range": {
          "start": { "line": 11, "character": 0 },
          "end": { "line": 11, "character": 3 }
        },
        "code": "CS0246"
      }
    ]
  }
]
```

Diagnostic fields: `message`, `severity`, `range`, `code` — **NO `source` field**.
File-level fields: `uri`, `filePath`, `diagnostics`.

### `get_vscode_info`, `open_diff`, `close_diff`

**Not exercised in either capture.** No wire data available.

---

## 4. Notification Structures (from captures)

### `selection_changed`

```json
{
  "method": "selection_changed",
  "params": {
    "text": "ZipArchive",
    "filePath": "c:\\Dev\\...\\ChangeFileContent.cs",
    "fileUrl": "file:///c%3A/Dev/.../ChangeFileContent.cs",
    "selection": {
      "start": { "line": 10, "character": 12 },
      "end": { "line": 10, "character": 22 },
      "isEmpty": false
    }
  }
}
```

Fields: `text`, `filePath`, `fileUrl`, `selection` (start/end/isEmpty) — **no `current` field** (only tool responses have it).

### `diagnostics_changed`

```json
{
  "method": "diagnostics_changed",
  "params": {
    "uris": [
      {
        "uri": "file:///c%3A/...",
        "diagnostics": [
          {
            "range": { "start": { "line": 11, "character": 0 }, "end": { "line": 11, "character": 3 } },
            "message": "The type or namespace name 'rrr' could not be found...",
            "severity": "error",
            "code": "CS0246"
          }
        ]
      }
    ]
  }
}
```

Fields per URI: `uri`, `diagnostics` — **no `filePath`** at this level.
Diagnostic fields: `range`, `message`, `severity`, `code` — **no `source`**.

---

## 5. Comparison: VS Code vs Our Implementation

### 5.1 ServerInfo Version Mismatch

| Field   | VS Code       | Ours          |
|---------|---------------|---------------|
| name    | `vscode-copilot-cli` | `vscode-copilot-cli` ✅ |
| title   | `VS Code Copilot CLI` | `VS Code Copilot CLI` ✅ |
| version | `0.0.1`       | `1.0.0`       ⚠️ |

**Impact**: Low. The version string is informational, but we should match it for consistency.
**Fix**: Change `Version = "1.0.0"` to `Version = "0.0.1"` in `McpPipeServer.cs:37`.

### 5.2 Extra Tool: `read_file`

Our server exposes 7 tools. VS Code exposes 6. We have an extra `read_file` tool that VS Code doesn't have.

**Impact**: Low risk. Copilot CLI will discover and potentially use the extra tool. Since it's additive, it shouldn't break compatibility. This is intentional per project docs.

### 5.3 `get_selection` — Potential Casing Issue ⚠️

Our `GetSelectionTool` returns the raw `SelectionResult` C# object:
```csharp
return await rpcClient.VsServices!.GetSelectionAsync();
```

`SelectionResult` has PascalCase properties: `Current`, `FilePath`, `FileUrl`, `Text`, `Selection`.

VS Code returns camelCase: `current`, `filePath`, `fileUrl`, `text`, `selection`.

**Impact**: **CRITICAL if the MCP .NET SDK uses PascalCase serialization.** The Copilot CLI parses these tool responses and expects camelCase field names. If the SDK uses its own camelCase policy (likely, since JSON-RPC is camelCase), this is fine. If it uses default System.Text.Json (PascalCase), every field name is wrong.

**Action needed**: Verify what the MCP .NET SDK serializer does. If PascalCase, fix by either:
- (a) Returning an anonymous object with explicit camelCase names (like OpenDiffTool does), or
- (b) Adding `[JsonPropertyName]` attributes to `SelectionResult`.

### 5.4 `get_selection` — Null Handling

VS Code returns `"null"` as text when no editor is active. Our implementation returns whatever `GetSelectionAsync()` gives us (likely a `SelectionResult` with null fields, which would serialize as `{"current":false,"filePath":null,...}` — different from literal `"null"`).

**Impact**: Medium. Copilot CLI may check for `"null"` text specifically.
**Action needed**: Consider matching VS Code's null behavior.

### 5.5 `get_vscode_info` — Potential Casing Issue ⚠️

Same concern as `get_selection`. Our tool returns `VsInfoResult` directly with PascalCase properties: `IdeName`, `AppName`, `Version`, `SolutionPath`, etc.

VS Code's response wasn't captured, so we can't verify the exact field names. But by convention, VS Code uses camelCase everywhere.

**Impact**: Same as 5.3 — depends on SDK serializer behavior.

### 5.6 `get_diagnostics` — `source` Field Difference

Our `DiagnosticItem` DTO has a `Source` field. VS Code's diagnostics (both in tool responses and notifications) do **not** include a `source` field.

**Impact**: Low. Extra fields shouldn't break parsing. But it's noise.
**Action needed**: Consider omitting `Source` when null, or removing it from the tool response serialization.

### 5.7 `get_diagnostics` — Potential Casing Issue ⚠️

Our `GetDiagnosticsTool` returns `result.Files` (a `List<FileDiagnostics>`) directly. The DTO properties are PascalCase: `Uri`, `FilePath`, `Diagnostics`, and nested `Message`, `Severity`, `Range`, `Code`, `Source`.

VS Code returns camelCase: `uri`, `filePath`, `diagnostics`, `message`, `severity`, `range`, `code`.

**Impact**: Same casing concern as 5.3/5.5.

### 5.8 `open_diff` / `close_diff` — Looks Correct ✅

These tools construct explicit anonymous objects with snake_case/lowercase names matching VS Code's convention:
```csharp
return new { success = ..., result = ..., trigger = ..., tab_name = ..., message = ..., error = ... };
```

Not captured in traffic, but the field names look correct by convention.

### 5.9 `update_session_name` — Looks Correct ✅

Returns `new { success = true }` which matches VS Code's `{"success": true}`.

### 5.10 Notifications — Look Correct ✅

Our `PushSelectionChangedAsync` and `PushDiagnosticsChangedAsync` construct anonymous objects with explicit camelCase names. Structure matches VS Code exactly:
- `selection_changed`: `text`, `filePath`, `fileUrl`, `selection` (start/end/isEmpty) ✅
- `diagnostics_changed`: `uris` → `uri`, `diagnostics` → `range`, `message`, `severity`, `code` ✅

### 5.11 HTTP Response Differences (Minor)

| Aspect | VS Code | Ours |
|--------|---------|------|
| Body encoding | `Transfer-Encoding: chunked` | `Content-Length: N` |
| `X-Powered-By` | `Express` | _(absent)_ |
| `cache-control` | `no-cache` / `no-cache, no-transform` | _(absent on POST)_ |
| `Date` header | Present | _(absent)_ |
| `Keep-Alive` | `timeout=5` on 202 | _(absent)_ |

**Impact**: Very low. HTTP framing differences shouldn't matter since the CLI reads the body correctly either way.

---

## 6. Test Opportunities

From these captures, we can now write integration tests that replay exact VS Code traffic:

### New Tests to Add

1. **Initialize handshake** — send `initialize` request, verify response matches VS Code's protocolVersion, capabilities, serverInfo.
2. **Tools list** — verify we return the same 6 tools (+ read_file) with correct schemas, including `execution.taskSupport`, `additionalProperties`, `$schema`.
3. **get_selection with null** — verify behavior when no editor is active (should return `"null"` text).
4. **get_selection with data** — verify response has `text`, `filePath`, `fileUrl`, `selection` (start/end/isEmpty), `current` — all camelCase.
5. **get_diagnostics empty** — verify returns `[]`.
6. **get_diagnostics with errors** — verify array structure with `uri`, `filePath`, `diagnostics` containing `message`, `severity`, `range`, `code` (no `source`).
7. **update_session_name** — verify `{"success": true}`.
8. **selection_changed notification** — verify notification params structure.
9. **diagnostics_changed notification** — verify notification params structure.
10. **Full session replay** — replay an entire capture file against our server and compare responses.

---

## 7. Action Items (Priority Order)

### P0 — Verify Immediately
- [ ] **Serialization casing**: Test whether the MCP .NET SDK serializes `SelectionResult`/`VsInfoResult`/`FileDiagnostics` with camelCase or PascalCase. If PascalCase, this is a **breaking compatibility bug** affecting `get_selection`, `get_vscode_info`, and `get_diagnostics`.

### P1 — Should Fix
- [ ] **get_selection null handling**: When no editor is active, consider returning literal `null` to match VS Code behavior instead of a serialized object with null fields.
- [ ] **DiagnosticItem.Source**: Either suppress this field when null or remove it from tool response serialization to avoid extra noise.
- [ ] **serverInfo.version**: Change from `"1.0.0"` to `"0.0.1"` for consistency.

### P2 — Nice to Have
- [ ] Add `cache-control: no-cache` header to POST tool responses.
- [ ] Consider using `Transfer-Encoding: chunked` instead of `Content-Length` for POST responses (matches VS Code behavior).

### P3 — Documentation
- [ ] `read_file` is intentionally extra — document this in test expectations.
- [ ] `get_vscode_info` and `open_diff`/`close_diff` wire formats are unknown — need separate capture sessions exercising these tools.
