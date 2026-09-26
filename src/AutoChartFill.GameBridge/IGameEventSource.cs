using AutoChartFill.Core;

namespace AutoChartFill.GameBridge;

public interface IGameEventSource : IAsyncDisposable
{
    bool IsListening { get; }
    int Port { get; }
    event EventHandler<GameEventEnvelope>? EventReceived;
    event EventHandler<string>? StatusChanged;
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync();
}
