# Single Source of Truth for Capture File Discovery

**Date:** 2026-03-08  
**Decided by:** Hudson (Tester)  
**Status:** Implemented

## Context

`TrafficReplayTests.cs` had 4 separate locations calling `Directory.GetFiles(FindCapturesDir(), "*.ndjson")`:
1. Line 25 in `CaptureFiles()` TheoryData method
2. Line 349 in `AllCaptures_ToolInputSchemas_AreConsistent()` [Fact]
3. Line 822 in `AllCaptures_RequestResponseIds_AreCorrelated()` [Fact]
4. Line 918 in `OurToolsList_MatchesVsCodeToolNames()` [Fact]

This duplication meant any changes to capture file discovery logic (filters, ordering, exclusions) would require updates in 4 places.

## Decision

Extracted a single private helper method `GetCaptureFiles()` that returns `string[]` from `Directory.GetFiles(FindCapturesDir(), "*.ndjson")`. All 4 locations now call this helper instead of inlining the file discovery logic.

```csharp
/// <summary>
/// Returns all .ndjson capture files from the Captures/ directory.
/// </summary>
private static string[] GetCaptureFiles()
{
	return Directory.GetFiles(FindCapturesDir(), "*.ndjson");
}
```

## Rationale

- **Single source of truth:** Capture file discovery logic lives in exactly one place
- **Future-proof:** Adding exclusions (e.g., skip broken captures, filter by name pattern) requires one change, not four
- **No behavioral change:** Tests continue to pass exactly as before (142/142)
- **Minimal footprint:** Single helper method, no new abstractions or complexity

## Verification

- Build: `dotnet build src\CopilotCliIde.Server.Tests\CopilotCliIde.Server.Tests.csproj` ✅
- Tests: `dotnet test src\CopilotCliIde.Server.Tests\CopilotCliIde.Server.Tests.csproj --no-build` — 142/142 pass ✅
