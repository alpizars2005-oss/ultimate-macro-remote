using UltimateRemoteAgent.Local;
using UltimateRemoteAgent.Protocol;

namespace UltimateRemoteAgent.Commands;

internal sealed class RemoteCommandDispatcher : IDisposable
{
    private readonly IRemoteLocalBridge _localBridge;
    private readonly Func<DateTimeOffset> _serverNow;
    private readonly ReadOnlyCommandDispatcher _readOnly;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private bool _disposed;

    internal RemoteCommandDispatcher(
        IRemoteLocalBridge localBridge,
        Func<DateTimeOffset> serverNow)
    {
        _localBridge = localBridge ?? throw new ArgumentNullException(nameof(localBridge));
        _serverNow = serverNow ?? throw new ArgumentNullException(nameof(serverNow));
        _readOnly = new ReadOnlyCommandDispatcher(localBridge, serverNow);
    }

    internal async Task DispatchAsync(
        CommandMessage command,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> send,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(send);

        if (command.Operation is RemoteOperation.GetStatus or RemoteOperation.ListStrategies)
        {
            await _readOnly.DispatchAsync(command, send, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (_serverNow() >= command.ExpiresAt)
        {
            await SendFailureAsync(
                send,
                command.CommandId,
                "COMMAND_EXPIRED",
                "The command expired before it could be accepted.",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        PreparedMutation prepared;
        try
        {
            prepared = await _localBridge.PrepareMutationAsync(command, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (LocalMutationException exception)
        {
            await SendLocalFailureAsync(send, command.CommandId, exception.Code, cancellationToken)
                .ConfigureAwait(false);
            return;
        }
        catch (Exception exception) when (
            exception is StrategyCatalogException or LocalStatusException or IOException or UnauthorizedAccessException)
        {
            await SendFailureAsync(
                send,
                command.CommandId,
                "LOCAL_PRECONDITION_FAILED",
                "Local macro state could not be validated safely.",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        await send(ProtocolCodec.EncodeAccepted(command.CommandId), cancellationToken)
            .ConfigureAwait(false);

        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_serverNow() >= command.ExpiresAt)
            {
                await SendFailureAsync(
                    send,
                    command.CommandId,
                    "COMMAND_EXPIRED",
                    "The command expired before execution began.",
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            await send(ProtocolCodec.EncodeExecuting(command.CommandId), cancellationToken)
                .ConfigureAwait(false);
            LocalActionOutcome outcome = await _localBridge.ExecuteMutationAsync(
                prepared,
                cancellationToken).ConfigureAwait(false);
            await send(
                ProtocolCodec.EncodeCompletedAction(
                    command.CommandId,
                    outcome.Snapshot,
                    outcome.ActionResult),
                cancellationToken).ConfigureAwait(false);
        }
        catch (LocalMutationException exception)
        {
            await SendLocalFailureAsync(send, command.CommandId, exception.Code, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    internal async Task ReconcileAsync(
        ReconciliationCommand reconciliation,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> send,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(send);
        if (reconciliation.Operation is not (
            RemoteOperation.StartStrategy or RemoteOperation.StopSafe or RemoteOperation.SwitchStrategy))
        {
            await SendFailureAsync(
                send,
                reconciliation.CommandId,
                "RECONCILIATION_UNSUPPORTED",
                "Only gameplay-changing commands can require reconciliation.",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LocalActionOutcome outcome = await _localBridge.ReconcileAsync(
                reconciliation,
                cancellationToken).ConfigureAwait(false);
            await send(
                ProtocolCodec.EncodeCompletedAction(
                    reconciliation.CommandId,
                    outcome.Snapshot,
                    outcome.ActionResult),
                cancellationToken).ConfigureAwait(false);
        }
        catch (LocalMutationException exception)
        {
            await SendLocalFailureAsync(
                send,
                reconciliation.CommandId,
                exception.Code,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _mutationGate.Dispose();
    }

    private static ValueTask SendLocalFailureAsync(
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> send,
        Guid commandId,
        string localCode,
        CancellationToken cancellationToken)
    {
        (string code, string message) = localCode switch
        {
            "STRATEGY_NOT_FOUND" =>
                ("STRATEGY_NOT_FOUND", "The requested strategy is not available locally."),
            "MACRO_ALREADY_RUNNING" =>
                ("MACRO_ALREADY_RUNNING", "A strategy is already running; use switch instead."),
            "MACRO_NOT_RUNNING" =>
                ("MACRO_NOT_RUNNING", "No strategy is currently running."),
            "MACRO_STATE_UNKNOWN" or "MACRO_STATE_CHANGED" =>
                ("MACRO_STATE_UNKNOWN", "Local macro state could not be confirmed safely."),
            "MAILBOX_BUSY" =>
                ("COMMAND_IN_PROGRESS", "Another local Remote command is still pending."),
            "START_CONFIRMATION_TIMEOUT" =>
                ("START_CONFIRMATION_TIMEOUT", "The macro did not confirm a new strategy start in time."),
            "SAFE_BOUNDARY_TIMEOUT" =>
                ("SAFE_BOUNDARY_TIMEOUT", "The macro did not reach a safe between-match boundary in time."),
            "MACRO_BRIDGE_REJECTED" =>
                ("MACRO_BRIDGE_REJECTED", "The local macro bridge rejected the command safely."),
            "RECONCILIATION_JOURNAL_MISSING" or
            "RECONCILIATION_JOURNAL_MISMATCH" or
            "RECONCILIATION_NOT_EXECUTED" or
            "RECONCILIATION_INDETERMINATE" =>
                ("RECONCILIATION_INDETERMINATE", "The previous command outcome could not be proven safely."),
            _ =>
                ("LOCAL_MUTATION_FAILED", "The local Remote action could not be completed safely."),
        };
        return SendFailureAsync(send, commandId, code, message, cancellationToken);
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
