using AutoChartFill.Core;
using AutoChartFill.Excel;

namespace AutoChartFill.Tests;

public sealed class JsonCaptureStoreTests
{
    [Fact]
    public async Task PersistsAndDeletesSidecar()
    {
        var workbook = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.xlsx");
        var store = new JsonCaptureStore();
        var state = new CaptureState { MappingSignature = "abc", PendingRender = true, Records = [new() { GameChartId = "id", DifficultyCode = "OPN" }] };
        try
        {
            await store.SaveAsync(workbook, state);
            var loaded = await store.LoadAsync(workbook);
            Assert.Equal("abc", loaded.MappingSignature);
            Assert.True(loaded.PendingRender);
            Assert.Single(loaded.Records);
            await store.DeleteAsync(workbook);
            Assert.False(File.Exists(JsonCaptureStore.SidecarPath(workbook)));
        }
        finally { if (File.Exists(JsonCaptureStore.SidecarPath(workbook))) File.Delete(JsonCaptureStore.SidecarPath(workbook)); }
    }
}
