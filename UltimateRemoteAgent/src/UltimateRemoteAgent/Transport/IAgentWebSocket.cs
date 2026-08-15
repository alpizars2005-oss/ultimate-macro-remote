using System.Net.WebSockets;

namespace UltimateRemoteAgent.Transport;

public interface IAgentWebSocket : IAsyncDisposable
{
    WebSocketState State { get; }

    WebSocketCloseStatus? CloseStatus { get; }

    string? CloseStatusDescription { get; }

    Task ConnectAsync(Uri endpoint, CancellationToken cancellationToken);

    ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken);

    ValueTask SendAsync(
        ReadOnlyMemory<byte> buffer,
        WebSocketMessageType messageType,
        bool endOfMessage,
        CancellationToken cancellationToken);

    Task CloseOutputAsync(
        WebSocketCloseStatus closeStatus,
        string? statusDescription,
        CancellationToken cancellationToken);
}
