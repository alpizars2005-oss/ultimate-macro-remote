using System.Net.WebSockets;
using UltimateRemoteAgent.Protocol;

namespace UltimateRemoteAgent.Transport;

public sealed class AgentWebSocketConnection : IAsyncDisposable
{
    private readonly IAgentWebSocket _socket;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly SemaphoreSlim _receiveGate = new(1, 1);
    private bool _disposed;

    public AgentWebSocketConnection(IAgentWebSocket socket)
    {
        _socket = socket ?? throw new ArgumentNullException(nameof(socket));
    }

    public WebSocketState State => _socket.State;

    public Task ConnectAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _socket.ConnectAsync(endpoint, cancellationToken);
    }

    public async ValueTask SendTextAsync(
        ReadOnlyMemory<byte> utf8Json,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (utf8Json.Length > ProtocolConstants.MaximumMessageBytes)
        {
            throw new ProtocolException(
                "MESSAGE_TOO_LARGE",
                "Message exceeds the protocol size limit.");
        }

        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _socket.SendAsync(
                utf8Json,
                WebSocketMessageType.Text,
                endOfMessage: true,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public async ValueTask<WebSocketInboundMessage> ReadAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _receiveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await WebSocketMessageReader.ReadAsync(
                _socket,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _receiveGate.Release();
        }
    }

    public async Task CloseOutputAsync(
        WebSocketCloseStatus status,
        string description,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await _socket.CloseOutputAsync(
                    status,
                    description,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _socket.DisposeAsync().ConfigureAwait(false);
        _sendGate.Dispose();
        _receiveGate.Dispose();
    }
}
