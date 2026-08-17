using UltimateRemoteAgent.Local;
using UltimateRemoteAgent.Protocol;

namespace UltimateRemoteAgent.Commands;

internal sealed class ReadOnlyCommandDispatcher
{
    private readonly IReadOnlyLocalBridge _localBridge;
    private readonly Func<DateTimeOffset> _serverNow;

    internal ReadOnlyCommandDispatcher(
        IReadOnlyLocalBridge localBridge,
        Func<DateTimeOffset>? serverNow = null)
    {
        _localBridge = localBridge;
        _serverNow = serverNow ?? (() => TimeProvider.System.GetUtcNow());
    }

    internal async Task DispatchAsync(
        CommandMessage command,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> send,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(send);
        if (_serverNow() >= command.ExpiresAt)
        {
            await SendFailureAsync(
                send,
                command.CommandId,
                "COMMAND_EXPIRED",
                "The read request expired before it could be accepted.",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (command.Operation is not (RemoteOperation.GetStatus or RemoteOperation.ListStrategies))
        {
            await SendFailureAsync(
                send,
                command.CommandId,
                "OPERATION_UNSUPPORTED",
                "This Agent version supports read-only operations only.",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        await send(ProtocolCodec.EncodeAccepted(command.CommandId), cancellationToken).ConfigureAwait(false);
        await send(ProtocolCodec.EncodeExecuting(command.CommandId), cancellationToken).ConfigureAwait(false);

        if (command.Operation is RemoteOperation.GetStatus)
        {
            try
            {
                MacroSnapshot snapshot = await _localBridge.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
                await send(
                    ProtocolCodec.EncodeCompletedStatus(command.CommandId, snapshot),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is LocalStatusException or StrategyCatalogException or IOException or UnauthorizedAccessException)
            {
                await SendFailureAsync(
                    send,
                    command.CommandId,
                    "STATUS_READ_FAILED",
                    "Local macro status could not be determined safely.",
                    cancellationToken).ConfigureAwait(false);
            }
            return;
        }

        try
        {
            IReadOnlyList<StrategySummary> strategies =
                await _localBridge.ListStrategiesAsync(cancellationToken).ConfigureAwait(false);
            await send(
                ProtocolCodec.EncodeCompletedStrategies(command.CommandId, strategies),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is StrategyCatalogException or IOException or UnauthorizedAccessException ||
            exception is ProtocolException { Code: "MESSAGE_TOO_LARGE" })
        {
            await SendFailureAsync(
                send,
                command.CommandId,
                exception is ProtocolException
                    ? "STRATEGY_LIST_TOO_LARGE"
                    : "STRATEGY_LIST_READ_FAILED",
                exception is ProtocolException
                    ? "The approved local strategy catalog is too large to return safely."
                    : "The approved local strategy catalog is unavailable.",
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static ValueTask SendFailureAsync(
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> send,
        Guid commandId,
        string code,
        string message,
        CancellationToken cancellationToken) => send(
            ProtocolCodec.EncodeFailed(commandId, new CommandError(code, message)),
            cancellationToken);
}
