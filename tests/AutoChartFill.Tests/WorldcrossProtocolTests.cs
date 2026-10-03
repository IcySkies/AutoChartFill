using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AutoChartFill.Core;
using AutoChartFill.GameBridge;

namespace AutoChartFill.Tests;

public sealed class WorldcrossProtocolTests
{
    [Theory]
    [InlineData("\"1.0\"", "\"1.0\"", "\"Selection\"")]
    [InlineData("1.0", "1.0", "0")]
    [InlineData("1", "1", "\"LobbySelection\"")]
    public async Task RelayFramesAcceptBothGameMakerNumberAndEnumEncodings(string protocol, string sequence, string kind)
    {
        var root = Path.Combine(Path.GetTempPath(), "acf-relay-encoding", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var sharedPath = Path.Combine(root, "bridge-relay.json");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        await WriteDiscoveryAsync(sharedPath, ((IPEndPoint)listener.LocalEndpoint).Port);
        try
        {
            await using var source = new RelayGameEventSource(root, Path.Combine(root, "missing.exe"), sharedPath);
            var received = new TaskCompletionSource<GameEventEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
            source.EventReceived += (_, envelope) => received.TrySetResult(envelope);
            await source.StartAsync();
            using var subscriber = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(3));
            var payload = Encoding.UTF8.GetBytes($"{{\"protocolVersion\":{protocol},\"sequence\":{sequence},\"kind\":{kind}}}\0");
            var frame = new byte[4 + payload.Length];
            BitConverter.GetBytes(payload.Length).CopyTo(frame, 0);
            payload.CopyTo(frame, 4);
            await subscriber.GetStream().WriteAsync(frame);
            var envelope = await received.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(1, envelope.ProtocolVersion);
            Assert.Equal(1, envelope.Sequence);
            Assert.Equal(kind.Contains("LobbySelection", StringComparison.Ordinal) ? GameEventKind.LobbySelection : GameEventKind.Selection, envelope.Kind);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task RelaySourceUsesDiscoveryWithoutRequiringProcessId()
    {
        var root = Path.Combine(Path.GetTempPath(), "acf-relay", Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(root, "AutoChartSwitchV2");
        Directory.CreateDirectory(directory);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var subscriberPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        await File.WriteAllTextAsync(Path.Combine(directory, "bridge-relay.json"), JsonSerializer.Serialize(new
        {
            protocolVersion = 1,
            host = "127.0.0.1",
            gamePort = 28745,
            subscriberPort,
            processId = int.MaxValue
        }));

        try
        {
            await using var source = new RelayGameEventSource(root, Path.Combine(root, "missing-relay.exe"));
            await source.StartAsync();
            using var subscriber = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(subscriberPort, source.Port);
        }
        finally
        {
            listener.Stop();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task RelaySourceSkipsStaleGamePathAndConnectsToSharedDiscovery()
    {
        var root = Path.Combine(Path.GetTempPath(), "acf-relay-fallback", Guid.NewGuid().ToString("N"));
        var gamePath = Path.Combine(root, "configured-game");
        var localDirectory = Path.Combine(gamePath, "AutoChartSwitchV2");
        Directory.CreateDirectory(localDirectory);
        using var staleListener = new TcpListener(IPAddress.Loopback, 0);
        staleListener.Start();
        var stalePort = ((IPEndPoint)staleListener.LocalEndpoint).Port;
        staleListener.Stop();
        using var liveListener = new TcpListener(IPAddress.Loopback, 0);
        liveListener.Start();
        var livePort = ((IPEndPoint)liveListener.LocalEndpoint).Port;
        var sharedPath = Path.Combine(root, "shared", "bridge-relay.json");
        Directory.CreateDirectory(Path.GetDirectoryName(sharedPath)!);
        await WriteDiscoveryAsync(Path.Combine(localDirectory, "bridge-relay.json"), stalePort);
        await WriteDiscoveryAsync(sharedPath, livePort);

        try
        {
            await using var source = new RelayGameEventSource(gamePath, Path.Combine(root, "missing.exe"), sharedPath);
            var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            source.StatusChanged += (_, status) =>
            {
                if (status.StartsWith("Relay subscriber connected", StringComparison.Ordinal)) connected.TrySetResult();
            };
            await source.StartAsync();
            using var subscriber = await liveListener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(3));
            await connected.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(livePort, source.Port);
        }
        finally
        {
            liveListener.Stop();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task FindRelayWakesSubscriberBeforeAutomaticRetry()
    {
        var root = Path.Combine(Path.GetTempPath(), "acf-relay-wake", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var sharedPath = Path.Combine(root, "bridge-relay.json");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            await using var source = new RelayGameEventSource("", Path.Combine(root, "missing.exe"), sharedPath);
            var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            source.StatusChanged += (_, status) =>
            {
                if (status.StartsWith("Relay unavailable", StringComparison.Ordinal)) waiting.TrySetResult();
                if (status.StartsWith("Relay subscriber connected", StringComparison.Ordinal)) connected.TrySetResult();
            };
            await source.StartAsync();
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await WriteDiscoveryAsync(sharedPath, port);
            await source.FindRelayAsync();
            using var subscriber = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromMilliseconds(750));
            await connected.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(port, source.Port);
        }
        finally
        {
            listener.Stop();
            Directory.Delete(root, true);
        }
    }

    private static Task WriteDiscoveryAsync(string path, int port) =>
        File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            protocolVersion = 1,
            host = "127.0.0.1",
            gamePort = 28745,
            subscriberPort = port,
            processId = int.MaxValue
        }));

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
