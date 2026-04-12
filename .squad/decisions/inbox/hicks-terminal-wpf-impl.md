# Decision: Use VS-Deployed Microsoft.Terminal.Wpf Assembly (Not NuGet)

**Author:** Hicks (Extension Dev)
**Date:** 2026-07-21
**Status:** IMPLEMENTED
**Impact:** HIGH — changes assembly sourcing strategy for terminal control

---

## Summary

Implements Ripley's terminal migration proposal (WebView2+xterm.js → Microsoft.Terminal.Wpf) with a critical adjustment: reference the VS-deployed assembly directly instead of using the CI.Microsoft.Terminal.Wpf NuGet package.

## Context

Sebastien discovered that `Microsoft.Terminal.Wpf.dll` and `Microsoft.Terminal.Control.dll` are already deployed with VS at:
```
Common7\IDE\CommonExtensions\Microsoft\Terminal\
```

This is VS's own terminal extension deployment. The DLLs are loaded into the VS AppDomain by the terminal extension.

## Decision

**Use a build-time assembly reference with `Private=false` instead of NuGet:**

```xml
<Reference Include="Microsoft.Terminal.Wpf">
  <HintPath>$(DevEnvDir)CommonExtensions\Microsoft\Terminal\Microsoft.Terminal.Wpf.dll</HintPath>
  <Private>false</Private>
</Reference>
```

This eliminates ALL risks from Ripley's NuGet-based proposal:
- ❌ CI NuGet package stability → ✅ Ships with VS itself
- ❌ Native DLL loading/resolution → ✅ Already loaded by VS terminal extension
- ❌ VSIX size increase (~2MB native DLLs) → ✅ Zero additional payload
- ❌ `ProvideCodeBase`/assembly resolution → ✅ Already in AppDomain
- ❌ Architecture-specific native DLL bundling → ✅ VS handles it

## Implementation Notes

- `$(DevEnvDir)` resolves during MSBuild because the build runs inside VS (F5 debug) or from a VS Developer Command Prompt where the variable is set.
- `Private=false` means "don't copy to output" — the DLL is already present at runtime.
- Required adding `System.Xaml` framework reference (TerminalControl's WPF base types need it).
- Theme detection uses luminance check instead of `IVsColorThemeService` (internal PIA unavailable to third-party extensions).

## Risks

- **Version coupling:** Our extension depends on whatever version of Microsoft.Terminal.Wpf ships with the target VS version. If the API changes between VS versions, we'd need conditional compilation or version checks.
- **Assembly not loaded:** If the user somehow has VS installed without the Terminal extension, the assembly won't be in the AppDomain. This is extremely unlikely (it's a default component) but would surface as a `TypeLoadException` on tool window open.
