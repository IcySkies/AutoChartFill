using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using AutoChartFill.Core;

namespace AutoChartFill.GameBridge;

public sealed class TcpGameEventSource : IGameEventSource
{
    public const int SupportedProtocolVersion = 1;
    private const int MaxFrameBytes = 1024 * 1024;
    private readonly JsonSerializerOptions _jsonOptions = CreateJsonOptions();
    private TcpListener? _listener;
    private CancellationTokenSource? _stop;

    public bool IsListening { get; private set; }
    public int Port { get; }
    public event EventHandler<GameEventEnvelope>? EventReceived;
    public event EventHandler<string>? StatusChanged;

    public TcpGameEventSource(int port = 28745)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        Port = port;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsListening) return Task.CompletedTask;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listener = new TcpListener(IPAddress.Loopback, Port);
        _listener.Start();
        IsListening = true;
        StatusChanged?.Invoke(this, $"Listening on 127.0.0.1:{Port}.");
        _ = AcceptLoopAsync(_stop.Token);
        return Task.CompletedTask;
    }

    public Task FindRelayAsync() => Task.CompletedTask;

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await _listener!.AcceptTcpClientAsync(cancellationToken);
                StatusChanged?.Invoke(this, "Game connected; waiting for selections.");
                _ = HandleClientAsync(client, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) { StatusChanged?.Invoke(this, $"Game bridge failed: {ex.Message}"); }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        long lastSequence = 0;
        try
        {
            using (client)
            await using (var stream = client.GetStream())
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var header = new byte[sizeof(int)];
                    if (!await ReadExactlyAsync(stream, header, cancellationToken)) break;
                    var length = BinaryPrimitives.ReadInt32LittleEndian(header);
                    if (length is <= 0 or > MaxFrameBytes) break;
                    var payload = new byte[length];
                    if (!await ReadExactlyAsync(stream, payload, cancellationToken)) break;
                    while (payload.Length > 0 && payload[^1] == 0) Array.Resize(ref payload, payload.Length - 1);
                    GameEventEnvelope? envelope;
                    try { envelope = JsonSerializer.Deserialize<GameEventEnvelope>(payload, _jsonOptions); }
                    catch (JsonException ex) { StatusChanged?.Invoke(this, $"Rejected malformed game event: {ex.Message}"); continue; }
                    if (envelope is null || envelope.ProtocolVersion != SupportedProtocolVersion) continue;
                    if (envelope.Sequence <= lastSequence) continue;
                    lastSequence = envelope.Sequence;
                    EventReceived?.Invoke(this, envelope);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (IOException) { StatusChanged?.Invoke(this, "Game disconnected; waiting for reconnection."); }
        catch (SocketException) { StatusChanged?.Invoke(this, "Game disconnected; waiting for reconnection."); }
    }

    private static async Task<bool> ReadExactlyAsync(NetworkStream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken);
            if (count == 0) return false;
            offset += count;
        }
        return true;
    }

    public Task StopAsync()
    {
        if (!IsListening) return Task.CompletedTask;
        IsListening = false;
        _stop?.Cancel();
        _listener?.Stop();
        _stop?.Dispose();
        _stop = null;
        _listener = null;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() { StopAsync(); return ValueTask.CompletedTask; }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new FlexibleDecimalConverter());
        options.Converters.Add(new IntegralConverter<int>(value => decimal.ToInt32(value)));
        options.Converters.Add(new IntegralConverter<long>(value => decimal.ToInt64(value)));
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private sealed class FlexibleDecimalConverter : JsonConverter<decimal>
    {
        public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetDecimal(out var number)) return number;
            if (reader.TokenType == JsonTokenType.String) return decimal.TryParse(reader.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var text) ? text : 0m;
            if (reader.TokenType == JsonTokenType.Null) return 0m;
            if (reader.TokenType == JsonTokenType.Number) { reader.Skip(); return 0m; }
            throw new JsonException("Expected a finite decimal.");
        }

        public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
    }
}

internal sealed class IntegralConverter<T>(Func<decimal, T> convert) : JsonConverter<T> where T : struct
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetDecimal(out var number) && number == decimal.Truncate(number)) return convert(number);
        if (reader.TokenType == JsonTokenType.String && decimal.TryParse(reader.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var text) && text == decimal.Truncate(text)) return convert(text);
        throw new JsonException("Expected an integral number.");
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
}
