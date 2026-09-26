using System.Drawing;
using System.Security.Cryptography;
using System.Text;
using AutoChartFill.Core;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Drawing.Spreadsheet;
using A = DocumentFormat.OpenXml.Drawing;

namespace AutoChartFill.Excel;

public sealed class ClosedXmlWorkbookRenderer : IWorkbookRenderer
{
    private const string PicturePrefix = "AutoChartFill_";

    public Task<IReadOnlyList<string>> GetWorksheetNamesAsync(string workbookPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateWorkbookPath(workbookPath);
        var workingCopy = CreateWorkingCopy(workbookPath);
        try
        {
            RemoveOwnedPicturesFromPackage(workingCopy, worksheetName: null);
            using var workbook = new XLWorkbook(workingCopy);
            return Task.FromResult<IReadOnlyList<string>>(workbook.Worksheets.Select(x => x.Name).ToList());
        }
        finally
        {
            if (File.Exists(workingCopy)) File.Delete(workingCopy);
        }
    }

    public Task RenderAsync(WorkbookCaptureSettings settings, CaptureState state, string fallbackJacketPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateWorkbookPath(settings.WorkbookPath);
        var workingCopy = CreateWorkingCopy(settings.WorkbookPath);
        try
        {
            RemoveOwnedPicturesFromPackage(workingCopy, settings.WorksheetName);
            using var workbook = new XLWorkbook(workingCopy);
        var sheet = GetSheet(workbook, settings.WorksheetName);
        var ordered = state.OrderedRecords();

        if (state.ManagedStartRow == 0)
            state.ManagedStartRow = FindManagedStartRow(sheet, settings);

        RemoveManagedMerges(sheet, settings, state.ManagedStartRow, Math.Max(state.RenderedRecordCount, ordered.Count));
        ResizeManagedBlock(sheet, settings, state, ordered.Count);
        var prototypeRow = state.ManagedStartRow == settings.FirstDataRow
            ? state.ManagedStartRow + ordered.Count
            : settings.FirstDataRow;
        WriteRecords(sheet, settings, state.ManagedStartRow, prototypeRow, ordered);
        MergeAndAddJackets(sheet, settings, state.ManagedStartRow, ordered, fallbackJacketPath);
        state.RenderedRecordCount = ordered.Count;

            SaveAtomically(workbook, settings.WorkbookPath);
            return Task.CompletedTask;
        }
        finally { if (File.Exists(workingCopy)) File.Delete(workingCopy); }
    }

    public Task ResetAsync(WorkbookCaptureSettings settings, CaptureState state, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateWorkbookPath(settings.WorkbookPath);
        var workingCopy = CreateWorkingCopy(settings.WorkbookPath);
        try
        {
            RemoveOwnedPicturesFromPackage(workingCopy, settings.WorksheetName);
            using var workbook = new XLWorkbook(workingCopy);
        var sheet = GetSheet(workbook, settings.WorksheetName);
        RemoveManagedMerges(sheet, settings, state.ManagedStartRow, state.RenderedRecordCount);
        if (state.RenderedRecordCount > 0)
        {
            var prototypeRow = state.ManagedStartRow == settings.FirstDataRow
                ? state.ManagedStartRow + state.RenderedRecordCount
                : settings.FirstDataRow;
            foreach (var row in Enumerable.Range(state.ManagedStartRow, state.RenderedRecordCount))
            {
                foreach (var column in settings.Columns.AsDictionary().Values.Select(ColumnNumber))
                {
                    var target = sheet.Cell(row, column);
                    target.Clear(XLClearOptions.Contents);
                    target.Style = sheet.Cell(prototypeRow, column).Style;
                }
            }
        }
            SaveAtomically(workbook, settings.WorkbookPath);
            return Task.CompletedTask;
        }
        finally { if (File.Exists(workingCopy)) File.Delete(workingCopy); }
    }

    private static IXLWorksheet GetSheet(XLWorkbook workbook, string name)
    {
        if (!workbook.TryGetWorksheet(name, out var sheet)) throw new InvalidOperationException($"Worksheet '{name}' does not exist.");
        return sheet;
    }

