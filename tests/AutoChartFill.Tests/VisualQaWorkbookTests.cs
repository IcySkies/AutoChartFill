using AutoChartFill.Core;
using AutoChartFill.Excel;
using ClosedXML.Excel;

namespace AutoChartFill.Tests;

public sealed class VisualQaWorkbookTests
{
    [Fact]
    public async Task ReproducesConfiguredWorkbookWhenRequested()
    {
        var input = Environment.GetEnvironmentVariable("AUTOCHARTFILL_REPRO_INPUT");
        var output = Environment.GetEnvironmentVariable("AUTOCHARTFILL_REPRO_OUTPUT");
        if (string.IsNullOrWhiteSpace(input) || string.IsNullOrWhiteSpace(output)) return;

        File.Copy(input, output, true);
        var state = await new JsonCaptureStore().LoadAsync(input);
        var settings = new WorkbookCaptureSettings
        {
            WorkbookPath = output,
            WorksheetName = "Sheet1",
            FirstDataRow = 1
        };
        var fallback = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "AutoChartFill.App", "Assets", "Memories_Sacrifice_jacket.png"));
        await new ClosedXmlWorkbookRenderer().RenderAsync(settings, state, fallback);
    }

    [Fact]
    public async Task GeneratesRepresentativeWorkbookWhenRequested()
    {
        var output = Environment.GetEnvironmentVariable("AUTOCHARTFILL_QA_OUTPUT");
        if (string.IsNullOrWhiteSpace(output)) return;

        var directory = Path.GetDirectoryName(output)!;
        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, $"qa-source-{Guid.NewGuid():N}.xlsx");
        try
        {
            using (var workbook = new XLWorkbook())
            {
                var sheet = workbook.AddWorksheet("Charts");
                var headers = new[] { "Jacket", "Song", "Difficulty", "Level", "Charter", "CHIP", "TECH", "STREAM", "CHORD", "BURST", "GIMMICK" };
                for (var i = 0; i < headers.Length; i++) sheet.Cell(1, i + 1).Value = headers[i];
                sheet.Range("A1:K1").Style.Fill.BackgroundColor = XLColor.FromHtml("#243137");
                sheet.Range("A1:K1").Style.Font.FontColor = XLColor.White;
                sheet.Range("A1:K1").Style.Font.Bold = true;
                sheet.Row(2).Height = 52;
                sheet.Column(1).Width = 13;
                sheet.Column(2).Width = 34;
                sheet.Columns(3, 11).Width = 12;
                sheet.Cell("L2").FormulaA1 = "=D2+F2";
                workbook.SaveAs(source);
            }

            var jacket = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "AutoChartFill.App", "Assets", "Memories_Sacrifice_jacket.png"));
            var stats = new ChartTechStats { Chip = 2, Tech = 4, Stream = 6, Chord = 8, Burst = 3, Gimmick = 1 };
            var state = new CaptureState
            {
                Records =
                [
                    new() { GameChartId = "qa-song", DifficultyCode = "FIN", Title = "Memories / Sacrifice", Artist = "Silentroom", Charter = "QA Charter", DifficultyNumber = 15.3m, JacketPath = jacket, TechStats = stats, SongOrder = 0 },
                    new() { GameChartId = "qa-song", DifficultyCode = "OPN", Title = "Memories / Sacrifice", Artist = "Silentroom", Charter = "QA Charter", DifficultyNumber = 8m, JacketPath = jacket, TechStats = stats, SongOrder = 0 },
                    new() { GameChartId = "second-song", DifficultyCode = "MID", Title = "Second Song", Artist = "Example Artist", Charter = "Another Charter", DifficultyNumber = 11m, JacketPath = jacket, TechStats = stats, SongOrder = 1 }
                ]
            };
            var settings = new WorkbookCaptureSettings { WorkbookPath = source, WorksheetName = "Charts", FirstDataRow = 2 };
            await new ClosedXmlWorkbookRenderer().RenderAsync(settings, state, jacket);
            File.Copy(source, output, true);
        }
        finally { if (File.Exists(source)) File.Delete(source); }
    }
}
