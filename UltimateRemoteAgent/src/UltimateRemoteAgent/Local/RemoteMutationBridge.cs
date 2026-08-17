using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using UltimateRemoteAgent.Protocol;

namespace UltimateRemoteAgent.Local;

internal sealed record LocalActionOutcome(ActionResult ActionResult, MacroSnapshot Snapshot);

internal sealed record PreparedMutation(
    CommandMessage Command,
    CommandJournalEntry Journal,
    string? CanonicalStrategyPath,
    LocalActionOutcome? ImmediateOutcome);

internal interface IRemoteLocalBridge : IReadOnlyLocalBridge
{
    Task<PreparedMutation> PrepareMutationAsync(
        CommandMessage command,
        CancellationToken cancellationToken);

    Task<LocalActionOutcome> ExecuteMutationAsync(
        PreparedMutation prepared,
        CancellationToken cancellationToken);

    Task<LocalActionOutcome> ReconcileAsync(
        ReconciliationCommand reconciliation,
        CancellationToken cancellationToken);
}

internal sealed class RemoteLocalBridge : IRemoteLocalBridge, IDisposable
{
    private static readonly TimeSpan ActivePollInterval = TimeSpan.FromSeconds(1);

    private readonly string _macroRoot;
    private readonly string _macroExecutable;
    private readonly string _macroScript;
    private readonly ReadOnlyLocalBridge _readOnly;
    private readonly RemoteMailbox _mailbox;
    private readonly BridgeStateReader _bridgeState;
    private readonly RemoteCommandJournal _journal;
    private bool _disposed;

    internal RemoteLocalBridge(
        string macroRoot,
        string? mailboxPath = null,
        string? statePath = null,
        string? journalDirectory = null)
    {
        _macroRoot = Enrollment.EnrollmentValidator.ValidateMacroRoot(macroRoot, requireFiles: true);
        _ = new WmiMacroProcessCensus(_macroRoot);
        _macroExecutable = Path.Combine(_macroRoot, "submacros", "AutoHotkey64.exe");
        _macroScript = Path.Combine(_macroRoot, "Main_Remote.ahk");
        _readOnly = new ReadOnlyLocalBridge(_macroRoot);
        _mailbox = new RemoteMailbox(mailboxPath);
        _bridgeState = new BridgeStateReader(statePath);
        _journal = new RemoteCommandJournal(journalDirectory);
    }