    private static int FindManagedStartRow(IXLWorksheet sheet, WorkbookCaptureSettings settings)
    {
        var columns = settings.Columns.AsDictionary().Values.Select(ColumnNumber).ToList();
        var last = 0;
        foreach (var column in columns)
        {
            var cell = sheet.Column(column).CellsUsed(XLCellsUsedOptions.Contents)
                .Where(x => x.Address.RowNumber >= settings.FirstDataRow)
                .LastOrDefault();
            if (cell is not null) last = Math.Max(last, cell.Address.RowNumber);
        }
        return last == 0 ? settings.FirstDataRow : last + 1;
    }

    private static void ResizeManagedBlock(IXLWorksheet sheet, WorkbookCaptureSettings settings, CaptureState state, int newCount)
    {
        var oldCount = state.RenderedRecordCount;
        if (oldCount == 0)
        {
            if (newCount == 0) return;
            if (state.ManagedStartRow > settings.FirstDataRow)
            {
                sheet.Row(state.ManagedStartRow).InsertRowsAbove(newCount);
                for (var row = state.ManagedStartRow; row < state.ManagedStartRow + newCount; row++)
                    CopyPrototypeRow(sheet, settings.FirstDataRow, row);
            }
            else
            {
                sheet.Row(state.ManagedStartRow).InsertRowsAbove(newCount);
                var prototypeRow = state.ManagedStartRow + newCount;
                for (var row = state.ManagedStartRow; row < state.ManagedStartRow + newCount; row++)
                    CopyPrototypeRow(sheet, prototypeRow, row);
            }
            return;
        }

        if (newCount > oldCount)
        {
            var count = newCount - oldCount;
            var insertionRow = state.ManagedStartRow + oldCount;
            sheet.Row(insertionRow).InsertRowsAbove(count);
            var prototypeRow = state.ManagedStartRow == settings.FirstDataRow
                ? insertionRow + count
                : settings.FirstDataRow;
            for (var row = insertionRow; row < insertionRow + count; row++) CopyPrototypeRow(sheet, prototypeRow, row);
        }
        else if (newCount < oldCount)
        {
            sheet.Rows(state.ManagedStartRow + newCount, state.ManagedStartRow + oldCount - 1).Delete();
        }
    }

    private static void CopyPrototypeRow(IXLWorksheet sheet, int prototypeRow, int targetRow)
    {
        if (prototypeRow == targetRow) return;
        var lastColumn = Math.Max(sheet.LastColumnUsed(XLCellsUsedOptions.All)?.ColumnNumber() ?? 1, 1);
        sheet.Range(prototypeRow, 1, prototypeRow, lastColumn).CopyTo(sheet.Cell(targetRow, 1));
        sheet.Row(targetRow).Height = sheet.Row(prototypeRow).Height;
    }

    private static void WriteRecords(IXLWorksheet sheet, WorkbookCaptureSettings settings, int startRow, int prototypeRow, IReadOnlyList<CapturedDifficulty> records)
    {
        var columns = settings.Columns;
        for (var index = 0; index < records.Count; index++)
        {
            var row = startRow + index;
            var record = records[index];
            RestoreMappedStyles(sheet, settings, prototypeRow, row);
            SetText(sheet, row, columns.Jacket, "");
            SetText(sheet, row, columns.Song, record.SongText);
            SetText(sheet, row, columns.Difficulty, record.DifficultyCode);
            SetNumber(sheet, row, columns.Level, record.DifficultyNumber);
            SetText(sheet, row, columns.Charter, record.Charter);
            SetNumber(sheet, row, columns.Chip, record.TechStats.Chip);
            SetNumber(sheet, row, columns.Tech, record.TechStats.Tech);
            SetNumber(sheet, row, columns.Stream, record.TechStats.Stream);
            SetNumber(sheet, row, columns.Chord, record.TechStats.Chord);
            SetNumber(sheet, row, columns.Burst, record.TechStats.Burst);
            SetNumber(sheet, row, columns.Gimmick, record.TechStats.Gimmick);

            var difficultyCell = sheet.Cell(row, ColumnNumber(columns.Difficulty));
            var color = XLColor.FromHtml(settings.Colors.For(record.DifficultyCode));
            difficultyCell.Style.Fill.BackgroundColor = color;
            difficultyCell.Style.Font.FontColor = ContrastColor(color);
        }
    }

