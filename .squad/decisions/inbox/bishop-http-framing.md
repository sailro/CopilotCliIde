# HTTP Response Framing: Match VS Code Express Server

**Date:** 2026-03-08
**Author:** Bishop
**Status:** Implemented

## Decision

All HTTP response headers from `McpPipeServer` are now lowercase (matching VS Code's Express server), and POST responses with `text/event-stream` content type use `Transfer-Encoding: chunked` instead of `Content-Length`.

## Rationale

Traffic captures comparing our MCP server against VS Code's showed 3 framing differences. While HTTP headers are case-insensitive per RFC 7230, matching Express's lowercase output byte-for-byte maximizes compatibility with any Copilot CLI parsing that might be case-sensitive in practice.

## Rules Going Forward

1. **All new HTTP response headers must be lowercase.** Do not use PascalCase (`Content-Type`) — use `content-type`.
2. **SSE responses (`text/event-stream`) must use chunked encoding.** Only plain-text error responses (400, 401, 404, etc.) use `Content-Length`.
3. **SSE chunk writes must be atomic.** Combine chunk size + data + trailing CRLF into a single `WriteAsync` call. Never split across multiple writes.

## Affects

- Bishop: Any new HTTP response code in `McpPipeServer`
- Hudson: Test assertions for HTTP headers must use lowercase
