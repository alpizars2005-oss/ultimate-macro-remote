using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text.RegularExpressions;

namespace UltimateRemoteAgent.Transport;

public sealed partial class SystemClientWebSocket : IAgentWebSocket
{
    private readonly ClientWebSocket _socket;

    private SystemClientWebSocket(ClientWebSocket socket)
    {
        _socket = socket;
    }

    public WebSocketState State => _socket.State;

    public WebSocketCloseStatus? CloseStatus => _socket.CloseStatus;

    public string? CloseStatusDescription => _socket.CloseStatusDescription;

    public static SystemClientWebSocket Create(string deviceCredential)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceCredential);
        if (!DeviceCredentialRegex().IsMatch(deviceCredential))
        {
            throw new ArgumentException("Device credential has an invalid format.", nameof(deviceCredential));
        }

        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = Timeout.InfiniteTimeSpan;
        socket.Options.HttpVersion = HttpVersion.Version11;
        socket.Options.HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact;
        socket.Options.SetRequestHeader(
            "Authorization",
            new AuthenticationHeaderValue("Bearer", deviceCredential).ToString());

        // Deliberately do not install a certificate validation callback. The platform
        // trust store and hostname validation remain authoritative.
        return new SystemClientWebSocket(socket);
    }

    public Task ConnectAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        ValidateEndpoint(endpoint);
        return _socket.ConnectAsync(endpoint, cancellationToken);
    }

    public ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken) =>
        _socket.ReceiveAsync(buffer, cancellationToken);

    public ValueTask SendAsync(
        ReadOnlyMemory<byte> buffer,
        WebSocketMessageType messageType,
        bool endOfMessage,
        CancellationToken cancellationToken) =>
        _socket.SendAsync(buffer, messageType, endOfMessage, cancellationToken);

    public Task CloseOutputAsync(
        WebSocketCloseStatus closeStatus,
        string? statusDescription,
        CancellationToken cancellationToken) =>
        _socket.CloseOutputAsync(closeStatus, statusDescription, cancellationToken);

    public ValueTask DisposeAsync()
    {
        _socket.Dispose();
        return ValueTask.CompletedTask;
    }

    public static void ValidateEndpoint(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri
            || !string.Equals(endpoint.Scheme, Uri.UriSchemeWss, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(endpoint.Host)
            || !string.IsNullOrEmpty(endpoint.UserInfo)
            || !string.IsNullOrEmpty(endpoint.Query)
            || !string.IsNullOrEmpty(endpoint.Fragment)
            || !string.Equals(endpoint.AbsolutePath, "/remote/v1/agent", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Agent endpoint must be an absolute WSS /remote/v1/agent URI without credentials, query, or fragment.",
                nameof(endpoint));
        }
    }

    [GeneratedRegex(
        "^urad_v1\\.[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\\.[A-Za-z0-9_-]{43}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex DeviceCredentialRegex();
}