    private static void RestoreMappedStyles(IXLWorksheet sheet, WorkbookCaptureSettings settings, int prototypeRow, int row)
    {
        foreach (var column in settings.Columns.AsDictionary().Values.Select(ColumnNumber))
        {
            var target = sheet.Cell(row, column);
            var prototype = sheet.Cell(prototypeRow, column);
            target.Style = prototype.Style;
            target.Clear(XLClearOptions.Contents);
        }
    }

    private static void MergeAndAddJackets(IXLWorksheet sheet, WorkbookCaptureSettings settings, int startRow,
        IReadOnlyList<CapturedDifficulty> records, string fallbackJacketPath)
    {
        var rowOffset = 0;
        foreach (var group in records.GroupBy(x => x.GameChartId, StringComparer.OrdinalIgnoreCase))
        {
            var groupRecords = group.ToList();
            var first = startRow + rowOffset;
            var last = first + groupRecords.Count - 1;
            if (last > first)
            {
                sheet.Range(first, ColumnNumber(settings.Columns.Jacket), last, ColumnNumber(settings.Columns.Jacket)).Merge();
                sheet.Range(first, ColumnNumber(settings.Columns.Song), last, ColumnNumber(settings.Columns.Song)).Merge();
            }
            var jacketPath = ResolveJacket(groupRecords.Select(x => x.JacketPath), fallbackJacketPath);
            if (jacketPath is not null) AddJacket(sheet, settings.Columns.Jacket, first, last, jacketPath, group.Key);
            rowOffset += groupRecords.Count;
        }
    }

    private static string? ResolveJacket(IEnumerable<string> paths, string fallback)
    {
        foreach (var path in paths.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            if (Path.IsPathRooted(path) && File.Exists(path)) return path;
            var sandbox = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VIVIDSTASIS", path.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(sandbox)) return sandbox;
        }
        return File.Exists(fallback) ? fallback : null;
    }

    private static void AddJacket(IXLWorksheet sheet, string columnName, int firstRow, int lastRow, string path, string chartId)
    {
        var column = ColumnNumber(columnName);
        var width = Math.Max(12, (int)Math.Floor(sheet.Column(column).Width * 7));
        var height = Math.Max(12, (int)Math.Floor(Enumerable.Range(firstRow, lastRow - firstRow + 1).Sum(row => sheet.Row(row).Height) * 96d / 72d));
        var picture = sheet.AddPicture(path);
        picture.Name = PicturePrefix + SafeName(chartId);
        picture.Placement = ClosedXML.Excel.Drawings.XLPicturePlacement.Move;
        var scale = Math.Min((width - 4d) / picture.OriginalWidth, (height - 4d) / picture.OriginalHeight);
        picture.WithSize(Math.Max(1, (int)(picture.OriginalWidth * scale)), Math.Max(1, (int)(picture.OriginalHeight * scale)));
        picture.MoveTo(sheet.Cell(firstRow, column), new Point(2, 2));
    }

