### 2026-03-07T14:35Z: User directive
**By:** Sebastien (via Copilot)
**What:** Never use `ThreadHelper.ThrowIfNotOnUIThread()` in Dispose methods. Do not assume Dispose runs on the UI thread.
**Why:** User request — captured for team memory
