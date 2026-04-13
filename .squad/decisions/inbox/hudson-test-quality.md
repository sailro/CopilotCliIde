# Decision: Test Quality Standards — No-Op Assertions and Duplicates

**Author:** Hudson
**Date:** 2026-03-31
**Status:** Implemented

## Context

Audit of `CopilotCliIde.Server.Tests` found 3 no-op assertions and 2 duplicate tests across 5 files.

## Changes

- **3 no-op assertions fixed:** `Assert.True(comparisons >= 0)` tautologies in consistency tests removed; `Assert.NotNull(task)` in McpPipeServerTests replaced with proper async await.
- **2 duplicates removed:** `OpenDiff_Output_ClosedViaTool_Rejection` (subset of `OpenDiff_Output_ClosedViaTool`), `ToolsList_TaskSupportIsForbidden` (subsumed by `OurToolsList_MatchesVsCodeToolNames`).
- Test count: 284 → 282 (all passing).

## Convention

Going forward: never use `Assert.True(x >= 0)` on counters that can only be ≥ 0 — it's a tautology. If the intent is logging, use xUnit's `ITestOutputHelper` or remove the assertion. If the intent is coverage validation, assert `> 0` and filter test data appropriately.
