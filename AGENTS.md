# Repository Instructions

## VividStasis Mod Changes

- The authoritative GameMaker mod source is `..\In-gameInfoAPI`; do not edit the loader mirror directly.
- After changing the mod, run `..\In-gameInfoAPI\tools\Sync-ModToLoader.ps1` and `..\In-gameInfoAPI\tools\Verify-ModSync.ps1`.
- The installed loader package is `..\SourceFiles\VividStasisModLoader\mods\VividStasisGameInfoAPI v3.0.0`.
- Preserve protocol version 1, existing event kinds, port 28745, and legacy `AutoChartSwitchV2` configuration paths.

## Validation

- Run the VividStasisModLoader after synchronization.
- Verify the newest loader log ends with `Patch flow completed` and contains no missing-entry, patch, or compile errors.
- Run the AutoChartFill Release build and test suite.
