using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AutoChartFill.Core;

public sealed class WorkbookColumnMappings
{
    public string Jacket { get; set; } = "A";
    public string Song { get; set; } = "B";
    public string Difficulty { get; set; } = "C";
    public string Level { get; set; } = "D";
    public string Charter { get; set; } = "E";
    public string Chip { get; set; } = "F";
    public string Tech { get; set; } = "G";
    public string Stream { get; set; } = "H";
    public string Chord { get; set; } = "I";
    public string Burst { get; set; } = "J";
    public string Gimmick { get; set; } = "K";

    public IReadOnlyDictionary<string, string> AsDictionary() => new Dictionary<string, string>
    {
        [nameof(Jacket)] = Jacket, [nameof(Song)] = Song, [nameof(Difficulty)] = Difficulty,
        [nameof(Level)] = Level, [nameof(Charter)] = Charter, [nameof(Chip)] = Chip,
        [nameof(Tech)] = Tech, [nameof(Stream)] = Stream, [nameof(Chord)] = Chord,
        [nameof(Burst)] = Burst, [nameof(Gimmick)] = Gimmick
    };
}

public sealed class DifficultyColors
{
    public string Opn { get; set; } = "#4A90E2";
    public string Mid { get; set; } = "#43A047";
    public string Fin { get; set; } = "#E53935";
    public string Enc { get; set; } = "#8E44AD";
    public string Bks { get; set; } = "#F39C12";
    public string Shatter { get; set; } = "#455A64";

    public string For(string difficulty) => DifficultyCodes.Normalize(difficulty) switch
    {
        "OPN" => Opn, "MID" => Mid, "FIN" => Fin, "ENC" => Enc,
        "BKS" => Bks, _ => Shatter
    };
}

public sealed class WorkbookCaptureSettings
{
    public string WorkbookPath { get; set; } = "";
    public string WorksheetName { get; set; } = "";
    public int FirstDataRow { get; set; } = 2;
    public WorkbookColumnMappings Columns { get; set; } = new();
    public DifficultyColors Colors { get; set; } = new();

    public string MappingSignature()
    {
        var source = $"{Path.GetFullPath(WorkbookPath).ToUpperInvariant()}\n{WorksheetName}\n{FirstDataRow}\n" +
            string.Join("\n", Columns.AsDictionary().OrderBy(x => x.Key).Select(x => $"{x.Key}:{x.Value.Trim().ToUpperInvariant()}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
    }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(WorkbookPath) || !Path.GetExtension(WorkbookPath).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
            errors.Add("Choose an .xlsx workbook.");
        else if (!File.Exists(WorkbookPath)) errors.Add("The selected workbook does not exist.");
        if (string.IsNullOrWhiteSpace(WorksheetName)) errors.Add("Choose a worksheet.");
        if (FirstDataRow < 1) errors.Add("First data row must be 1 or greater.");
        var mappings = Columns.AsDictionary();
        foreach (var mapping in mappings)
            if (!Regex.IsMatch(mapping.Value.Trim(), "^[A-Za-z]{1,3}$")) errors.Add($"{mapping.Key} must be an Excel column letter.");
        var duplicate = mappings.Values.Select(x => x.Trim().ToUpperInvariant()).GroupBy(x => x).FirstOrDefault(x => x.Count() > 1);
        if (duplicate is not null) errors.Add($"Column {duplicate.Key} is assigned more than once.");
        foreach (var color in DifficultyCodes.All.Select(Colors.For))
            if (!Regex.IsMatch(color, "^#[0-9A-Fa-f]{6}$")) errors.Add($"Invalid color value: {color}");
        return errors;
    }
}

public sealed class CaptureState
{
    public int SchemaVersion { get; set; } = 1;
    public string MappingSignature { get; set; } = "";
    public int ManagedStartRow { get; set; }
    public int RenderedRecordCount { get; set; }
    public bool PendingRender { get; set; }
    public List<CapturedDifficulty> Records { get; set; } = [];

    public IReadOnlyList<CapturedDifficulty> OrderedRecords() => Records
        .OrderBy(x => x.SongOrder)
        .ThenBy(x => DifficultyCodes.OrderOf(x.DifficultyCode))
        .ThenBy(x => x.DifficultyCode, StringComparer.Ordinal)
        .ToList();
}

public sealed record WorkbookValidationResult(bool IsValid, string Message, IReadOnlyList<string> Worksheets)
{
    public static WorkbookValidationResult Valid(IReadOnlyList<string> worksheets) => new(true, "Workbook configuration is valid.", worksheets);
    public static WorkbookValidationResult Invalid(string message, IReadOnlyList<string>? worksheets = null) => new(false, message, worksheets ?? []);
}

public interface ICaptureStore
{
    Task<CaptureState> LoadAsync(string workbookPath, CancellationToken cancellationToken = default);
    Task SaveAsync(string workbookPath, CaptureState state, CancellationToken cancellationToken = default);
    Task DeleteAsync(string workbookPath, CancellationToken cancellationToken = default);
}

public interface IWorkbookRenderer
{
    Task<IReadOnlyList<string>> GetWorksheetNamesAsync(string workbookPath, CancellationToken cancellationToken = default);
    Task RenderAsync(WorkbookCaptureSettings settings, CaptureState state, string fallbackJacketPath, CancellationToken cancellationToken = default);
    Task ResetAsync(WorkbookCaptureSettings settings, CaptureState state, CancellationToken cancellationToken = default);
}
