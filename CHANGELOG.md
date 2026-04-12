# Changelog

All notable changes to the CopilotCliIde Visual Studio extension are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Added

- **Embedded Copilot CLI terminal** — dockable tool window (**Tools → Copilot CLI (Embedded Terminal)**) that hosts Copilot CLI directly inside Visual Studio with full ANSI color support, interactive prompts, and resize handling ([PR #7](https://github.com/sailro/CopilotCliIde/pull/7) by @bommerts)

### Fixed

- Fix multi-byte UTF-8 character corruption in terminal output (emoji, CJK)
- Fix terminal focus recovery after F5 debug cycles
- Fix terminal session thread safety for concurrent start/stop calls
- Add graceful fallback when WebView2 runtime is unavailable
- Add `ResizeObserver` for terminal resize on dock panel splitter drags

### Changed

- Rename menu items for clarity: **Launch Copilot CLI (External Terminal)** and **Show Copilot CLI (Embedded Terminal in VS)**

## [1.0.15] - 2026-03-31

### Fixed

- Fix MCP server crash when VS solution folder contains `appsettings.json` with Kestrel HTTPS configuration ([#4](https://github.com/sailro/CopilotCliIde/issues/4))

### Test

- Add regression tests for server WorkingDirectory isolation (`ServerWorkingDirectoryTests`)

## [1.0.14] - 2026-03-30

### Changed

- Replace custom HTTP/MCP stack with `ModelContextProtocol.AspNetCore` (Kestrel named-pipe transport)
- Declare `TaskSupport = Forbidden` per-tool via `[McpServerTool]` attributes (VSCode parity)

### Fixed

- Fix reset-notification session gating (initial state push)

### Test

- Add VS capture session (v1.0.14) with capture-driven replay tests
- Add `execution.taskSupport` parity assertions for all tools
- Refine SSE session handling and tooling in test infrastructure

### Build

- Fix release workflow to prevent removing the related tag on delete release

## [1.0.13] - 2026-03-28

### Changed

- Extract shared `DiagnosticSeverity` contract into CopilotCliIde.Shared
- Split `VsServiceRpc` into smaller focused classes
- Extract notification handling from extension package
- Refactor extension and server with OOP patterns

### Test

- Update `CrossCaptureConsistencyTests` for multi-VS-capture support
- Add new VS Code capture session (v0.41)

### Docs

- Document Tools → Launch Copilot CLI menu command in README
- Align protocol docs with capture parity tests

### Build

- Update GitHub Actions to Node.js 24-compatible versions

## [1.0.12] - 2026-03-28

### Added

- **Tools → Launch Copilot CLI** menu command to start Copilot CLI from within Visual Studio ([PR #3](https://github.com/sailro/CopilotCliIde/pull/3) by @andysterland)

### Changed

- Suppress additional compiler warnings (`nowarn`)

## [1.0.11] - 2026-03-26

_Re-tagged from 1.0.10 — marketplace publishing failed, creating an invalid payload._

## [1.0.10] - 2026-03-26

### Fixed

- Fix VS2022 compatibility by downgrading StreamJsonRpc to 2.22 ([PR #2](https://github.com/sailro/CopilotCliIde/pull/2))

## [1.0.9] - 2026-03-10

### Added

- GitHub Actions CI workflow (`ci.yml`)
- GitHub Actions release workflow (`release.yml`)
- Build status badge in README

### Build

- Refactor build project structure

## [1.0.8] - 2026-03-10

### Fixed

- Fix diagnostic `range.end` always being `(0,0)` by using `PersistentSpan`
- Fix `get_selection` column reporting accuracy
- Log format consistency for separator and position accuracy

### Changed

- Sync protocol responses with VS Code reference captures

### Test

- Rewrite `CrossCaptureConsistencyTests` to strict mode

## [1.0.7] - 2026-03-08

### Added

- Live diagnostics push via `ITableDataSink` subscription
- `DiagnosticTracker` extracted from package (mirrors `SelectionTracker` pattern)
- PipeProxy tool for capturing MCP traffic between Copilot CLI and VS Code
- Proxy-based protocol compatibility tests with strict unknown tool/notification detection
- VS Code Insiders protocol capture for validation

### Fixed

- Fix `DiagnosticTracker` analyzer warnings (VSSDK007, IDE0028, IDE0031)
- Fix path/uri parameter consistency in tool calls
- Fix protocol compatibility issues found via capture comparison

### Changed

- Align HTTP response framing with VS Code Express server
- Replace golden snapshot tests with real VS Code extension captures (v0.39)
- Update RPC contracts for diagnostics support
- Align `close_diff` message format

## [1.0.6] - 2026-03-07

### Added

- Diagnostics deduplication via `HashCode` fingerprint
- Push initial selection and diagnostics on CLI connect with dedup reset
- `ErrorListReader` to consolidate diagnostics collection logic
- Whitespace format enforcement via husky pre-commit hook
- Unit tests (xUnit v3 migration)
- `Directory.Build.props` for centralized build configuration
- Protocol documentation from reverse-engineering sessions

### Fixed

- Fix URI format inconsistency — use `PathUtils` everywhere
- Fix selection log to display 1-based line/column numbers

### Changed

- Replace file-based logging with VS Output Window pane ("Copilot CLI IDE")
- Extract `SelectionTracker`, `DebouncePusher`, `PathUtils` from package
- Centralize severity mapping into shared utility
- Cache `IVsMonitorSelection`, remove UI thread assert from `Dispose`
- Bump dependencies

### Docs

- Update README to match current wire formats and notifications
- Add Diagnostics section to README for Output Window logging

## [1.0.5] - 2026-03-05

### Changed

- Replace DTE events with native VS editor APIs (`IVsMonitorSelection`, `IWpfTextView`) for selection tracking
- Replace debounce with synchronous selection reads
- Push current selection when Copilot CLI SSE client connects
- Simplify `PushCurrentSelection` — remove `BufferGraph` mapping and deferred re-push

### Fixed

- Fix initial selection push on lazy load and clear on tab close
- Fix active view tracking on document tab switch
- Fix stale selection positions during mouse operations
- Fix out-of-order selection notifications during mouse drag

## [1.0.4] - 2026-03-05

### Changed

- Tear down and recreate connection on solution close/open (solution lifecycle management)

### Docs

- Update README to document solution lifecycle behavior
- Add Copilot instructions and code review instructions

## [1.0.3] - 2026-03-04

### Added

- Blocking diff workflow with Accept/Reject InfoBar and `TaskCompletionSource`
- SSE selection change notifications pushed to Copilot CLI
- VS Code protocol alignment for tool schemas

### Fixed

- Fix VSTHRD010 warnings in InfoBar event handlers
- Fix analyzer warnings and IDE messages

### Docs

- Update README with SSE notifications, architecture diagram, and VS Code compatibility notes

## [1.0.2] - 2026-03-03

_Version bump only for marketplace publishing tests — no functional changes._

## [1.0.1] - 2026-03-03

### Changed

- Adjust RPC contracts to match VS Code Copilot Chat extension exactly

### Fixed

- Track solution changes in lock file and fix multi-instance log conflicts

### Docs

- Update README with screenshots and functionality details

## [1.0.0] - 2026-03-02

### Added

- Initial release of the CopilotCliIde Visual Studio extension
- Two-process architecture: VS extension (`net472`) + MCP server (`net10.0`) over named pipes
- HTTP-over-pipe implementation for MCP Streamable HTTP transport with chunked transfer encoding
- All 7 MCP tools matching VS Code's Copilot Chat extension:
  - `get_vscode_info` — IDE and workspace metadata
  - `get_selection` — Current editor selection with file context
  - `open_diff` — Open diff viewer with proposed changes
  - `close_diff` — Close diff viewer and return result
  - `get_diagnostics` — Retrieve errors/warnings from the Error List
  - `read_file` — Read file contents from the workspace
  - `update_session_name` — Set the Copilot CLI session display name
- `SelectionTracker` for real-time editor selection capture via DTE events
- Lock file discovery mechanism (`~/.copilot/ide/*.lock`) for Copilot CLI to find running VS instances
- Comprehensive README with usage instructions, architecture diagram, and tool documentation

[Unreleased]: https://github.com/sailro/CopilotCliIde/compare/1.0.15...HEAD
[1.0.15]: https://github.com/sailro/CopilotCliIde/compare/1.0.14...1.0.15
[1.0.14]: https://github.com/sailro/CopilotCliIde/compare/1.0.13...1.0.14
[1.0.13]: https://github.com/sailro/CopilotCliIde/compare/1.0.12...1.0.13
[1.0.12]: https://github.com/sailro/CopilotCliIde/compare/1.0.11...1.0.12
[1.0.11]: https://github.com/sailro/CopilotCliIde/compare/1.0.10...1.0.11
[1.0.10]: https://github.com/sailro/CopilotCliIde/compare/1.0.9...1.0.10
[1.0.9]: https://github.com/sailro/CopilotCliIde/compare/1.0.8...1.0.9
[1.0.8]: https://github.com/sailro/CopilotCliIde/compare/1.0.7...1.0.8
[1.0.7]: https://github.com/sailro/CopilotCliIde/compare/1.0.6...1.0.7
[1.0.6]: https://github.com/sailro/CopilotCliIde/compare/1.0.5...1.0.6
[1.0.5]: https://github.com/sailro/CopilotCliIde/compare/1.0.4...1.0.5
[1.0.4]: https://github.com/sailro/CopilotCliIde/compare/1.0.3...1.0.4
[1.0.3]: https://github.com/sailro/CopilotCliIde/compare/1.0.2...1.0.3
[1.0.2]: https://github.com/sailro/CopilotCliIde/compare/1.0.1...1.0.2
[1.0.1]: https://github.com/sailro/CopilotCliIde/compare/1.0.0...1.0.1
[1.0.0]: https://github.com/sailro/CopilotCliIde/releases/tag/1.0.0
