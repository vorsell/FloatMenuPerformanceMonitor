# Float Menu Performance Monitor

A RimWorld 1.6 / 1.7 developer tool for locating map right-click-menu performance hotspots. It measures float-menu providers and WorkGiver checks, writes sampled results to `Player.log`, and can register dedicated profiling entries with Dubs Performance Analyzer when available.

This is a diagnostic tool and adds some profiling overhead; disable it before final FPS comparisons.

[Steam Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3783963046)

## RimWorld 1.7 update

- Includes a dedicated .NET 10 / x64 assembly for 1.7 and retains the existing 1.6 assembly. `LoadFolders.xml` selects only the matching version.
- Fixes the DPA Float Menu Overview runtime error on 1.7 without modifying or shipping DPA. DPA remains optional.
- The 1.7 build passed manual checks with and without DPA, including coexistence with Float Menu Performance Optimizer.

## Log locations

- RimWorld 1.6: `%USERPROFILE%\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Player.log`
- RimWorld 1.7: `%USERPROFILE%\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Logs\Player.log`

## Building

For 1.7, use `Source/FloatMenuPerformanceMonitor.1.7.csproj` with the .NET 10 SDK and x64 Release. Set `RimWorldDir` and `HarmonyDir` for your 1.7 installation and compatible Harmony.

The legacy `Source/FloatMenuPerformanceMonitor.csproj` targets .NET Framework 4.8.1 / x64 for 1.6. Its local `HintPath` values must point to version-matched 1.6 references; use a preserved 1.6 snapshot if the installed game has been upgraded.

Redirect final and intermediate build outputs to staging for compile-only checks, and exit the game before replacing active assemblies. The existing 1.6 DLL was retained rather than rebuilt for this update.

