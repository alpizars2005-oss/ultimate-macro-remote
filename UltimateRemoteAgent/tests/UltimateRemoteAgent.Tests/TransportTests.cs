using System.Net.WebSockets;
using System.Text;
using UltimateRemoteAgent.Protocol;
using UltimateRemoteAgent.Transport;

namespace UltimateRemoteAgent.Tests;

[TestClass]
public sealed class TransportTests
{
    [TestMethod]
    public async Task MessageReaderReassemblesFragmentedUtf8Text()
    {
        byte[] payload = Encoding.UTF8.GetBytes("{\"name\":\"café\"}");
        var socket = new FakeSocket(
            Frame.Text(payload[..12], endOfMessage: false),
            Frame.Text(payload[12..], endOfMessage: true));

        WebSocketInboundMessage message = await WebSocketMessageReader.ReadAsync(
            socket,
            CancellationToken.None);

        Assert.AreEqual(WebSocketMessageType.Text, message.MessageType);
        CollectionAssert.AreEqual(payload, message.Payload.ToArray());
    }

    [TestMethod]
    public async Task MessageReaderRejectsBinaryAndOversizedMessages()
    {
        var binary = new FakeSocket(Frame.Binary([1, 2, 3], endOfMessage: true));
        ProtocolException binaryError = await Assert.ThrowsExactlyAsync<ProtocolException>(
            async () =>
            {
                await WebSocketMessageReader.ReadAsync(binary, CancellationToken.None);
            });
        Assert.AreEqual("TEXT_REQUIRED", binaryError.Code);

        byte[] chunk = new byte[8 * 1024];
        var frames = Enumerable.Range(0, 9)
            .Select(index => Frame.Text(chunk, endOfMessage: index == 8))
            .ToArray();
        var oversized = new FakeSocket(frames);
        ProtocolException sizeError = await Assert.ThrowsExactlyAsync<ProtocolException>(
            async () =>
            {
                await WebSocketMessageReader.ReadAsync(oversized, CancellationToken.None);
            });
        Assert.AreEqual("MESSAGE_TOO_LARGE", sizeError.Code);
    }

    [TestMethod]
    public async Task MessageReaderReturnsCloseMetadataWithoutTreatingItAsJson()
    {
        var socket = new FakeSocket(Frame.Close());
        socket.CloseStatusValue = WebSocketCloseStatus.PolicyViolation;
        socket.CloseDescriptionValue = "UNSUPPORTED_PROTOCOL";

        WebSocketInboundMessage message = await WebSocketMessageReader.ReadAsync(
            socket,
            CancellationToken.None);

        Assert.IsTrue(message.IsClose);
        Assert.AreEqual(WebSocketCloseStatus.PolicyViolation, message.CloseStatus);
        Assert.AreEqual("UNSUPPORTED_PROTOCOL", message.CloseDescription);
        Assert.AreEqual(0, message.Payload.Length);
    }

    [TestMethod]
    public async Task MessageReaderRejectsCloseDuringAFragmentedMessage()
    {
        var socket = new FakeSocket(
            Frame.Text([1], endOfMessage: false),
            Frame.Close());

        ProtocolException exception = await Assert.ThrowsExactlyAsync<ProtocolException>(
            async () =>
            {
                await WebSocketMessageReader.ReadAsync(socket, CancellationToken.None);
            });

        Assert.AreEqual("TRUNCATED_MESSAGE", exception.Code);
    }

    [TestMethod]
    public async Task ConnectionSerializesConcurrentTextSends()
    {
        var socket = new FakeSocket();
        socket.DelaySends = true;
        await using var connection = new AgentWebSocketConnection(socket);

        Task first = connection.SendTextAsync(new byte[] { 1 }, CancellationToken.None).AsTask();
        await socket.FirstSendEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task second = connection.SendTextAsync(new byte[] { 2 }, CancellationToken.None).AsTask();
        await Task.Delay(20);

        Assert.AreEqual(1, socket.SendCalls);
        socket.ReleaseSends.TrySetResult();
        await Task.WhenAll(first, second);
        Assert.AreEqual(1, socket.MaximumConcurrentSends);
        Assert.AreEqual(2, socket.SendCalls);
    }

    [TestMethod]
    public void SystemSocketRequiresExactAuthenticatedWssEndpointShape()
    {
        SystemClientWebSocket.ValidateEndpoint(
            new Uri("wss://remote.example.test/remote/v1/agent"));

        Uri[] rejected =
        {
            new("ws://remote.example.test/remote/v1/agent"),
            new("wss://user@remote.example.test/remote/v1/agent"),
            new("wss://remote.example.test/other"),
            new("wss://remote.example.test/remote/v1/agent?token=secret"),
        };
        foreach (Uri endpoint in rejected)
        {
            Assert.ThrowsExactly<ArgumentException>(
                () => SystemClientWebSocket.ValidateEndpoint(endpoint));
        }
    }

    private sealed record Frame(
        byte[] Payload,
        WebSocketMessageType MessageType,
        bool EndOfMessage)
    {
        internal static Frame Text(byte[] payload, bool endOfMessage) =>
            new(payload, WebSocketMessageType.Text, endOfMessage);

        internal static Frame Binary(byte[] payload, bool endOfMessage) =>
            new(payload, WebSocketMessageType.Binary, endOfMessage);

        internal static Frame Close() =>
            new([], WebSocketMessageType.Close, true);
    }

    private sealed class FakeSocket(params Frame[] frames) : IAgentWebSocket
    {
        private readonly Queue<Frame> _frames = new(frames);
        private int _concurrentSends;

        internal bool DelaySends { get; set; }

        internal int SendCalls { get; private set; }

        internal int MaximumConcurrentSends { get; private set; }

        internal TaskCompletionSource FirstSendEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ReleaseSends { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal WebSocketCloseStatus? CloseStatusValue { get; set; }

        internal string? CloseDescriptionValue { get; set; }

        public WebSocketState State => WebSocketState.Open;

        public WebSocketCloseStatus? CloseStatus => CloseStatusValue;

        public string? CloseStatusDescription => CloseDescriptionValue;

        public Task ConnectAsync(Uri endpoint, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken)
        {
            Frame frame = _frames.Dequeue();
            frame.Payload.CopyTo(buffer);
            return ValueTask.FromResult(
                new ValueWebSocketReceiveResult(
                    frame.Payload.Length,
                    frame.MessageType,
                    frame.EndOfMessage));
        }

        public async ValueTask SendAsync(
            ReadOnlyMemory<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            SendCalls++;
            int concurrent = Interlocked.Increment(ref _concurrentSends);
            MaximumConcurrentSends = Math.Max(MaximumConcurrentSends, concurrent);
            FirstSendEntered.TrySetResult();
            try
            {
                if (DelaySends)
                {
                    await ReleaseSends.Task.WaitAsync(cancellationToken);
                }
            }
            finally
            {
                Interlocked.Decrement(ref _concurrentSends);
            }
        }

        public Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
