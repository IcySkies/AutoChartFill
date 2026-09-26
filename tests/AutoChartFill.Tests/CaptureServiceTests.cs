using AutoChartFill.Core;

namespace AutoChartFill.Tests;

public sealed class CaptureServiceTests
{
    [Fact]
    public async Task RepeatedDifficultyUpdatesInsteadOfDuplicating()
    {
        var store = new MemoryStore();
        var renderer = new MemoryRenderer();
        var service = new CaptureService(store, renderer);
        var settings = ValidSettings();

        await service.CaptureAsync(CaptureCandidateTests.Chart("OPENING", level: 5m), settings, "");
        var result = await service.CaptureAsync(CaptureCandidateTests.Chart("OPENING", level: 9m), settings, "");

        Assert.Single(result.State.Records);
        Assert.Equal(9m, result.State.Records[0].DifficultyNumber);
        Assert.Contains("Updated", result.Message);
    }

    [Fact]
    public async Task FailedRenderRemainsPendingAndRetryClearsIt()
    {
        var store = new MemoryStore();
        var renderer = new MemoryRenderer { Fail = true };
        var service = new CaptureService(store, renderer);
        var settings = ValidSettings();

        var captured = await service.CaptureAsync(CaptureCandidateTests.Chart("OPENING"), settings, "");
        Assert.True(captured.State.PendingRender);
        renderer.Fail = false;
        var retried = await service.RetryAsync(settings, "");

        Assert.False(retried.State.PendingRender);
        Assert.Equal(2, renderer.RenderCount);
    }

    [Fact]
    public async Task MappingChangesAreRejectedAfterFirstCapture()
    {
        var store = new MemoryStore();
        var service = new CaptureService(store, new MemoryRenderer());
        var settings = ValidSettings();
        await service.CaptureAsync(CaptureCandidateTests.Chart("OPENING"), settings, "");
        settings.Columns.Song = "Z";

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RetryAsync(settings, ""));
    }

    private static WorkbookCaptureSettings ValidSettings()
    {
        var path = Path.GetTempFileName() + ".xlsx";
        File.WriteAllBytes(path, [1]);
        return new() { WorkbookPath = path, WorksheetName = "Charts", FirstDataRow = 2 };
    }

    private sealed class MemoryStore : ICaptureStore
    {
        private CaptureState _state = new();
        public Task<CaptureState> LoadAsync(string workbookPath, CancellationToken cancellationToken = default) => Task.FromResult(Clone(_state));
        public Task SaveAsync(string workbookPath, CaptureState state, CancellationToken cancellationToken = default) { _state = Clone(state); return Task.CompletedTask; }
        public Task DeleteAsync(string workbookPath, CancellationToken cancellationToken = default) { _state = new(); return Task.CompletedTask; }
        private static CaptureState Clone(CaptureState state) => new()
        {
            SchemaVersion = state.SchemaVersion,
            MappingSignature = state.MappingSignature,
            ManagedStartRow = state.ManagedStartRow,
            RenderedRecordCount = state.RenderedRecordCount,
            PendingRender = state.PendingRender,
            Records = state.Records.ToList()
        };
    }

    private sealed class MemoryRenderer : IWorkbookRenderer
    {
        public bool Fail { get; set; }
        public int RenderCount { get; private set; }
        public Task<IReadOnlyList<string>> GetWorksheetNamesAsync(string workbookPath, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>(["Charts"]);
        public Task RenderAsync(WorkbookCaptureSettings settings, CaptureState state, string fallbackJacketPath, CancellationToken cancellationToken = default)
        {
            RenderCount++;
            if (Fail) throw new IOException("locked");
            state.RenderedRecordCount = state.Records.Count;
            return Task.CompletedTask;
        }
        public Task ResetAsync(WorkbookCaptureSettings settings, CaptureState state, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
