# Changelog

All notable changes to the CopilotCliIde Visual Studio extension are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

## [1.0.20] - 2026-04-28

### Fixed

- Fixed a crash that could occur when closing a solution or shutting down Visual Studio while Copilot CLI was connected.

## [1.0.19] - 2026-04-26

### Added

- **Configurable external terminal.** Pick your own terminal for **Tools → Launch Copilot CLI (External Terminal)** under **Settings → Copilot CLI IDE Bridge → External Terminal**. Use Windows Terminal, PowerShell, pwsh, or any other shell — defaults stay the same if you don't change anything.
- Use `{WorkspaceFolder}` in the command or arguments to inject the current solution directory at launch (handy for terminals like Windows Terminal that don't inherit the working directory).
- The Output pane now logs the full command, arguments, and working directory each time you launch the external terminal — useful when tuning a custom configuration.

### Changed

- Renamed the existing **Terminal** settings category to **Embedded Terminal** so it's clear which terminal you're configuring.
- Faster, more reliable startup when Copilot CLI connects.

### Fixed

- Diff views from a previous solution no longer linger after you switch solutions.
- Selection and diagnostic notifications no longer stack up under rapid edits.
- Eliminated a rare race condition during shutdown that could cause a crash.

## [1.0.18] - 2026-04-13

### Added

- **Embedded terminal font settings.** Choose your font family and size for the embedded Copilot CLI terminal under **Settings → Copilot CLI IDE Bridge → Terminal**. The font dropdown lists installed monospace fonts and accepts any name you type.

### Changed

- The embedded terminal now uses the same native terminal control that powers Windows Terminal and Visual Studio's built-in terminal. Result: crisper text, perfect box-drawing characters, and no Chromium dependency.

### Fixed

- The embedded terminal now auto-focuses when you open it — no click needed before typing.
- Pressing Escape no longer steals focus away from the embedded terminal.
- Terminal redraws cleanly when restarting a session.
- Resolved a Windows-Terminal compatibility issue that affected some Visual Studio installations.

## [1.0.17] - 2026-04-12

### Fixed

- The embedded terminal's right-click menu no longer shows the browser context menu — right-click pastes, as expected.
- Hidden the duplicate scrollbar in the embedded terminal so it matches an external terminal's appearance.
- Embedded terminal colors now match Windows Terminal's brightness.
- Smoother selection and focus handling in the embedded terminal.

## [1.0.16] - 2026-04-12

### Added

- **Embedded Copilot CLI terminal.** A dockable tool window (**Tools → Show Copilot CLI (Embedded Terminal)**) hosts Copilot CLI directly inside Visual Studio with full color, interactive prompts, and resizing ([PR #7](https://github.com/sailro/CopilotCliIde/pull/7) by @bommerts).

### Changed

- Clearer menu names: **Launch Copilot CLI (External Terminal)** and **Show Copilot CLI (Embedded Terminal)**.

### Fixed

- Emoji and CJK characters now display correctly in the embedded terminal.
- The terminal recovers focus after F5 debug cycles.
- The terminal redraws correctly when its tab becomes visible again or when the solution reloads.
- The embedded terminal stays alive when you close a solution and restarts in the new working directory when you open another.
- Friendly fallback if the WebView2 runtime isn't installed on the machine (no longer crashes).
- The terminal now resizes correctly when you drag dock-panel splitters.

## [1.0.15] - 2026-03-31

### Fixed

- Fixed a startup crash when the solution folder contained an `appsettings.json` configuring Kestrel HTTPS ([#4](https://github.com/sailro/CopilotCliIde/issues/4)).

## [1.0.14] - 2026-03-30

### Changed

- Internal MCP plumbing modernized for better protocol parity with VS Code's Copilot Chat extension.

### Fixed

- The initial selection / diagnostics push now arrives reliably right after Copilot CLI connects.

## [1.0.13] - 2026-03-28

### Docs

- README now documents the **Tools → Launch Copilot CLI** menu command.

## [1.0.12] - 2026-03-28

### Added

- **Tools → Launch Copilot CLI** menu command to start Copilot CLI from inside Visual Studio ([PR #3](https://github.com/sailro/CopilotCliIde/pull/3) by @andysterland).

## [1.0.11] - 2026-03-26

_Re-tagged from 1.0.10 — marketplace publishing failed, creating an invalid payload._

## [1.0.10] - 2026-03-26

### Fixed

- Compatibility with Visual Studio 2022 ([PR #2](https://github.com/sailro/CopilotCliIde/pull/2)).

## [1.0.9] - 2026-03-10

### Added

- Continuous integration and release pipelines.
- Build status badge in the README.

## [1.0.8] - 2026-03-10

### Fixed

- Diagnostics now report accurate end-of-range positions instead of always showing column zero.
- More accurate column reporting for the current selection.

## [1.0.7] - 2026-03-08

### Added

- **Live diagnostics.** Errors and warnings from the Error List are pushed to Copilot CLI as they appear — no need to ask.

## [1.0.6] - 2026-03-07

### Added

- Initial selection and diagnostics are pushed to Copilot CLI as soon as it connects.
- Diagnostic logs are now consolidated in a dedicated **Copilot CLI IDE** pane in the Output Window (**View → Output**).

### Fixed

- Selection-position log entries now use 1-based line and column numbers, matching the editor.

### Docs

- README updated to describe the Output Window diagnostics pane.

## [1.0.5] - 2026-03-05

### Fixed

- The current selection is pushed correctly the first time Copilot CLI connects, even on lazily loaded windows.
- Selection updates properly when you switch document tabs.
- No more stale or out-of-order selection updates while dragging with the mouse.
- The selection clears cleanly when the last editor tab is closed.

## [1.0.4] - 2026-03-05

### Changed

- Closing or switching solutions now disconnects Copilot CLI cleanly and reconnects to the new solution — matching how VS Code behaves when you close a folder.

### Docs

- README documents the solution-lifecycle behavior.

## [1.0.3] - 2026-03-04

### Added

- **Diff workflow.** When Copilot CLI proposes changes, a diff view opens with an Accept / Reject InfoBar. Closing the tab counts as Reject. The CLI waits until you decide.
- Selection changes are pushed to Copilot CLI as you move the cursor.

### Docs

- README updated with the new notification flow, architecture diagram, and VS Code compatibility notes.

## [1.0.2] - 2026-03-03

_Version bump only for marketplace publishing tests — no functional changes._

## [1.0.1] - 2026-03-03

### Fixed

- Multiple Visual Studio instances now coexist correctly (no shared-log conflicts, correct solution tracking per instance).

### Docs

- README updated with screenshots and feature details.

## [1.0.0] - 2026-03-02

### Added

- Initial release: Copilot CLI's `/ide` command now works with Visual Studio, just like it does with VS Code.
- Auto-discovery — Copilot CLI finds your running Visual Studio instances automatically.
- All 7 IDE tools supported: editor info, current selection, diff viewer (open/close), diagnostics, file read, and session naming.
- Live editor selection sent to Copilot CLI.

[Unreleased]: https://github.com/sailro/CopilotCliIde/compare/1.0.20...HEAD
[1.0.20]: https://github.com/sailro/CopilotCliIde/compare/1.0.19...1.0.20
[1.0.19]: https://github.com/sailro/CopilotCliIde/compare/1.0.18...1.0.19
[1.0.18]: https://github.com/sailro/CopilotCliIde/compare/1.0.17...1.0.18
[1.0.17]: https://github.com/sailro/CopilotCliIde/compare/1.0.16...1.0.17
[1.0.16]: https://github.com/sailro/CopilotCliIde/compare/1.0.15...1.0.16
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
