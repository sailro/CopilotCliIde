# Project Context

- **Owner:** Sebastien
- **Project:** CopilotCliIde — A Visual Studio extension (VSIX) bridging GitHub Copilot CLI's /ide command with Visual Studio via MCP over named pipes. Three C# projects: CopilotCliIde (VS extension, net472), CopilotCliIde.Server (MCP server, net10.0), CopilotCliIde.Shared (contracts, netstandard2.0).
- **Stack:** C#, .NET, MSBuild, VSSDK, StreamJsonRpc, MCP, Windows named pipes
- **Created:** 2026-03-05

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->

### 2026-03-10 — Orchestration: Team Improvement Review Quartet (2026-03-10T19-57-12Z)

Executed a four-agent review session (Ripley, Hicks, Bishop, Hudson) in parallel mode. Each agent produced a formal findings report. Scribe's responsibilities:

**Task completion:**
1. Created 4 orchestration logs (`.squad/orchestration-log/{timestamp}-{agent}.md`)
2. Created 1 session log (`.squad/log/{timestamp}-team-improvement-review.md`)
3. Merged 6 inbox files into `.squad/decisions.md` under "Review Findings — 2026-03-10" section (15 HIGH/MEDIUM/LOW findings + 15 action items)
4. Appended cross-agent updates to each agent's history.md (Ripley, Hicks, Bishop, Hudson)
5. Deleted 6 inbox files after merge (ripley-architecture-review, hicks-extension-review-findings, bishop-server-review-findings, hudson-test-coverage-review-2026-03, copilot-directive-2026-03-10T09-16-38Z, copilot-directive-2026-03-10T09-20-15Z)
6. Skipped archive (no entries >30 days old yet; decisions.md is now 85 KB after merge)
7. Ready for git commit (7 files modified)

**Key cross-references documented:**
- Hicks HIGH-2 (DebouncePusher TOCTOU) = Ripley H1 (threading hazard)
- Ripley H4 (silent catches) spans extension and server — unified fix opportunity
- Bishop H1+H2 (cache-control, timeout race) are protocol correctness issues
- Bishop M3 (fire-and-forget event handlers) is Hicks territory (Program.cs)
- Hudson items 3-6 depend on extracting Ripley's L4, L5 (PathUtils, DebouncePusher tests)
- Hudson item 12 (CopilotCliIde.Tests project) blocks downstream improvements
