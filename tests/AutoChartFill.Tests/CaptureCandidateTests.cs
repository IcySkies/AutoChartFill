using AutoChartFill.Core;

namespace AutoChartFill.Tests;

public sealed class CaptureCandidateTests
{
    [Fact]
    public void FormatsSongAndNormalizesDifficulty()
    {
        var result = CaptureCandidateResult.From(Chart("MIDDLE"), 4);

        Assert.True(result.Accepted);
        Assert.Equal("MID", result.Capture!.DifficultyCode);
        Assert.Equal("Song - Artist", result.Capture.SongText);
        Assert.Equal(4, result.Capture.SongOrder);
    }

    [Fact]
    public void RejectsAllZeroStats()
    {
        var result = CaptureCandidateResult.From(Chart("OPENING") with { TechStats = new() }, 0);

        Assert.False(result.Accepted);
        Assert.Contains("no chart data", result.Message);
    }

    [Theory]
    [InlineData("", "Song", "Artist", "OPENING")]
    [InlineData("id", "", "Artist", "OPENING")]
    [InlineData("id", "Song", "", "OPENING")]
    [InlineData("id", "Song", "Artist", "")]
    public void RejectsRequiredMetadata(string id, string title, string artist, string difficulty)
    {
        var result = CaptureCandidateResult.From(Chart(difficulty) with { ChartId = id, Title = title, Artist = artist }, 0);
        Assert.False(result.Accepted);
    }

    [Fact]
    public void OrdersEncoreBeforeBackstageAndShatter()
    {
        Assert.True(DifficultyCodes.OrderOf("ENC") < DifficultyCodes.OrderOf("BKS"));
        Assert.True(DifficultyCodes.OrderOf("BKS") < DifficultyCodes.OrderOf("SHATTER"));
    }

    [Fact]
    public void MapsCustomDifficultyToShatter()
    {
        Assert.Equal("SHATTER", DifficultyCodes.Normalize("custom"));
    }

    internal static GameChartSnapshot Chart(string difficulty, string id = "song-1", decimal level = 12.3m) => new()
    {
        ChartId = id,
        Title = " Song ",
        Artist = " Artist ",
        Charter = "Charter",
        DifficultyCode = difficulty,
        RawDifficultyName = difficulty,
        DifficultyNumber = level,
        TechStats = new() { Chip = 1, Tech = 2, Stream = 3, Chord = 4, Burst = 5, Gimmick = 6 }
    };
}
