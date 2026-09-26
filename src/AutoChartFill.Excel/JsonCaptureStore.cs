using System.Text.Json;
using AutoChartFill.Core;

namespace AutoChartFill.Excel;

public sealed class JsonCaptureStore : ICaptureStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string SidecarPath(string workbookPath) => Path.GetFullPath(workbookPath) + ".autochartfill.json";

    public async Task<CaptureState> LoadAsync(string workbookPath, CancellationToken cancellationToken = default)
    {
        var path = SidecarPath(workbookPath);
        if (!File.Exists(path)) return new();
        await using var stream = File.OpenRead(path);
        var state = await JsonSerializer.DeserializeAsync<CaptureState>(stream, Options, cancellationToken);
        if (state is null || state.SchemaVersion != 1) throw new InvalidOperationException("The capture sidecar has an unsupported format.");
        state.Records ??= [];
        return state;
    }

    public async Task SaveAsync(string workbookPath, CaptureState state, CancellationToken cancellationToken = default)
    {
        var path = SidecarPath(workbookPath);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
            {
                await JsonSerializer.SerializeAsync(stream, state, Options, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public Task DeleteAsync(string workbookPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = SidecarPath(workbookPath);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }
}
