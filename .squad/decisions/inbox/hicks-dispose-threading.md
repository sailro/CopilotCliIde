### Remove UI Thread Requirement from Dispose

**Author:** Hicks (Extension Dev)  
**Date:** 2026-03-07  
**Status:** Implemented

## Context

`Dispose(bool disposing)` called `ThreadHelper.ThrowIfNotOnUIThread()` and re-fetched `IVsMonitorSelection` via `GetGlobalService`. This is fragile: Dispose can be called during shutdown when the UI thread may not be available, and `GetGlobalService` may return null for a service that was alive during `InitializeAsync`.

## Decision

1. Cache `IVsMonitorSelection` as `_monitorSelection` field, captured in `InitializeAsync` where it's already fetched.
2. Remove `ThreadHelper.ThrowIfNotOnUIThread()` from `Dispose` — the cached reference eliminates the need for a service lookup.
3. Use the cached `_monitorSelection` for `UnadviseSelectionEvents` in Dispose.
4. Null out both `_monitorSelection` and `_selectionMonitorCookie` (set to 0) after unadvising for clean teardown.

## Rationale

- **Dispose shouldn't throw.** `ThrowIfNotOnUIThread()` in a disposal path violates the principle that cleanup should be resilient.
- **Caching avoids stale-service risk.** During VS shutdown, `GetGlobalService` may return null even for services that were healthy at init time.
- **Zeroing the cookie** prevents double-unadvise if Dispose is called more than once.

## Files Changed

- `src/CopilotCliIde/CopilotCliIdePackage.cs` — field added, InitializeAsync updated, Dispose refactored

## Verification

- Server builds clean (0 warnings)
- 109 tests pass
