using AutoChartFill.Core;
using AutoChartFill.Excel;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Drawing.Spreadsheet;
using A = DocumentFormat.OpenXml.Drawing;

namespace AutoChartFill.Tests;

public sealed class WorkbookRendererTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "AutoChartFill.Tests", Guid.NewGuid().ToString("N"));

    public WorkbookRendererTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task AppendsAfterExistingRowsAndPreservesUnmappedContent()
    {
        var path = CreateTemplate(existingRow: true);
        using (var setupWorkbook = new XLWorkbook(path))
        {
            setupWorkbook.Worksheet("Charts").Cell("B3").Value = "After";
            setupWorkbook.Save();
        }
        var settings = Settings(path);
        var state = State(Record("song-1", "OPN", 0));

        await new ClosedXmlWorkbookRenderer().RenderAsync(settings, state, "");

        using var workbook = new XLWorkbook(path);
        var sheet = workbook.Worksheet("Charts");
        Assert.Equal("Existing Song", sheet.Cell("B2").GetString());
        Assert.Equal("After", sheet.Cell("B3").GetString());
        Assert.Equal("Song song-1 - Artist", sheet.Cell("B4").GetString());
        Assert.Equal("keep", sheet.Cell("L4").GetString());
        Assert.Equal(4, state.ManagedStartRow);
        Assert.Equal(1, state.RenderedRecordCount);
    }

    [Fact]
    public async Task GroupsAndOrdersDifficultiesWithSharedMergesAndOneJacket()
    {
        var path = CreateTemplate();
        var jacket = CreateJacket();
        var settings = Settings(path);
        var records = new[]
        {
            Record("song-1", "FIN", 0, jacket),
            Record("song-2", "MID", 1, jacket),
            Record("song-1", "OPN", 0, jacket),
            Record("song-1", "BKS", 0, jacket),
            Record("song-1", "ENC", 0, jacket)
        };
        var state = State(records);

        await new ClosedXmlWorkbookRenderer().RenderAsync(settings, state, "");

        using var workbook = new XLWorkbook(path);
        var sheet = workbook.Worksheet("Charts");
        Assert.Equal(new[] { "OPN", "FIN", "ENC", "BKS", "MID" }, sheet.Range("C2:C6").Cells().Select(x => x.GetString()));
        Assert.Contains(sheet.MergedRanges, x => x.RangeAddress.ToString() == "A2:A5");
        Assert.Contains(sheet.MergedRanges, x => x.RangeAddress.ToString() == "B2:B5");
        Assert.Equal(2, sheet.Pictures.Count);
        Assert.All(sheet.Pictures, picture => Assert.StartsWith("AutoChartFill_", picture.Name));
    }

    [Fact]
    public async Task LongChartIdsProduceValidUniquePictureNames()
    {
        var path = CreateTemplate();
        var jacket = CreateJacket();
        var settings = Settings(path);
        var state = State(
            Record("this-is-a-very-long-chart-identifier-one", "OPN", 0, jacket),
            Record("this-is-a-very-long-chart-identifier-two", "OPN", 1, jacket));

        await new ClosedXmlWorkbookRenderer().RenderAsync(settings, state, "");

        using var workbook = new XLWorkbook(path);
        var names = workbook.Worksheet("Charts").Pictures.Select(x => x.Name).ToList();
        Assert.Equal(2, names.Distinct(StringComparer.Ordinal).Count());
        Assert.All(names, name => Assert.True(name.Length <= 31));
    }

    [Fact]
    public async Task RepairsOrphanedOwnedPictureAndPreservesTemplatePictures()
    {
        var path = CreateTemplate();
        var jacket = CreateJacket();
        using (var setupWorkbook = new XLWorkbook(path))
        {
            var sheet = setupWorkbook.Worksheet("Charts");
            var templatePicture = sheet.AddPicture(jacket);
            templatePicture.Name = "TemplateLogo";
            templatePicture.MoveTo(sheet.Cell("M2"));
            setupWorkbook.Save();
        }

        var settings = Settings(path);
        var state = State(Record("song-1", "OPN", 0, jacket));
        var renderer = new ClosedXmlWorkbookRenderer();
        await renderer.RenderAsync(settings, state, jacket);
        BreakOwnedPictureRelationship(path);

        state.Records[0] = state.Records[0] with { Charter = "Recovered" };
        await renderer.RenderAsync(settings, state, jacket);

        using var workbook = new XLWorkbook(path);
        var sheetAfterRepair = workbook.Worksheet("Charts");
        Assert.Equal("Recovered", sheetAfterRepair.Cell("E2").GetString());
        Assert.Single(sheetAfterRepair.Pictures, picture => picture.Name == "TemplateLogo");
        Assert.Single(sheetAfterRepair.Pictures, picture => picture.Name.StartsWith("AutoChartFill_"));
    }

    [Fact]
    public async Task OnlyDifficultyCellGetsConfiguredFill()
    {
        var path = CreateTemplate();
        var settings = Settings(path);
        settings.Colors.Opn = "#123456";
        var state = State(Record("song-1", "OPN", 0));

        await new ClosedXmlWorkbookRenderer().RenderAsync(settings, state, "");

        using var workbook = new XLWorkbook(path);
        var sheet = workbook.Worksheet("Charts");
        Assert.Equal("FF123456", sheet.Cell("C2").Style.Fill.BackgroundColor.Color.ToArgb().ToString("X8"));
        Assert.NotEqual(sheet.Cell("C2").Style.Fill.BackgroundColor, sheet.Cell("B2").Style.Fill.BackgroundColor);
        Assert.Equal("FFFFFFFF", sheet.Cell("C2").Style.Font.FontColor.Color.ToArgb().ToString("X8"));
    }

    [Fact]
    public async Task ReRenderUpdatesRowsWithoutDuplication()
    {
        var path = CreateTemplate();
        var settings = Settings(path);
        var state = State(Record("song-1", "FIN", 0));
        var renderer = new ClosedXmlWorkbookRenderer();
        await renderer.RenderAsync(settings, state, "");
        using (var interimWorkbook = new XLWorkbook(path))
        {
            interimWorkbook.Worksheet("Charts").Cell("B3").Value = "After";
            interimWorkbook.Save();
        }
        state.Records[0] = state.Records[0] with { DifficultyNumber = 19.7m, Charter = "Updated" };
        state.Records.Add(Record("song-1", "OPN", 0));

        await renderer.RenderAsync(settings, state, "");

        using var workbook = new XLWorkbook(path);
        var sheet = workbook.Worksheet("Charts");
        Assert.Equal(new[] { "OPN", "FIN" }, sheet.Range("C2:C3").Cells().Select(x => x.GetString()));
        Assert.Equal(19.7m, sheet.Cell("D3").GetValue<decimal>());
        Assert.Equal("Updated", sheet.Cell("E3").GetString());
        Assert.Equal("After", sheet.Cell("B4").GetString());
    }

    [Fact]
    public async Task ResetRemovesOnlyManagedBlock()
    {
        var path = CreateTemplate(existingRow: true);
        using (var setupWorkbook = new XLWorkbook(path))
        {
            setupWorkbook.Worksheet("Charts").Cell("B3").Value = "After";
            setupWorkbook.Save();
        }
        var settings = Settings(path);
        var state = State(Record("song-1", "OPN", 0), Record("song-1", "MID", 0));
        var renderer = new ClosedXmlWorkbookRenderer();
        await renderer.RenderAsync(settings, state, "");
        using (var editedWorkbook = new XLWorkbook(path))
        {
            editedWorkbook.Worksheet("Charts").Cell(state.ManagedStartRow, 12).Value = "operator note";
            editedWorkbook.Save();
        }

        await renderer.ResetAsync(settings, state);

        using var workbook = new XLWorkbook(path);
        var sheet = workbook.Worksheet("Charts");
        Assert.Equal("Existing Song", sheet.Cell("B2").GetString());
        Assert.Equal("After", sheet.Cell("B3").GetString());
        Assert.Equal("operator note", sheet.Cell(state.ManagedStartRow, 12).GetString());
        Assert.Equal("", sheet.Cell(state.ManagedStartRow, 2).GetString());
        Assert.DoesNotContain(sheet.Pictures, x => x.Name.StartsWith("AutoChartFill_"));
    }

    private string CreateTemplate(bool existingRow = false)
    {
        var path = Path.Combine(_directory, $"template-{Guid.NewGuid():N}.xlsx");
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Charts");
        sheet.Row(2).Height = 45;
        sheet.Range("A2:K2").Style.Border.BottomBorder = XLBorderStyleValues.Thin;
        sheet.Range("A2:K2").Style.Font.FontName = "Arial";
        sheet.Cell("L2").Value = "keep";
        if (existingRow) sheet.Cell("B2").Value = "Existing Song";
        workbook.SaveAs(path);
        return path;
    }

    private string CreateJacket()
    {
        var source = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "AutoChartFill.App", "Assets", "Memories_Sacrifice_jacket.png");
        return Path.GetFullPath(source);
    }

    private static void BreakOwnedPictureRelationship(string path)
    {
        using var document = SpreadsheetDocument.Open(path, true);
        var workbookPart = document.WorkbookPart!;
        var sheet = workbookPart.Workbook.Sheets!.Elements<DocumentFormat.OpenXml.Spreadsheet.Sheet>()
            .Single(x => x.Name?.Value == "Charts");
        var worksheetPart = (WorksheetPart)workbookPart.GetPartById(sheet.Id!.Value!);
        var drawingsPart = worksheetPart.DrawingsPart!;
        var ownedAnchor = drawingsPart.WorksheetDrawing.ChildElements.Single(anchor =>
            anchor.Descendants<NonVisualDrawingProperties>().Any(properties =>
                properties.Name?.Value is { } name && name.StartsWith("AutoChartFill_", StringComparison.Ordinal)));
        var relationshipId = ownedAnchor.Descendants<A.Blip>().Single().Embed!.Value!;

        drawingsPart.DeletePart(relationshipId);
        drawingsPart.WorksheetDrawing.Save();
    }

    private static WorkbookCaptureSettings Settings(string path) => new()
    {
        WorkbookPath = path,
        WorksheetName = "Charts",
        FirstDataRow = 2
    };

    private static CaptureState State(params CapturedDifficulty[] records) => new()
    {
        MappingSignature = "test",
        PendingRender = true,
        Records = records.ToList()
    };

    private static CapturedDifficulty Record(string id, string difficulty, int order, string jacket = "") => new()
    {
        GameChartId = id,
        DifficultyCode = difficulty,
        Title = $"Song {id}",
        Artist = "Artist",
        Charter = "Charter",
        DifficultyNumber = 12.3m,
        JacketPath = jacket,
        SongOrder = order,
        TechStats = new() { Chip = 1, Tech = 2, Stream = 3, Chord = 4, Burst = 5, Gimmick = 6 }
    };

    public void Dispose()
    {
        try { Directory.Delete(_directory, true); } catch { }
    }
}
