using System.Buffers;
using System.Net.WebSockets;
using UltimateRemoteAgent.Protocol;

namespace UltimateRemoteAgent.Transport;

public sealed record WebSocketInboundMessage(
    WebSocketMessageType MessageType,
    ReadOnlyMemory<byte> Payload,
    WebSocketCloseStatus? CloseStatus = null,
    string? CloseDescription = null)
{
    public bool IsClose => MessageType == WebSocketMessageType.Close;
}

public static class WebSocketMessageReader
{
    private const int ReceiveBufferBytes = 8 * 1024;
    private const int MaximumFragments = 1024;

    public static async ValueTask<WebSocketInboundMessage> ReadAsync(
        IAgentWebSocket socket,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(socket);
        byte[] receiveBuffer = ArrayPool<byte>.Shared.Rent(ReceiveBufferBytes);
        var payload = new ArrayBufferWriter<byte>();
        WebSocketMessageType? messageType = null;
        int fragmentCount = 0;
        try
        {
            while (true)
            {
                ValueWebSocketReceiveResult result = await socket.ReceiveAsync(
                    receiveBuffer.AsMemory(0, ReceiveBufferBytes),
                    cancellationToken).ConfigureAwait(false);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    if (messageType is not null)
                    {
                        throw new ProtocolException(
                            "TRUNCATED_MESSAGE",
                            "Connection closed during a fragmented message.");
                    }

                    return new WebSocketInboundMessage(
                        WebSocketMessageType.Close,
                        ReadOnlyMemory<byte>.Empty,
                        socket.CloseStatus,
                        socket.CloseStatusDescription);
                }

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    throw new ProtocolException(
                        "TEXT_REQUIRED",
                        "Protocol messages must be WebSocket text messages.");
                }

                if (messageType is not null && messageType != result.MessageType)
                {
                    throw new ProtocolException(
                        "INVALID_FRAGMENT",
                        "WebSocket message type changed between fragments.");
                }

                messageType = result.MessageType;
                fragmentCount++;
                if (fragmentCount > MaximumFragments || (result.Count == 0 && !result.EndOfMessage))
                {
                    throw new ProtocolException(
                        "INVALID_FRAGMENT",
                        "WebSocket message uses invalid or excessive fragmentation.");
                }

                if (result.Count < 0 || result.Count > ReceiveBufferBytes)
                {
                    throw new ProtocolException("INVALID_FRAGMENT", "Invalid WebSocket fragment size.");
                }

                if (payload.WrittenCount > ProtocolConstants.MaximumMessageBytes - result.Count)
                {
                    throw new ProtocolException(
                        "MESSAGE_TOO_LARGE",
                        "Message exceeds the protocol size limit.");
                }

                payload.Write(receiveBuffer.AsSpan(0, result.Count));
                if (result.EndOfMessage)
                {
                    return new WebSocketInboundMessage(
                        WebSocketMessageType.Text,
                        payload.WrittenMemory.ToArray());
                }
            }
        }
        finally
        {
            Array.Clear(receiveBuffer, 0, receiveBuffer.Length);
            ArrayPool<byte>.Shared.Return(receiveBuffer);
        }
    }
}
