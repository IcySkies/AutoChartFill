using AutoChartFill.Core;

namespace AutoChartFill.App;

public sealed class AppSettings
{
    public bool CaptureEnabled { get; set; }
    public int BridgePort { get; set; } = 28745;
    public string GamePath { get; set; } = "";
    public WorkbookCaptureSettings Workbook { get; set; } = new();
}
