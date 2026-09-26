using System.Net;
using System.Net.Sockets;
using System.Text;
using AutoChartFill.Core;
using AutoChartFill.GameBridge;

namespace AutoChartFill.Tests;

public sealed class WorldcrossProtocolTests
{
    [Fact]
    public async Task TcpSourceAcceptsStringWorldcrossRoomAndOptionalEmptyPayload()
    {
        var port = GetFreePort();
        await using var source = new TcpGameEventSource(port);
        var received = new TaskCompletionSource<GameEventEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.EventReceived += (_, value) => received.TrySetResult(value);
        await source.StartAsync();

        await SendRawEventAsync(port, "{\"protocolVersion\":1.0,\"sequence\":1.0,\"kind\":\"WorldcrossRoom\",\"worldcross\":{\"players\":[]}}");
        var envelope = await received.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(GameEventKind.WorldcrossRoom, envelope.Kind);
        Assert.Empty(envelope.Worldcross!.Players);
        Assert.Null(envelope.Chart);
    }

    [Fact]
    public async Task TcpSourceAcceptsNumericWorldcrossGameplayKindAndNumericStrings()
    {
        var port = GetFreePort();
        await using var source = new TcpGameEventSource(port);
        var received = new TaskCompletionSource<GameEventEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.EventReceived += (_, value) => received.TrySetResult(value);
        await source.StartAsync();

        const string json = "{\"protocolVersion\":1.0,\"sequence\":2.0,\"kind\":7,\"worldcross\":{\"players\":[{\"steamId64\":\"76561198000000004\",\"rating\":\"9.5\",\"class\":\"3\",\"score\":\"1010000.0\",\"lastPlayScore\":\"1010000\",\"label\":\"\"}]}}";
        await SendRawEventAsync(port, json);
        var envelope = await received.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(GameEventKind.WorldcrossGameplay, envelope.Kind);
        var player = Assert.Single(envelope.Worldcross!.Players);
        Assert.Equal("76561198000000004", player.SteamId64);
        Assert.Equal(9.5m, player.Rating);
        Assert.Equal(3, player.Class);
        Assert.Equal(1010000m, player.Score);
        Assert.Equal(1010000m, player.LastPlayScore);
        Assert.Equal("", player.Label);
    }

    [Fact]
    public async Task TcpSourceAcceptsChartInfoHighlightEvent()
    {
        var port = GetFreePort();
        await using var source = new TcpGameEventSource(port);
        var received = new TaskCompletionSource<GameEventEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.EventReceived += (_, value) => received.TrySetResult(value);
        await source.StartAsync();

        await SendRawEventAsync(port, "{\"protocolVersion\":1,\"sequence\":3,\"kind\":\"ChartInfo\",\"chart\":{\"chartId\":\"highlight\",\"rawDifficultyName\":\"OPENING\"}}");
        var envelope = await received.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(GameEventKind.ChartInfo, envelope.Kind);
        Assert.Equal("highlight", envelope.Chart!.ChartId);
    }

    private static async Task SendRawEventAsync(int port, string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        await using var stream = client.GetStream();
        var frame = new byte[sizeof(int) + payload.Length];
        BitConverter.GetBytes(payload.Length).CopyTo(frame, 0);
        payload.CopyTo(frame, sizeof(int));
        await stream.WriteAsync(frame);
        await stream.FlushAsync();
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
