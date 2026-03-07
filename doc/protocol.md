# Copilot CLI `/ide` Protocol

> Reverse-engineered and validated by
> implementing a compatible extension for a different IDE.
> This document describes the protocol as observed — it is not an official spec.

## Overview

The `/ide` protocol enables [GitHub Copilot CLI](https://githubnext.com/projects/copilot-cli/)
to communicate with a running IDE instance. When a user runs `/ide` in Copilot CLI,
the CLI discovers connected IDEs, displays the user's current editor context
(file, selection, diagnostics), and can propose file edits via a diff view.

The protocol has three layers:

| Layer | Purpose | Mechanism |
|-------|---------|-----------|
| **Discovery** | CLI finds running IDE instances | Lock files in `~/.copilot/ide/` |
| **Transport** | CLI sends requests / receives responses | Streamable HTTP over a named pipe / socket |
| **Application** | Tool calls and push notifications | MCP (Model Context Protocol) with JSON-RPC 2.0 |

```
┌──────────────┐              ┌──────────────┐              ┌──────────────┐
│ Copilot CLI  │◄───MCP──────►│ MCP Server   │◄────────────►│     IDE      │
│ (/ide mode)  │  Streamable  │ (pipe/socket │  (internal)  │  Extension   │
│              │  HTTP        │  listener)   │              │              │
└──────────────┘              └──────────────┘              └──────────────┘
```

The MCP Server can be embedded directly in the IDE extension or run as a separate
child process — the architecture is an implementation choice. What matters is that
the named pipe / socket accepts Streamable HTTP and exposes the correct MCP tools.

---

## 1. Discovery — Lock Files

### Location

```
~/.copilot/ide/{uuid}.lock
```

Each running IDE instance writes a JSON lock file to this directory. The UUID is a
random identifier generated at connection startup. Copilot CLI scans this directory
to discover available IDEs.

### Lock File Schema

```jsonc
{
  "socketPath": "\\\\.\\pipe\\mcp-{guid}.sock",  // Windows named pipe
  // or: "/tmp/mcp-{guid}.sock",                 // Unix domain socket
  "scheme": "pipe",
  "headers": {
    "Authorization": "Nonce {guid}"
  },
  "pid": 12345,
  "ideName": "Your IDE Name",
  "timestamp": 1709836800000,
  "workspaceFolders": [
    "/home/user/my-project"
  ],
  "isTrusted": true
}
```

| Field | Type | Description |
|-------|------|-------------|
| `socketPath` | string | Full path to the named pipe or Unix domain socket the MCP server listens on. On Windows: `\\.\pipe\{name}`. On Unix: a filesystem path. |
| `scheme` | string | Transport scheme. Always `"pipe"`. |
| `headers` | object | HTTP headers the CLI includes on every request. Contains `Authorization: Nonce {guid}` where the GUID is generated per session. |
| `pid` | number | OS process ID of the IDE or MCP server process. Used for stale lock file cleanup. |
| `ideName` | string | Human-readable IDE name (e.g. `"Visual Studio Code"`, `"JetBrains IntelliJ"`, `"Neovim"`). |
| `timestamp` | number | Unix timestamp in milliseconds (UTC) when the lock file was written. |
| `workspaceFolders` | string[] | Absolute paths of open workspace / project folders. |
| `isTrusted` | boolean | Whether the workspace is trusted by the IDE. |

### Lifecycle

- **Created** when a workspace / project opens and the MCP server is ready to accept
  connections.
- **Deleted** when the workspace / project closes or the IDE shuts down.

### Pipe / Socket Name Convention

The reference implementation uses: `mcp-{guid}.sock`

The `.sock` suffix is a naming convention. On Windows the actual transport is a
named pipe (`\\.\pipe\mcp-{guid}.sock`); on Unix it would be a domain socket.

---

## 2. Transport — Streamable HTTP over Named Pipe

Copilot CLI connects to the pipe / socket specified in `socketPath` and speaks
**HTTP/1.1** directly over it. This is the
[Streamable HTTP MCP transport](https://modelcontextprotocol.io/specification/2025-03-26/basic/transports#streamable-http).

### Authentication

Every HTTP request MUST include the `Authorization` header from the lock file:

```
Authorization: Nonce {guid}
```

Requests without a valid nonce receive `401 Unauthorized`.

### HTTP Headers

**Required on all requests:**
- `Authorization: Nonce {guid}` — authentication token from lock file

**Standard headers (POST):**
- `Content-Type: application/json`
- `Content-Length: {bytes}`
- `Mcp-Session-Id: {sessionId}` — associate request with MCP session (optional but recommended)
- `mcp-protocol-version: 2025-11-25` — MCP protocol version (may be sent by CLI)

**Optional informational headers (CLI may send):**
- `X-Copilot-Session-Id` — CLI's internal session UUID
- `X-Copilot-PID` — CLI process ID
- `X-Copilot-Parent-PID` — Parent process ID

Servers should ignore unrecognized headers.

### Endpoints

All operations target a single path: `/mcp` (or `/`).

| Method | Path | Purpose |
|--------|------|---------|
| `POST` | `/mcp` | Send a JSON-RPC request (tool call, initialize, etc.) |
| `GET`  | `/mcp` | Open an SSE stream for server-to-client push notifications |
| `DELETE` | `/mcp` | Terminate the MCP session |

### POST — Tool Calls and Requests

```http
POST /mcp HTTP/1.1
Authorization: Nonce abc123
Content-Type: application/json
Content-Length: 182
Mcp-Session-Id: session-a1b2c3d4
mcp-protocol-version: 2025-11-25

{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"get_selection","arguments":{},"_meta":{"progressToken":1}}}
```

**Response (with result):** HTTP 200 with SSE body:

```http
HTTP/1.1 200 OK
Content-Type: text/event-stream
Mcp-Session-Id: session-a1b2c3d4
Content-Length: ...

event: message
data: {"jsonrpc":"2.0","id":1,"result":{"content":[...]}}
```

**Response (notification, no result):** HTTP 202 Accepted.

**Timeout:** Most tool calls have a 30-second timeout. The `open_diff` tool is exempt
because it blocks until the user accepts or rejects the diff (with a 1-hour ultimate
fallback).

### GET — Server-Sent Events (SSE) Stream

The CLI opens an SSE stream to receive push notifications:

```http
GET /mcp HTTP/1.1
Authorization: Nonce abc123
Mcp-Session-Id: session-a1b2c3d4
```

```http
HTTP/1.1 200 OK
Content-Type: text/event-stream
Cache-Control: no-cache, no-transform
Connection: keep-alive
Mcp-Session-Id: session-a1b2c3d4
Transfer-Encoding: chunked
```

The server sends chunked SSE events for push notifications (see §4). This connection
is kept alive until the session ends or the pipe disconnects.

### DELETE — Session Termination

```http
DELETE /mcp HTTP/1.1
Authorization: Nonce abc123
```

Returns `200 OK` and closes the connection.

### Session ID

The server generates a session ID on the first POST and returns it via the
`Mcp-Session-Id` response header. The client includes this header on subsequent
requests to associate them with the same MCP session. The format of the session
ID is implementation-defined (e.g. a GUID or prefixed string).

---

## 3. Application Layer — MCP Tools

The server exposes MCP tools that Copilot CLI invokes via `tools/call` JSON-RPC
requests. The server MUST advertise itself as:

```json
{
  "name": "vscode-copilot-cli",
  "version": "1.0.0"
}
```

> **Important:** All implementations MUST use this exact server name regardless of
> the actual IDE. The CLI matches on this name.

### Server Capabilities

```json
{
  "tools": {
    "listChanged": true
  }
}
```

### Tool Inventory

The protocol defines **6 tools**. The names and schemas must match exactly for CLI
compatibility.

| Tool | Description |
|------|-------------|
| [`get_vscode_info`](#get_vscode_info) | Get IDE instance information |
| [`get_selection`](#get_selection) | Get the current text selection |
| [`get_diagnostics`](#get_diagnostics) | Get language diagnostics (errors, warnings) |
| [`open_diff`](#open_diff) | Open a diff view with proposed changes |
| [`close_diff`](#close_diff) | Close a diff tab |
| [`update_session_name`](#update_session_name) | Set the CLI session display name |

### Tool Execution Mode

All tools declare `taskSupport: "forbidden"` — they do not support MCP task-based
execution. Tool calls use simple request/response semantics (except `open_diff`
which is long-running but still uses request/response).

### Tool Call Format

Tool calls include a `_meta` object with metadata for streaming and progress tracking:

```json
{
  "method": "tools/call",
  "params": {
    "name": "get_selection",
    "arguments": {},
    "_meta": { "progressToken": 1 }
  },
  "jsonrpc": "2.0",
  "id": 1
}
```

| Field | Description |
|-------|-------------|
| `_meta.progressToken` | Incrementing integer for streaming results. Opaque to the server — handled by the MCP transport layer. |

Servers implementing synchronous tools (non-streaming) can safely ignore `_meta`.

---

### `get_vscode_info`

Returns information about the IDE instance.

> The tool is named `get_vscode_info` even for non-VS Code IDEs — the CLI matches
> on this exact name.

**Parameters:** None

**Response:**

```json
{
  "ideName": "Your IDE Name",
  "appName": "Your IDE Name",
  "version": "1.0.0",
  "processId": 12345
}
```

| Field | Type | Description |
|-------|------|-------------|
| `ideName` | string | IDE identifier (e.g. `"Visual Studio Code"`, `"IntelliJ IDEA"`) |
| `appName` | string | Application display name |
| `version` | string | IDE version string |
| `processId` | number | OS process ID |

Implementations may include additional fields as appropriate for their IDE.

---

### `get_selection`

Returns the current text selection or cursor position in the active editor.

**Parameters:** None

**Response:**

```json
{
  "current": true,
  "filePath": "/home/user/project/src/index.ts",
  "fileUrl": "file:///home/user/project/src/index.ts",
  "text": "selected text here",
  "selection": {
    "start": { "line": 10, "character": 4 },
    "end": { "line": 10, "character": 22 },
    "isEmpty": false
  }
}
```

| Field | Type | Description |
|-------|------|-------------|
| `current` | boolean | `true` if from the active editor, `false` if cached/stale |
| `filePath` | string? | Absolute file path |
| `fileUrl` | string? | `file:///` URI (see [File URI Convention](#5-file-uri-convention)) |
| `text` | string? | Selected text content (empty string if cursor-only) |
| `selection` | SelectionRange? | Position coordinates |

**SelectionRange:** `{ start: Position, end: Position, isEmpty: boolean }`
**Position:** `{ line: number, character: number }` — both 0-based.

---

### `get_diagnostics`

Returns language diagnostics (errors, warnings, information) from the IDE.

**Parameters:**

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `uri` | string | No | File URI to filter by. If omitted, returns diagnostics for all files. |

**Response:** Array of file diagnostic groups:

```json
[
  {
    "uri": "file:///home/user/project/src/index.ts",
    "diagnostics": [
      {
        "message": "Cannot find name 'foo'",
        "severity": "error",
        "range": {
          "start": { "line": 5, "character": 10 },
          "end": { "line": 5, "character": 13 }
        },
        "source": "typescript",
        "code": "2304"
      }
    ]
  }
]
```

| Field | Type | Description |
|-------|------|-------------|
| `uri` | string | File URI |
| `diagnostics[].message` | string | Diagnostic message text |
| `diagnostics[].severity` | string | `"error"`, `"warning"`, or `"information"` |
| `diagnostics[].range` | Range | Location in file (0-based line/character) |
| `diagnostics[].source` | string? | Diagnostic source (e.g. language service name) |
| `diagnostics[].code` | string? | Diagnostic code (e.g. `"2304"`, `"E0001"`) |

---

### `open_diff`

Opens a diff view comparing the original file with proposed new content. **Blocks**
until the user accepts, rejects, or closes the diff tab.

**Parameters:**

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `original_file_path` | string | Yes | Path to the original file |
| `new_file_contents` | string | Yes | The proposed new file content |
| `tab_name` | string | Yes | Display name for the diff tab |

**Response:**

```json
{
  "success": true,
  "result": "SAVED",
  "trigger": "accepted_via_button",
  "tab_name": "refactor-auth",
  "message": "User accepted changes for auth.ts",
  "error": null
}
```

| Field | Type | Description |
|-------|------|-------------|
| `success` | boolean | Whether the diff opened without error |
| `result` | string | Resolution: `"SAVED"` (accepted) or `"REJECTED"` |
| `trigger` | string | What caused the resolution (see table below) |
| `tab_name` | string | The tab name passed in |
| `message` | string? | Human-readable status message |
| `error` | string? | Error message if `success` is false |

**Trigger values:**

| Trigger | Meaning |
|---------|---------|
| `accepted_via_button` | User clicked Accept |
| `rejected_via_button` | User clicked Reject |
| `closed_via_tab` | User closed the diff tab |
| `closed_via_tool` | Another `open_diff` or `close_diff` call replaced this diff |
| `timeout` | Ultimate fallback timeout |

> **Blocking behavior:** The MCP server MUST skip its normal 30-second timeout for
> `open_diff` calls. The tool blocks the HTTP response until the user acts. Copilot
> CLI is expected to handle this long-running call.

---

### `close_diff`

Programmatically closes a diff tab opened by `open_diff`.

**Parameters:**

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `tab_name` | string | Yes | Tab name matching the `open_diff` call |

**Response:**

```json
{
  "success": true,
  "already_closed": false,
  "tab_name": "refactor-auth",
  "message": "Diff \"refactor-auth\" closed and changes rejected",
  "error": null
}
```

| Field | Type | Description |
|-------|------|-------------|
| `success` | boolean | Whether the operation completed |
| `already_closed` | boolean | `true` if no active diff with that tab name was found |
| `tab_name` | string | Echo of the tab name |
| `message` | string? | Status message |
| `error` | string? | Error message if `success` is false |

Closing a diff via this tool signals `REJECTED` with trigger `closed_via_tool` to
any pending `open_diff` call.

---

### `update_session_name`

Sets a display name for the current CLI session.

**Parameters:**

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | Yes | The new session name |

**Response:**

```json
{
  "success": true
}
```

This is a fire-and-forget tool — the server acknowledges but the name may or may
not be surfaced in the IDE's UI depending on the implementation.

---

## 4. Push Notifications

The server pushes real-time notifications to connected SSE clients as JSON-RPC 2.0
notifications. There are **2 notification types**, both recommended to be debounced
at ~200ms.

### `selection_changed`

Sent when the user changes the active file, moves the cursor, or changes the text
selection.

```json
{
  "jsonrpc": "2.0",
  "method": "selection_changed",
  "params": {
    "text": "",
    "filePath": "/home/user/project/src/index.ts",
    "fileUrl": "file:///home/user/project/src/index.ts",
    "selection": {
      "start": { "line": 10, "character": 4 },
      "end": { "line": 10, "character": 4 },
      "isEmpty": true
    }
  }
}
```

| Field | Type | Description |
|-------|------|-------------|
| `text` | string | Selected text (empty string if no selection) |
| `filePath` | string | Absolute file path |
| `fileUrl` | string | File URI (see [File URI Convention](#5-file-uri-convention)) |
| `selection` | SelectionRange | Cursor/selection position (0-based) |

**Debounce:** ~200ms recommended. Only the most recent selection should be sent.
Deduplication (suppressing sends when file + positions haven't changed) is
recommended to avoid redundant notifications.

### `diagnostics_changed`

Sent when diagnostics (errors, warnings) change — typically after builds complete
or documents are saved.

```json
{
  "jsonrpc": "2.0",
  "method": "diagnostics_changed",
  "params": {
    "uris": [
      {
        "uri": "file:///home/user/project/src/index.ts",
        "diagnostics": [
          {
            "range": {
              "start": { "line": 5, "character": 10 },
              "end": { "line": 5, "character": 13 }
            },
            "message": "Cannot find name 'foo'",
            "severity": "error",
            "code": "2304"
          }
        ]
      }
    ]
  }
}
```

| Field | Type | Description |
|-------|------|-------------|
| `uris` | array | Array of `{ uri, diagnostics }` objects |
| `uris[].uri` | string | File URI |
| `uris[].diagnostics` | array | Diagnostics for this file (same schema as `get_diagnostics`) |

**Debounce:** ~200ms recommended.

**Virtual URIs:** IDEs may optionally send diagnostics for virtual URIs (e.g., `git://` scheme
for diff buffers) with empty diagnostics arrays `[]` to indicate the buffer exists but has
no errors. Clients should handle both cases gracefully.

### SSE Wire Format

Push notifications are sent as chunked-encoded SSE events:

```
event: message
data: {"jsonrpc":"2.0","method":"selection_changed","params":{...}}

```

Each event is wrapped in HTTP chunked transfer encoding:
```
{hex-length}\r\n
event: message\ndata: {json}\n\n
\r\n
```

---

## 5. File URI Convention

File paths exchanged between CLI and IDE follow the standard `file:///` URI scheme:

| Platform | File Path | File URI |
|----------|-----------|----------|
| Windows | `C:\Dev\file.ts` | `file:///c%3A/Dev/file.ts` |
| macOS / Linux | `/home/user/file.ts` | `file:///home/user/file.ts` |

Key rules:
- On Windows, drive letter is **lowercase** (`c:` not `C:`)
- On Windows, colon is **percent-encoded** as `%3A`
- Backslashes are converted to forward slashes
- File paths in `filePath` fields use the OS-native format (but lowercase drive
  letter on Windows for consistency)

---

## 6. Sequence Diagrams

### Initial Connection

```
Copilot CLI                    MCP Server                     IDE
    │                               │                          │
    │  scans ~/.copilot/ide/*.lock  │                          │
    │  reads socketPath + headers   │                          │
    │                               │                          │
    │──POST /mcp (initialize)──────►│                          │
    │◄─200 SSE (server capabilities)│                          │
    │                               │                          │
    │──GET /mcp (SSE stream)───────►│                          │
    │◄─200 (chunked SSE)────────────│                          │
    │                               │                          │
    │  (connection established)     │                          │
```

### Tool Call (get_selection)

```
Copilot CLI                    MCP Server                     IDE
    │                               │                          │
    │──POST {"method":"tools/call", │                          │
    │   "params":{"name":           │                          │
    │   "get_selection"}}──────────►│                          │
    │                               │──get selection──────────►│
    │                               │◄─selection data──────────│
    │◄─200 SSE {"result":...}───────│                          │
```

### open_diff (Blocking)

```
Copilot CLI                    MCP Server                     IDE / User
    │                               │                          │
    │──POST {"method":"tools/call", │                          │
    │   "params":{"name":           │                          │
    │   "open_diff",...}}──────────►│                          │
    │                               │──open diff view─────────►│
    │                               │                          │──shows diff + UI
    │                               │       (blocks...)        │
    │       (waits...)              │                          │
    │                               │                          │──user clicks Accept
    │                               │◄─result: SAVED───────────│
    │◄─200 SSE {"result":...}───────│                          │
```

### Push Notification (selection_changed)

```
IDE                            MCP Server                     Copilot CLI
 │                               │                              │
 │──user moves cursor            │                              │
 │──selection changed───────────►│                              │
 │                               │──SSE: selection_changed─────►│
 │                               │                              │──updates display
```

---

## 7. Implementation Guide

### Required Behaviors

1. **Authentication via nonce.** Generate a cryptographically random value for each
   session. Validate it on every HTTP request.

2. **`open_diff` must block.** The MCP server MUST NOT time out on `open_diff` calls.
   Use a mechanism that waits indefinitely (with an optional long fallback timeout)
   for the user to accept or reject.

3. **Debounce push notifications.** Both `selection_changed` and `diagnostics_changed`
   should be debounced at ~200ms to avoid flooding the CLI.

4. **Tool names are immutable.** Tool names like `get_vscode_info` must be used
   exactly as specified. The CLI matches on exact names.

5. **Server name is `"vscode-copilot-cli"`.** Use this exact string in the MCP
   server's `initialize` response regardless of your IDE.

### Protocol Compatibility Checklist

- [ ] Lock file written to `~/.copilot/ide/{uuid}.lock` with all 8 fields
- [ ] Pipe / socket accepts HTTP/1.1 with nonce authentication
- [ ] MCP server name is `"vscode-copilot-cli"`
- [ ] All 6 tools registered with exact names and matching schemas
- [ ] SSE stream serves chunked `text/event-stream` on GET
- [ ] `selection_changed` pushed on editor changes (file switch, cursor move, selection)
- [ ] `diagnostics_changed` pushed when diagnostics change
- [ ] `open_diff` blocks until user action (no premature timeout)
- [ ] File URIs follow the convention (lowercase drive letter, `%3A` colon on Windows)
