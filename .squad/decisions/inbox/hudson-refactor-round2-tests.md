# Hudson — Refactor + Round 2 Priority Tests

**Date:** 2026-03-08
**Author:** Hudson (Tester)

## Changes Made

### Refactor: FindAllCaptureFiles removed
- `FindAllCaptureFiles()` eliminated. Cross-capture `[Fact]` tests inline `Directory.GetFiles(FindCapturesDir(), "*.ndjson")`.
- All per-capture tests use `[Theory] [MemberData(nameof(CaptureFiles))]` with `TheoryData<string>`.
- `FindCapturesDir()` retained as shared infrastructure.

### Test 6 Extended: diagnostic `source`/`code` validation
- `VsCodeDiagnosticsChanged_HasExpectedStructure` now validates `source` (string|null) and `code` (string|null) fields when present on diagnostic items.
- Cross-capture safe: checks type only when field exists.

### Test E2: get_selection integration
- `OurServer_GetSelectionResponse_HasExpectedStructure` — full pipe roundtrip with mocked `IVsServiceRpc`.
- Validates all 5 top-level fields and selection sub-fields with realistic mock data.

### Test E3: Auth rejection
- `OurServer_InvalidNonce_Returns401` — verifies wrong nonce gets HTTP 401.
- Tests the sole security boundary of the MCP pipe server.

### Test B1 Fix
- Pre-existing failure on vs-1.0.7: `current: false` responses omit `filePath`/`fileUrl`/`selection`.
- Added early return for `current: false` case.

## Impact
- Test count: 140 → 142
- All 142 passing
- No production code changes
