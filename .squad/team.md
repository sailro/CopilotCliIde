# Squad Team

> CopilotCliIde — VS extension bridging Copilot CLI's /ide command with Visual Studio

## Coordinator

| Name | Role | Notes |
|------|------|-------|
| Squad | Coordinator | Routes work, enforces handoffs and reviewer gates. |

## Members

| Name | Role | Charter | Status |
|------|------|---------|--------|
| Ripley | Lead | `.squad/agents/ripley/charter.md` | 🏗️ Active |
| Hicks | Extension Dev | `.squad/agents/hicks/charter.md` | 🔌 Active |
| Bishop | Server Dev | `.squad/agents/bishop/charter.md` | 🔧 Active |
| Hudson | Tester | `.squad/agents/hudson/charter.md` | 🧪 Active |
| Scribe | Session Logger | `.squad/agents/scribe/charter.md` | 📋 Active |
| Ralph | Work Monitor | — | 🔄 Monitor |

## Project Context

- **Owner:** Sebastien
- **Project:** CopilotCliIde — Visual Studio extension (VSIX) bridging GitHub Copilot CLI's /ide command with Visual Studio via MCP over named pipes
- **Stack:** C#, .NET (net472 / net10.0 / netstandard2.0), MSBuild, VSSDK, StreamJsonRpc, MCP, Windows named pipes, WebView2, xterm.js, ConPTY
- **Architecture:** Copilot CLI → MCP over named pipe → CopilotCliIde.Server → StreamJsonRpc over named pipe → CopilotCliIde (VS extension)
- **Created:** 2026-03-05
