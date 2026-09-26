namespace AutoChartFill.Core;

public sealed class CaptureService
{
    private readonly ICaptureStore _store;
    private readonly IWorkbookRenderer _renderer;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public CaptureService(ICaptureStore store, IWorkbookRenderer renderer)
    {
        _store = store;
        _renderer = renderer;
    }

    public async Task<(CaptureState State, string Message)> CaptureAsync(
        GameChartSnapshot snapshot,
        WorkbookCaptureSettings settings,
        string fallbackJacketPath,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureSettings(settings);
            var state = await _store.LoadAsync(settings.WorkbookPath, cancellationToken);
            EnsureCompatible(state, settings);
            var existingSong = state.Records.Where(x => x.GameChartId.Equals(snapshot.ChartId.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            var nextOrder = existingSong.Count > 0 ? existingSong[0].SongOrder : state.Records.Select(x => x.SongOrder).DefaultIfEmpty(-1).Max() + 1;
            var candidate = CaptureCandidateResult.From(snapshot, nextOrder);
            if (!candidate.Accepted || candidate.Capture is null) return (state, candidate.Message);

            var index = state.Records.FindIndex(x => x.Key.Equals(candidate.Capture.Key, StringComparison.OrdinalIgnoreCase));
            var updated = index >= 0;
            if (updated) state.Records[index] = candidate.Capture with { SongOrder = state.Records[index].SongOrder };
            else state.Records.Add(candidate.Capture);
            state.MappingSignature = settings.MappingSignature();
            state.PendingRender = true;
            await _store.SaveAsync(settings.WorkbookPath, state, cancellationToken);

            try
            {
                await _renderer.RenderAsync(settings, state, fallbackJacketPath, cancellationToken);
                state.PendingRender = false;
                await _store.SaveAsync(settings.WorkbookPath, state, cancellationToken);
                return (state, updated ? "Updated the existing difficulty row." : "Captured the highlighted difficulty.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                state.PendingRender = true;
                return (state, $"Capture saved and pending workbook retry: {ex.Message}");
            }
        }
        finally { _gate.Release(); }
    }

    public async Task<(CaptureState State, string Message)> RetryAsync(
        WorkbookCaptureSettings settings,
        string fallbackJacketPath,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureSettings(settings);
            var state = await _store.LoadAsync(settings.WorkbookPath, cancellationToken);
            EnsureCompatible(state, settings);
            if (state.Records.Count == 0) return (state, "There are no captured rows to render.");
            try
            {
                await _renderer.RenderAsync(settings, state, fallbackJacketPath, cancellationToken);
                state.PendingRender = false;
                await _store.SaveAsync(settings.WorkbookPath, state, cancellationToken);
                return (state, "Workbook is up to date.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                state.PendingRender = true;
                try { await _store.SaveAsync(settings.WorkbookPath, state, cancellationToken); }
                catch (Exception saveException) when (saveException is IOException or UnauthorizedAccessException) { }
                return (state, $"Workbook is still pending: {ex.Message}");
            }
        }
        finally { _gate.Release(); }
    }

    public async Task ResetAsync(WorkbookCaptureSettings settings, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureSettings(settings);
            var state = await _store.LoadAsync(settings.WorkbookPath, cancellationToken);
            EnsureCompatible(state, settings);
            if (state.RenderedRecordCount > 0) await _renderer.ResetAsync(settings, state, cancellationToken);
            await _store.DeleteAsync(settings.WorkbookPath, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    private static void EnsureSettings(WorkbookCaptureSettings settings)
    {
        var errors = settings.Validate();
        if (errors.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
    }

    private static void EnsureCompatible(CaptureState state, WorkbookCaptureSettings settings)
    {
        if (state.Records.Count > 0 && !state.MappingSignature.Equals(settings.MappingSignature(), StringComparison.Ordinal))
            throw new InvalidOperationException("Workbook mapping is locked. Reset tracking before changing it.");
    }
}
