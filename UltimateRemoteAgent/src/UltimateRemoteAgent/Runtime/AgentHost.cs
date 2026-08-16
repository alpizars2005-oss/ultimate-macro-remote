using System.Net.WebSockets;
using UltimateRemoteAgent.Commands;
using UltimateRemoteAgent.Enrollment;
using UltimateRemoteAgent.Local;
using UltimateRemoteAgent.Protocol;
using UltimateRemoteAgent.Transport;

namespace UltimateRemoteAgent.Runtime;

internal sealed class AgentHost
{
    private const string AgentVersion = "0.4.0";
    private readonly EnrollmentRecord _enrollment;
    private readonly IRemoteLocalBridge _bridge;
    private readonly FullJitterReconnectPolicy _reconnectPolicy;

    internal AgentHost(
        EnrollmentRecord enrollment,
        IRemoteLocalBridge bridge,
        FullJitterReconnectPolicy? reconnectPolicy = null)
    {
        _enrollment = EnrollmentValidator.Validate(enrollment, requireFiles: true);
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _reconnectPolicy = reconnectPolicy ?? FullJitterReconnectPolicy.CreateDefault();
    }

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        int consecutiveFailures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RunConnectionAsync(cancellationToken).ConfigureAwait(false);
                consecutiveFailures = 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (ProtocolException exception) when (IsTerminalProtocolFailure(exception))
            {
                throw new AgentRuntimeException(exception.Code, exception);
            }
            catch (EnrollmentException exception)
            {
                throw new AgentRuntimeException(exception.Code, exception);
            }
            catch (Exception exception) when (IsTransient(exception))
            {
                consecutiveFailures = Math.Min(consecutiveFailures + 1, 30);
                SafeLog.Warning("AGENT_CONNECTION_LOST");
            }

            TimeSpan delay = _reconnectPolicy.GetDelay(consecutiveFailures);
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RunConnectionAsync(CancellationToken cancellationToken)
    {
        await using IAgentWebSocket socket = SystemClientWebSocket.Create(_enrollment.DeviceCredential);
        await using var connection = new AgentWebSocketConnection(socket);
        await connection.ConnectAsync(_enrollment.WebSocketUri, cancellationToken).ConfigureAwait(false);

        MacroSnapshot initialSnapshot = await _bridge.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        await connection.SendTextAsync(
            ProtocolCodec.EncodeHello(AgentVersion, initialSnapshot),
            cancellationToken).ConfigureAwait(false);

        WebSocketInboundMessage first = await connection.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (first.IsClose)
        {
            ThrowForCloseBeforeWelcome(first);
        }
        WelcomeMessage welcome = ProtocolCodec.ParseServerMessage(first.Payload) as WelcomeMessage
            ?? throw new ProtocolException("WELCOME_REQUIRED", "First server message must be WELCOME.");

        var clock = new ServerSynchronizedClock();
        clock.Synchronize(welcome.ServerTime);
        using var dispatcher = new RemoteCommandDispatcher(_bridge, clock.GetUtcNow);

        foreach (ReconciliationCommand reconciliation in welcome.ReconcileCommands)
        {
            await dispatcher.ReconcileAsync(
                reconciliation,
                connection.SendTextAsync,
                cancellationToken).ConfigureAwait(false);
        }

        using var connectionLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task heartbeat = HeartbeatLoopAsync(
            connection,
            welcome.HeartbeatIntervalSeconds,
            connectionLifetime.Token);
        var commandTasks = new HashSet<Task>();
        try
        {
            SafeLog.Info("AGENT_CONNECTED");
            while (!connectionLifetime.IsCancellationRequested)
            {
                WebSocketInboundMessage inbound = await connection.ReadAsync(connectionLifetime.Token)
                    .ConfigureAwait(false);
                if (inbound.IsClose)
                {
                    if (inbound.CloseStatus == WebSocketCloseStatus.PolicyViolation)
                    {
                        throw new ProtocolException(
                            "SERVER_POLICY_REJECTION",
                            "The central service rejected this Agent connection by policy.");
                    }
                    throw new IOException("Agent WebSocket closed.");
                }

                ServerMessage message = ProtocolCodec.ParseServerMessage(inbound.Payload);
                if (message is not CommandMessage command)
                {
                    throw new ProtocolException(
                        "UNEXPECTED_MESSAGE",
                        "Only COMMAND messages are valid after WELCOME.");
                }

                Task task = dispatcher.DispatchAsync(
                    command,
                    connection.SendTextAsync,
                    connectionLifetime.Token);
                commandTasks.Add(task);
                _ = task.ContinueWith(
                    completed =>
                    {
                        lock (commandTasks)
                        {
                            commandTasks.Remove(completed);
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        finally
        {
            connectionLifetime.Cancel();
            try
            {
                await heartbeat.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            Task[] pending;
            lock (commandTasks)
            {
                pending = commandTasks.ToArray();
            }
            if (pending.Length > 0)
            {
                await Task.WhenAll(pending.Select(IgnoreCancellationAsync)).ConfigureAwait(false);
            }

            try
            {
                await connection.CloseOutputAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "agent_stopping",
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or WebSocketException or ObjectDisposedException)
            {
            }
        }
    }

    private async Task HeartbeatLoopAsync(
        AgentWebSocketConnection connection,
        int intervalSeconds,
        CancellationToken cancellationToken)
    {
        TimeSpan interval = TimeSpan.FromSeconds(intervalSeconds);
        while (true)
        {
            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            MacroSnapshot snapshot = await _bridge.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
            await connection.SendTextAsync(
                ProtocolCodec.EncodeHeartbeat(snapshot),
                cancellationToken).ConfigureAwait(false);
        }
    }

    internal static void ThrowForCloseBeforeWelcome(WebSocketInboundMessage message)
    {
        if (!message.IsClose)
        {
            throw new ArgumentException("Message is not a WebSocket close frame.", nameof(message));
        }
        if (message.CloseStatus == WebSocketCloseStatus.PolicyViolation)
        {
            throw new ProtocolException(
                "SERVER_POLICY_REJECTION",
                "The central service rejected this Agent connection by policy.");
        }
        throw new IOException("Connection closed before WELCOME.");
    }

    private static bool IsTerminalProtocolFailure(ProtocolException exception) =>
        exception.Code is
            "SERVER_POLICY_REJECTION" or
            "UNSUPPORTED_PROTOCOL" or
            "INVALID_CAPABILITIES" or
            "WELCOME_REQUIRED";

    private static bool IsTransient(Exception exception) => exception is
        IOException or
        WebSocketException or
        TimeoutException or
        LocalStatusException or
        StrategyCatalogException or
        ProtocolException;

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
