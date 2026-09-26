using System.Text.Json.Serialization;

namespace AutoChartFill.Core;

public sealed record ChartTechStats
{
    public decimal Chip { get; init; }
    public decimal Tech { get; init; }
    public decimal Stream { get; init; }
    public decimal Chord { get; init; }
    public decimal Burst { get; init; }
    public decimal Gimmick { get; init; }

    [JsonIgnore]
    public IReadOnlyList<decimal> Values => [Chip, Tech, Stream, Chord, Burst, Gimmick];

    [JsonIgnore]
    public bool IsEmpty => Values.All(value => value == 0m);
}

public enum GameEventKind
{
    Selection,
    ChartLoadingStarted,
    ChartStarted,
    ChartExitTransitionStarted,
    GameplayEnded,
    LobbySelection,
    WorldcrossRoom,
    WorldcrossGameplay,
    ChartInfo
}

public sealed record WorldcrossPlayer
{
    public string SteamId64 { get; init; } = "";
    public string Name { get; init; } = "";
    public string State { get; init; } = "unready";
    public decimal Rating { get; init; }
    public int Class { get; init; }
    public decimal Score { get; init; }
    public decimal LastPlayScore { get; init; }
    public string Label { get; init; } = "";
}

public sealed record WorldcrossSnapshot
{
    public IReadOnlyList<WorldcrossPlayer> Players { get; init; } = [];
}

public sealed record GameChartSnapshot
{
    public string ChartId { get; init; } = "";
    public string RawDifficultyName { get; init; } = "";
    public string DifficultyCode { get; init; } = "";
    public string Title { get; init; } = "";
    public string FormattedTitle { get; init; } = "";
    public string Artist { get; init; } = "";
    public string FormattedArtist { get; init; } = "";
    public string Illustrator { get; init; } = "";
    public string FormattedIllustrator { get; init; } = "";
    public string Charter { get; init; } = "";
    public string FormattedCharter { get; init; } = "";
    public decimal DifficultyNumber { get; init; }
    public string JacketPath { get; init; } = "";
    public ChartTechStats TechStats { get; init; } = new();
}

public sealed record GameEventEnvelope
{
    public int ProtocolVersion { get; init; } = 1;
    public long Sequence { get; init; }
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;
    public GameEventKind Kind { get; init; }
    public GameChartSnapshot? Chart { get; init; }
    public WorldcrossSnapshot? Worldcross { get; init; }
}

public static class DifficultyCodes
{
    private static readonly string[] Ordered = ["OPN", "MID", "FIN", "ENC", "BKS", "SHATTER"];

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        return value.Trim().ToUpperInvariant() switch
        {
        "OPENING" or "OPN" => "OPN",
        "MIDDLE" or "MID" => "MID",
        "FINALE" or "FIN" => "FIN",
        "ENCORE" or "ENC" => "ENC",
        "BACKSTAGE" or "BKS" => "BKS",
        "SHATTER" => "SHATTER",
        _ => "SHATTER"
        };
    }

    public static int OrderOf(string value)
    {
        var index = Array.IndexOf(Ordered, Normalize(value));
        return index < 0 ? int.MaxValue : index;
    }

    public static IReadOnlyList<string> All => Ordered;
}

public sealed record CapturedDifficulty
{
    public string GameChartId { get; init; } = "";
    public string DifficultyCode { get; init; } = "";
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string Charter { get; init; } = "";
    public decimal DifficultyNumber { get; init; }
    public string JacketPath { get; init; } = "";
    public ChartTechStats TechStats { get; init; } = new();
    public int SongOrder { get; init; }

    [JsonIgnore]
    public string Key => $"{GameChartId.Trim().ToUpperInvariant()}|{DifficultyCode}";

    [JsonIgnore]
    public string SongText => $"{Title} - {Artist}";
}

public sealed record CaptureCandidateResult(bool Accepted, string Message, CapturedDifficulty? Capture)
{
    public static CaptureCandidateResult From(GameChartSnapshot? chart, int songOrder)
    {
        if (chart is null) return new(false, "The selection did not include chart information.", null);
        var id = chart.ChartId.Trim();
        var title = chart.Title.Trim();
        var artist = chart.Artist.Trim();
        var difficulty = DifficultyCodes.Normalize(
            string.IsNullOrWhiteSpace(chart.DifficultyCode) ? chart.RawDifficultyName : chart.DifficultyCode);
        if (id.Length == 0) return new(false, "Skipped selection: chart ID is missing.", null);
        if (title.Length == 0) return new(false, "Skipped selection: title is missing.", null);
        if (artist.Length == 0) return new(false, "Skipped selection: artist is missing.", null);
        if (difficulty.Length == 0) return new(false, "Skipped selection: difficulty is missing or unknown.", null);
        if (chart.TechStats is null || chart.TechStats.IsEmpty)
            return new(false, "Skipped selection: the highlighted difficulty contains no chart data.", null);
        return new(true, "Ready to capture.", new CapturedDifficulty
        {
            GameChartId = id,
            DifficultyCode = difficulty,
            Title = title,
            Artist = artist,
            Charter = chart.Charter.Trim(),
            DifficultyNumber = chart.DifficultyNumber,
            JacketPath = chart.JacketPath.Trim(),
            TechStats = chart.TechStats,
            SongOrder = songOrder
        });
    }
}
