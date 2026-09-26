using System.Text.Json;
using System.IO;

namespace AutoChartFill.App;

public sealed class AppPersistence
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public static string RootPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SVC-AS", "AutoChartFill");
    public static string SettingsPath => Path.Combine(RootPath, "settings.json");

    public async Task<AppSettings> LoadAsync()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new();
            await using var stream = File.OpenRead(SettingsPath);
            return await JsonSerializer.DeserializeAsync<AppSettings>(stream, Options) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            try { File.Move(SettingsPath, SettingsPath + $".corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}", true); } catch { }
            return new();
        }
    }

    public async Task SaveAsync(AppSettings settings)
    {
        await _gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(RootPath);
            var temporary = SettingsPath + $".{Guid.NewGuid():N}.tmp";
            try
            {
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
                    await JsonSerializer.SerializeAsync(stream, settings, Options);
                File.Move(temporary, SettingsPath, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { _gate.Release(); }
    }
}
