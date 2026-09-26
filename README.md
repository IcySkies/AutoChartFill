# AutoChartFill

AutoChartFill monitors vivid/stasis chart-selection events and writes highlighted playable difficulties into an existing `.xlsx` workbook. It is a standalone .NET 8 WPF application and does not connect to OBS.

## Setup

1. Synchronize `..\In-gameInfoAPI` with `..\In-gameInfoAPI\tools\Sync-ModToLoader.ps1` and verify it with `..\In-gameInfoAPI\tools\Verify-ModSync.ps1`.
2. Enable the installed `..\SourceFiles\VividStasisModLoader\mods\VividStasisGameInfoAPI v3.0.0` mod. AutoChartFill starts or reuses the required `VividStasisGameInfoRelay.exe`, which owns `127.0.0.1:28745` and fans events out to subscribers.
3. Choose an `.xlsx` workbook, worksheet, prototype row, and one distinct column for each output field.
4. Save the configuration and enable **Capture**.
5. Highlight difficulties in vivid/stasis. Empty charts whose six tech statistics are all zero are ignored.

The selected workbook is edited in place. Tracking metadata is stored beside it as `<workbook>.autochartfill.json`. Keep the workbook closed in Excel while capturing; locked writes remain pending and can be retried.

Rows are keyed by chart ID and difficulty, so repeated highlights update rather than duplicate. Difficulties within a song are ordered OPN, MID, FIN, ENC, BKS, SHATTER. Jacket and `Title - Artist` cells are merged across each song group.

## Build and test

```powershell
dotnet build AutoChartFill.sln --configuration Release
dotnet test tests\AutoChartFill.Tests\AutoChartFill.Tests.csproj --configuration Release
```