    private static string CreateWorkingCopy(string workbookPath)
    {
        var fullPath = Path.GetFullPath(workbookPath);
        var copy = Path.Combine(Path.GetDirectoryName(fullPath)!, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.working.xlsx");
        File.Copy(fullPath, copy, true);
        return copy;
    }

    private static void RemoveOwnedPicturesFromPackage(string workbookPath, string? worksheetName)
    {
        using var document = SpreadsheetDocument.Open(workbookPath, true);
        var workbookPart = document.WorkbookPart ?? throw new InvalidOperationException("Workbook content is missing.");
        var sheets = workbookPart.Workbook.Sheets?.Elements<DocumentFormat.OpenXml.Spreadsheet.Sheet>() ?? [];
        foreach (var sheet in sheets.Where(x => worksheetName is null ||
                                                string.Equals(x.Name?.Value, worksheetName, StringComparison.Ordinal)))
        {
            var worksheetRelationshipId = sheet.Id?.Value;
            if (string.IsNullOrWhiteSpace(worksheetRelationshipId))
            {
                if (worksheetName is not null)
                    throw new InvalidOperationException($"Worksheet '{worksheetName}' has no package relationship.");
                continue;
            }

            var worksheetPart = (WorksheetPart)workbookPart.GetPartById(worksheetRelationshipId);
            var drawingsPart = worksheetPart.DrawingsPart;
            var drawing = drawingsPart?.WorksheetDrawing;
            if (drawingsPart is null || drawing is null) continue;

            var ownedAnchors = drawing.ChildElements
                .Where(anchor => anchor.Descendants<NonVisualDrawingProperties>()
                    .Any(properties => properties.Name?.Value is { } name &&
                                       name.StartsWith(PicturePrefix, StringComparison.Ordinal)))
                .ToList();
            if (ownedAnchors.Count == 0) continue;

            foreach (var anchor in ownedAnchors) anchor.Remove();
            var referencedIds = drawing.Descendants<A.Blip>()
                .Select(blip => blip.Embed?.Value)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.Ordinal);
            foreach (var imagePart in drawingsPart.ImageParts.ToList())
            {
                var relationshipId = drawingsPart.GetIdOfPart(imagePart);
                if (!referencedIds.Contains(relationshipId)) drawingsPart.DeletePart(imagePart);
            }
            drawing.Save();
        }

        if (worksheetName is not null && !sheets.Any(x => string.Equals(x.Name?.Value, worksheetName, StringComparison.Ordinal)))
            throw new InvalidOperationException($"Worksheet '{worksheetName}' does not exist.");
    }

    private static void RemoveManagedMerges(IXLWorksheet sheet, WorkbookCaptureSettings settings, int startRow, int count)
    {
        if (startRow <= 0 || count <= 0) return;
        var endRow = startRow + count - 1;
        var columns = new[] { ColumnNumber(settings.Columns.Jacket), ColumnNumber(settings.Columns.Song) };
        foreach (var range in sheet.MergedRanges.ToList())
        {
            if (range.RangeAddress.LastAddress.RowNumber < startRow || range.RangeAddress.FirstAddress.RowNumber > endRow) continue;
            if (columns.Any(column => range.RangeAddress.FirstAddress.ColumnNumber <= column && range.RangeAddress.LastAddress.ColumnNumber >= column))
                range.Unmerge();
        }
    }

    private static void SaveAtomically(XLWorkbook workbook, string path)
    {
        var fullPath = Path.GetFullPath(path);
        var temporary = Path.Combine(Path.GetDirectoryName(fullPath)!, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp.xlsx");
        try
        {
            workbook.SaveAs(temporary);
            using (File.Open(fullPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            File.Replace(temporary, fullPath, null, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void ValidateWorkbookPath(string path)
    {
        if (!Path.GetExtension(path).Equals(".xlsx", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Only .xlsx workbooks are supported.");
        if (!File.Exists(path)) throw new FileNotFoundException("The selected workbook does not exist.", path);
    }

    private static int ColumnNumber(string column) => XLHelper.GetColumnNumberFromLetter(column.Trim().ToUpperInvariant());
    private static void SetText(IXLWorksheet sheet, int row, string column, string value) => sheet.Cell(row, ColumnNumber(column)).Value = value;
    private static void SetNumber(IXLWorksheet sheet, int row, string column, decimal value) => sheet.Cell(row, ColumnNumber(column)).Value = value;
    private static string SafeName(string value)
    {
        var cleaned = string.Concat(value.Select(x => char.IsLetterOrDigit(x) ? x : '_'));
        if (cleaned.Length > 8) cleaned = cleaned[..8];
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..8];
        return $"{cleaned}_{hash}";
    }

    private static XLColor ContrastColor(XLColor color)
    {
        var source = color.Color;
        var luminance = (0.2126 * source.R + 0.7152 * source.G + 0.0722 * source.B) / 255d;
        return luminance > 0.55 ? XLColor.Black : XLColor.White;
    }
}