    public Task<MacroSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) =>
        _readOnly.GetSnapshotAsync(cancellationToken);

    public Task<IReadOnlyList<StrategySummary>> ListStrategiesAsync(CancellationToken cancellationToken) =>
        _readOnly.ListStrategiesAsync(cancellationToken);

    public async Task<PreparedMutation> PrepareMutationAsync(
        CommandMessage command,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (command.Operation is not (
            RemoteOperation.StartStrategy or RemoteOperation.StopSafe or RemoteOperation.SwitchStrategy))
        {
            throw new LocalMutationException("OPERATION_UNSUPPORTED");
        }

        string? canonicalStrategyPath = null;
        if (command.Operation is RemoteOperation.StartStrategy or RemoteOperation.SwitchStrategy)
        {
            string strategyId = command.Arguments.StrategyId
                ?? throw new LocalMutationException("STRATEGY_NOT_FOUND");
            StrategyCatalog catalog = StrategyCatalog.Load(_macroRoot);
            if (!catalog.TryResolveCanonicalPath(strategyId, out canonicalStrategyPath))
            {
                throw new LocalMutationException("STRATEGY_NOT_FOUND");
            }
            if (canonicalStrategyPath.IndexOfAny(['\r', '\n']) >= 0)
            {
                throw new LocalMutationException("STRATEGY_PATH_UNSAFE");
            }
        }

        MacroSnapshot snapshot = await GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (snapshot.MacroState is MacroState.Unknown)
        {
            throw new LocalMutationException("MACRO_STATE_UNKNOWN");
        }

        LocalActionOutcome? immediate = null;
        switch (command.Operation)
        {
            case RemoteOperation.StartStrategy:
                if (snapshot.MacroState is MacroState.Running)
                {
                    throw new LocalMutationException("MACRO_ALREADY_RUNNING");
                }
                break;

            case RemoteOperation.StopSafe:
                if (snapshot.MacroState is MacroState.NotRunning or MacroState.Idle)
                {
                    immediate = new LocalActionOutcome(ActionResult.StoppedSafe, snapshot);
                }
                break;

            case RemoteOperation.SwitchStrategy:
                if (snapshot.MacroState is not MacroState.Running)
                {
                    throw new LocalMutationException("MACRO_NOT_RUNNING");
                }
                if (string.Equals(
                    snapshot.CurrentStrategyId,
                    command.Arguments.StrategyId,
                    StringComparison.Ordinal))
                {
                    immediate = new LocalActionOutcome(ActionResult.SwitchedSafe, snapshot);
                }
                break;
        }

        BridgeStateData baseline = _bridgeState.ReadOrDefault();
        CommandJournalEntry journal = _journal.CreateAccepted(
            command,
            baseline.TimeWhenStartedPlaying,
            baseline.CurrentRunCount);
        return new PreparedMutation(command, journal, canonicalStrategyPath, immediate);
    }

    public async Task<LocalActionOutcome> ExecuteMutationAsync(
        PreparedMutation prepared,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CommandJournalEntry journal = _journal.MarkExecuting(prepared.Journal);
        try
        {
            if (prepared.ImmediateOutcome is not null)
            {
                _journal.MarkCompleted(
                    journal,
                    prepared.ImmediateOutcome.ActionResult,
                    prepared.ImmediateOutcome.Snapshot);
                return prepared.ImmediateOutcome;
            }

            LocalActionOutcome outcome = prepared.Command.Operation switch
            {
                RemoteOperation.StartStrategy =>
                    await ExecuteStartAsync(prepared, journal, cancellationToken).ConfigureAwait(false),
                RemoteOperation.StopSafe =>
                    await ExecuteStopAsync(prepared, cancellationToken).ConfigureAwait(false),
                RemoteOperation.SwitchStrategy =>
                    await ExecuteSwitchAsync(prepared, cancellationToken).ConfigureAwait(false),
                _ => throw new LocalMutationException("OPERATION_UNSUPPORTED"),
            };

            _journal.MarkCompleted(journal, outcome.ActionResult, outcome.Snapshot);
            return outcome;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (LocalMutationException exception)
        {
            TryMarkFailed(journal, exception.Code);
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            const string code = "LOCAL_MUTATION_FAILED";
            TryMarkFailed(journal, code);
            throw new LocalMutationException(code, exception);
        }
    }

    public async Task<LocalActionOutcome> ReconcileAsync(
        ReconciliationCommand reconciliation,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CommandJournalEntry? entry = _journal.TryLoad(reconciliation.CommandId);
        if (entry is null)
        {
            throw new LocalMutationException("RECONCILIATION_JOURNAL_MISSING");
        }
        if (entry.Operation != reconciliation.Operation)
        {
            throw new LocalMutationException("RECONCILIATION_JOURNAL_MISMATCH");
        }

        if (entry.Stage is JournalStage.Completed)
        {
            if (entry.ActionResult is null || entry.Snapshot is null)
            {
                throw new LocalMutationException("JOURNAL_INVALID");
            }
            return new LocalActionOutcome(entry.ActionResult.Value, entry.Snapshot);
        }
        if (entry.Stage is JournalStage.Failed)
        {
            throw new LocalMutationException(entry.ErrorCode ?? "RECONCILIATION_PREVIOUSLY_FAILED");
        }
        if (entry.Stage is JournalStage.Accepted)
        {
            TryMarkFailed(entry, "RECONCILIATION_NOT_EXECUTED");
            throw new LocalMutationException("RECONCILIATION_NOT_EXECUTED");
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BridgeStateData? bridge = TryReadBridgeState();
            if (bridge is not null && bridge.LastCommandId == reconciliation.CommandId)
            {
                if (string.Equals(bridge.LastResult, "error", StringComparison.OrdinalIgnoreCase))
                {
                    TryMarkFailed(entry, "MACRO_BRIDGE_REJECTED");
                    throw new LocalMutationException("MACRO_BRIDGE_REJECTED");
                }

                MacroSnapshot? snapshot = await TryGetSnapshotAsync(cancellationToken).ConfigureAwait(false);
                if (snapshot is not null)
                {
                    LocalActionOutcome? outcome = reconciliation.Operation switch
                    {
                        RemoteOperation.StartStrategy when
                            string.Equals(bridge.LastResult, "start_accepted", StringComparison.OrdinalIgnoreCase) &&
                            HasNewStartEvidence(entry, bridge) &&
                            IsRunningTarget(snapshot, entry.StrategyId, requireRoblox: true) =>
                                new LocalActionOutcome(ActionResult.StrategyStarted, snapshot),

                        RemoteOperation.StopSafe when
                            string.Equals(bridge.LastResult, "stopped_safe", StringComparison.OrdinalIgnoreCase) &&
                            snapshot.MacroState is MacroState.NotRunning or MacroState.Idle =>
                                new LocalActionOutcome(ActionResult.StoppedSafe, snapshot),

                        RemoteOperation.SwitchStrategy when
                            string.Equals(bridge.LastResult, "switched_safe", StringComparison.OrdinalIgnoreCase) &&
                            IsRunningTarget(snapshot, entry.StrategyId, requireRoblox: false) =>
                                new LocalActionOutcome(ActionResult.SwitchedSafe, snapshot),

                        _ => null,
                    };

                    if (outcome is not null)
                    {
                        _journal.MarkCompleted(entry, outcome.ActionResult, outcome.Snapshot);
                        return outcome;
                    }
                }
            }

            await Task.Delay(ActivePollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _readOnly.Dispose();
    }

    private async Task<LocalActionOutcome> ExecuteStartAsync(
        PreparedMutation prepared,
        CommandJournalEntry journal,
        CancellationToken cancellationToken)
    {
        string targetId = prepared.Command.Arguments.StrategyId
            ?? throw new LocalMutationException("STRATEGY_NOT_FOUND");
        string targetPath = prepared.CanonicalStrategyPath
            ?? throw new LocalMutationException("STRATEGY_NOT_FOUND");

        MacroSnapshot immediatelyBefore = await GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (immediatelyBefore.MacroState is MacroState.Running or MacroState.Unknown)
        {
            throw new LocalMutationException("MACRO_STATE_CHANGED");
        }

        _mailbox.Enqueue(prepared.Command.CommandId, "start", targetPath);
        try
        {
            LaunchFixedRemoteMacro();
        }
        catch
        {
            if (_mailbox.TryRemoveIfOwned(prepared.Command.CommandId))
            {
                throw;
            }

            return await WaitForStartEvidenceAsync(
                prepared.Command.CommandId,
                targetId,
                journal,
                cancellationToken).ConfigureAwait(false);
        }

        return await WaitForStartEvidenceAsync(
            prepared.Command.CommandId,
            targetId,
            journal,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<LocalActionOutcome> WaitForStartEvidenceAsync(
        Guid commandId,
        string targetId,
        CommandJournalEntry journal,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BridgeStateData? bridge = TryReadBridgeState();
            if (bridge is not null)
            {
                if (bridge.LastCommandId == commandId &&
                    string.Equals(bridge.LastResult, "error", StringComparison.OrdinalIgnoreCase))
                {
                    throw new LocalMutationException("MACRO_BRIDGE_REJECTED");
                }

                if (bridge.LastCommandId == commandId &&
                    string.Equals(bridge.LastResult, "start_accepted", StringComparison.OrdinalIgnoreCase) &&
                    HasNewStartEvidence(journal, bridge))
                {
                    MacroSnapshot? snapshot = await TryGetSnapshotAsync(cancellationToken).ConfigureAwait(false);
                    if (snapshot is not null && IsRunningTarget(snapshot, targetId, requireRoblox: true))
                    {
                        return new LocalActionOutcome(ActionResult.StrategyStarted, snapshot);
                    }
                }
            }

            await Task.Delay(ActivePollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<LocalActionOutcome> ExecuteStopAsync(
        PreparedMutation prepared,
        CancellationToken cancellationToken)
    {
        _mailbox.Enqueue(prepared.Command.CommandId, "stop", null);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BridgeStateData? bridge = TryReadBridgeState();
            if (bridge is not null && bridge.LastCommandId == prepared.Command.CommandId)
            {
                if (string.Equals(bridge.LastResult, "error", StringComparison.OrdinalIgnoreCase))
                {
                    throw new LocalMutationException("MACRO_BRIDGE_REJECTED");
                }
                if (string.Equals(bridge.LastResult, "stopped_safe", StringComparison.OrdinalIgnoreCase))
                {
                    MacroSnapshot? snapshot = await TryGetSnapshotAsync(cancellationToken).ConfigureAwait(false);
                    if (snapshot is not null &&
                        snapshot.MacroState is MacroState.NotRunning or MacroState.Idle)
                    {
                        return new LocalActionOutcome(ActionResult.StoppedSafe, snapshot);
                    }
                }
            }

            await Task.Delay(ActivePollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<LocalActionOutcome> ExecuteSwitchAsync(
        PreparedMutation prepared,
        CancellationToken cancellationToken)
    {
        string targetId = prepared.Command.Arguments.StrategyId
            ?? throw new LocalMutationException("STRATEGY_NOT_FOUND");
        string targetPath = prepared.CanonicalStrategyPath
            ?? throw new LocalMutationException("STRATEGY_NOT_FOUND");
        _mailbox.Enqueue(prepared.Command.CommandId, "switch", targetPath);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BridgeStateData? bridge = TryReadBridgeState();
            if (bridge is not null && bridge.LastCommandId == prepared.Command.CommandId)
            {
                if (string.Equals(bridge.LastResult, "error", StringComparison.OrdinalIgnoreCase))
                {
                    throw new LocalMutationException("MACRO_BRIDGE_REJECTED");
                }
                if (string.Equals(bridge.LastResult, "switched_safe", StringComparison.OrdinalIgnoreCase))
                {
                    MacroSnapshot? snapshot = await TryGetSnapshotAsync(cancellationToken).ConfigureAwait(false);
                    if (snapshot is not null && IsRunningTarget(snapshot, targetId, requireRoblox: false))
                    {
                        return new LocalActionOutcome(ActionResult.SwitchedSafe, snapshot);
                    }
                }
            }

            await Task.Delay(ActivePollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private BridgeStateData? TryReadBridgeState()
    {
        try
        {
            return _bridgeState.ReadOrDefault();
        }
        catch (LocalMutationException)
        {
            return null;
        }
    }

    private async Task<MacroSnapshot?> TryGetSnapshotAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is LocalStatusException or StrategyCatalogException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void LaunchFixedRemoteMacro()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _macroExecutable,
            WorkingDirectory = _macroRoot,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(_macroScript);
        Process? process = Process.Start(startInfo);
        if (process is null)
        {
            throw new LocalMutationException("MACRO_LAUNCH_FAILED");
        }
        process.Dispose();
    }

    private static bool HasNewStartEvidence(CommandJournalEntry entry, BridgeStateData bridge) =>
        bridge.TimeWhenStartedPlaying != 0 &&
        (bridge.TimeWhenStartedPlaying != entry.BaselineTimeWhenStartedPlaying ||
         bridge.CurrentRunCount > entry.BaselineRunCount);

    private static bool IsRunningTarget(
        MacroSnapshot snapshot,
        string? strategyId,
        bool requireRoblox) =>
        !string.IsNullOrEmpty(strategyId) &&
        snapshot.MacroState is MacroState.Running &&
        string.Equals(snapshot.CurrentStrategyId, strategyId, StringComparison.Ordinal) &&
        (!requireRoblox || snapshot.RobloxRunning);

    private void TryMarkFailed(CommandJournalEntry entry, string code)
    {
        try
        {
            _journal.MarkFailed(entry, code);
        }
        catch (LocalMutationException)
        {
        }
    }
}

internal sealed class RemoteMailbox
{
    private const int MaximumMailboxBytes = 16 * 1024;
    private readonly string _path;

    internal RemoteMailbox(string? path = null) =>
        _path = Path.GetFullPath(path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Ultimate_Macro",
            "remote_command.ini"));

    internal void Enqueue(Guid commandId, string action, string? canonicalStrategyPath)
    {
        if (action is not ("start" or "stop" or "switch"))
        {
            throw new LocalMutationException("MAILBOX_ACTION_INVALID");
        }
        if (canonicalStrategyPath is not null && canonicalStrategyPath.IndexOfAny(['\r', '\n']) >= 0)
        {
            throw new LocalMutationException("STRATEGY_PATH_UNSAFE");
        }

        EnsureParentSafe();
        if (File.Exists(_path))
        {
            MailboxCommand existing = ReadExisting();
            if (existing.CommandId == commandId &&
                string.Equals(existing.Action, action, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.StrategyPath, canonicalStrategyPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            throw new LocalMutationException("MAILBOX_BUSY");
        }

        var builder = new StringBuilder();
        builder.AppendLine("[Command]");
        builder.Append("Id=").AppendLine(commandId.ToString("D"));
        builder.Append("Action=").AppendLine(action);
        if (canonicalStrategyPath is not null)
        {
            builder.Append("Strategy=").AppendLine(canonicalStrategyPath);
        }

        var encoding = new UnicodeEncoding(false, true, true);
        byte[] body = encoding.GetBytes(builder.ToString());
        byte[] preamble = encoding.GetPreamble();
        byte[] payload = new byte[preamble.Length + body.Length];
        Buffer.BlockCopy(preamble, 0, payload, 0, preamble.Length);
        Buffer.BlockCopy(body, 0, payload, preamble.Length, body.Length);
        if (payload.Length > MaximumMailboxBytes)
        {
            throw new LocalMutationException("MAILBOX_INVALID");
        }

        string directory = Path.GetDirectoryName(_path)!;
        string temporary = Path.Combine(directory, $".remote.{commandId:D}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            {
                stream.Write(payload);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, _path, overwrite: false);
        }
        catch (IOException exception)
        {
            throw new LocalMutationException("MAILBOX_BUSY", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new LocalMutationException("MAILBOX_WRITE_FAILED", exception);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
            catch (IOException)
            {
            }
        }
    }

    internal bool TryRemoveIfOwned(Guid commandId)
    {
        try
        {
            if (!File.Exists(_path))
            {
                return true;
            }
            MailboxCommand existing = ReadExisting();
            if (existing.CommandId != commandId)
            {
                return false;
            }
            File.Delete(_path);
            return !File.Exists(_path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or LocalMutationException)
        {
            return false;
        }
    }

    private void EnsureParentSafe()
    {
        string? directory = Path.GetDirectoryName(_path);
        if (string.IsNullOrEmpty(directory))
        {
            throw new LocalMutationException("MAILBOX_PATH_UNSAFE");
        }
        try
        {
            Directory.CreateDirectory(directory);
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 ||
                (File.Exists(_path) && (File.GetAttributes(_path) & FileAttributes.ReparsePoint) != 0))
            {
                throw new LocalMutationException("MAILBOX_PATH_UNSAFE");
            }
        }
        catch (LocalMutationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new LocalMutationException("MAILBOX_PATH_UNSAFE", exception);
        }
    }

    private MailboxCommand ReadExisting()
    {
        try
        {
            var info = new FileInfo(_path);
            if (!info.Exists || info.Length is <= 0 or > MaximumMailboxBytes)
            {
                throw new LocalMutationException("MAILBOX_INVALID");
            }
            string text = BoundedIni.Decode(File.ReadAllBytes(_path), MaximumMailboxBytes);
            Dictionary<string, string> command = BoundedIni.ReadSection(text, "Command");
            if (!command.TryGetValue("Id", out string? idText) ||
                !Guid.TryParseExact(idText, "D", out Guid id) ||
                !command.TryGetValue("Action", out string? action))
            {
                throw new LocalMutationException("MAILBOX_INVALID");
            }
            command.TryGetValue("Strategy", out string? strategy);
            return new MailboxCommand(id, action, strategy);
        }
        catch (LocalMutationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new LocalMutationException("MAILBOX_READ_FAILED", exception);
        }
    }

    private sealed record MailboxCommand(Guid CommandId, string Action, string? StrategyPath);
}

internal sealed record BridgeStateData(
    uint TimeWhenStartedPlaying,
    long CurrentRunCount,
    Guid? LastCommandId,
    string? LastResult);

internal sealed class BridgeStateReader
{
    private const int MaximumStateBytes = 128 * 1024;
    private readonly string _path;

    internal BridgeStateReader(string? path = null) =>
        _path = Path.GetFullPath(path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Ultimate_Macro",
            "state.ini"));

    internal BridgeStateData ReadOrDefault()
    {
        if (!File.Exists(_path))
        {
            return new BridgeStateData(0, 0, null, null);
        }

        try
        {
            byte[] bytes = File.ReadAllBytes(_path);
            string text = BoundedIni.Decode(bytes, MaximumStateBytes);
            Dictionary<string, string> state = BoundedIni.ReadSection(text, "State");
            Dictionary<string, string> remote = BoundedIni.ReadSection(text, "Remote");

            uint timeStarted = ParseUInt32(state.GetValueOrDefault("TimeWhenStartedPlaying"));
            long runCount = ParseNonNegativeInt64(state.GetValueOrDefault("CurrentRunCount"));
            Guid? commandId = null;
            if (remote.TryGetValue("LastCommandId", out string? idText) &&
                Guid.TryParseExact(idText, "D", out Guid parsed))
            {
                commandId = parsed;
            }
            string? result = remote.GetValueOrDefault("LastResult");
            if (result is not null && result.Length > 64)
            {
                result = null;
            }
            return new BridgeStateData(timeStarted, runCount, commandId, result);
        }
        catch (LocalMutationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new LocalMutationException("STATE_READ_FAILED", exception);
        }
    }

    private static uint ParseUInt32(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }
        if (!uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out uint value))
        {
            throw new LocalMutationException("STATE_FORMAT_INVALID");
        }
        return value;
    }

    private static long ParseNonNegativeInt64(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }
        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long value) || value < 0)
        {
            throw new LocalMutationException("STATE_FORMAT_INVALID");
        }
        return value;
    }
}

internal static class BoundedIni
{
    internal static string Decode(byte[] payload, int maximumBytes)
    {
        if (payload.Length is <= 0 || payload.Length > maximumBytes)
        {
            throw new LocalMutationException("INI_FILE_INVALID");
        }
        try
        {
            if (payload.Length >= 2 && payload[0] == 0xff && payload[1] == 0xfe)
            {
                return new UnicodeEncoding(false, true, true).GetString(payload, 2, payload.Length - 2);
            }
            if (payload.Length >= 3 && payload[0] == 0xef && payload[1] == 0xbb && payload[2] == 0xbf)
            {
                return new UTF8Encoding(false, true).GetString(payload, 3, payload.Length - 3);
            }

            bool looksUtf16 = payload.Take(Math.Min(payload.Length, 128))
                .Where((_, index) => index % 2 == 1)
                .Count(value => value == 0) > 8;
            return looksUtf16
                ? new UnicodeEncoding(false, true, true).GetString(payload)
                : new UTF8Encoding(false, true).GetString(payload);
        }
        catch (DecoderFallbackException exception)
        {
            throw new LocalMutationException("INI_FILE_INVALID", exception);
        }
    }

    internal static Dictionary<string, string> ReadSection(string text, string sectionName)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool inSection = false;
        foreach (string raw in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] is ';' or '#')
            {
                continue;
            }
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                inSection = string.Equals(line[1..^1].Trim(), sectionName, StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inSection)
            {
                continue;
            }
            int separator = line.IndexOf('=');
            if (separator <= 0)
            {
                throw new LocalMutationException("INI_FILE_INVALID");
            }
            string key = line[..separator].Trim();
            string value = line[(separator + 1)..].Trim();
            if (!values.TryAdd(key, value))
            {
                throw new LocalMutationException("INI_FILE_INVALID");
            }
        }
        return values;
    }
}
