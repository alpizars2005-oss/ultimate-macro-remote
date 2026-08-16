using System.Text.Json;
using System.Text.Json.Serialization;
using UltimateRemoteAgent.Protocol;

namespace UltimateRemoteAgent.Local;

internal enum JournalStage
{
    Accepted,
    Executing,
    Completed,
    Failed,
}

internal sealed record CommandJournalEntry(
    int Version,
    Guid CommandId,
    RemoteOperation Operation,
    string? StrategyId,
    JournalStage Stage,
    ActionResult? ActionResult,
    MacroSnapshot? Snapshot,
    string? ErrorCode,
    DateTimeOffset UpdatedAtUtc)
{
    internal const int CurrentVersion = 1;
}

internal sealed class RemoteCommandJournal
{
    private const int MaximumJournalBytes = 32 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly string _directory;

    internal RemoteCommandJournal(string? directory = null)
    {
        _directory = Path.GetFullPath(directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UltimateRemoteAgent",
            "journal",
            "v1"));
    }

    internal CommandJournalEntry CreateAccepted(CommandMessage command)
    {
        var entry = new CommandJournalEntry(
            CommandJournalEntry.CurrentVersion,
            command.CommandId,
            command.Operation,
            command.Arguments.StrategyId,
            JournalStage.Accepted,
            null,
            null,
            null,
            DateTimeOffset.UtcNow);
        Save(entry);
        return entry;
    }

    internal CommandJournalEntry MarkExecuting(CommandJournalEntry entry)
    {
        CommandJournalEntry updated = entry with
        {
            Stage = JournalStage.Executing,
            ActionResult = null,
            Snapshot = null,
            ErrorCode = null,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        Save(updated);
        return updated;
    }

    internal CommandJournalEntry MarkCompleted(
        CommandJournalEntry entry,
        ActionResult actionResult,
        MacroSnapshot snapshot)
    {
        CommandJournalEntry updated = entry with
        {
            Stage = JournalStage.Completed,
            ActionResult = actionResult,
            Snapshot = snapshot,
            ErrorCode = null,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        Save(updated);
        return updated;
    }

    internal CommandJournalEntry MarkFailed(CommandJournalEntry entry, string errorCode)
    {
        CommandJournalEntry updated = entry with
        {
            Stage = JournalStage.Failed,
            ActionResult = null,
            Snapshot = null,
            ErrorCode = errorCode,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        Save(updated);
        return updated;
    }

    internal CommandJournalEntry? TryLoad(Guid commandId)
    {
        string path = GetPath(commandId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is <= 0 or > MaximumJournalBytes)
            {
                throw new LocalMutationException("JOURNAL_INVALID");
            }

            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length > MaximumJournalBytes)
            {
                throw new LocalMutationException("JOURNAL_INVALID");
            }

            CommandJournalEntry? entry = JsonSerializer.Deserialize<CommandJournalEntry>(bytes, JsonOptions);
            if (entry is null ||
                entry.Version != CommandJournalEntry.CurrentVersion ||
                entry.CommandId != commandId ||
                entry.UpdatedAtUtc.Offset != TimeSpan.Zero ||
                !IsCoherent(entry))
            {
                throw new LocalMutationException("JOURNAL_INVALID");
            }

            return entry;
        }
        catch (LocalMutationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            throw new LocalMutationException("JOURNAL_READ_FAILED", exception);
        }
    }

    private void Save(CommandJournalEntry entry)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            if ((File.GetAttributes(_directory) & FileAttributes.ReparsePoint) != 0)
            {
                throw new LocalMutationException("JOURNAL_PATH_UNSAFE");
            }

            string destination = GetPath(entry.CommandId);
            string temporary = Path.Combine(_directory, $".{entry.CommandId:D}.{Guid.NewGuid():N}.tmp");
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(entry, JsonOptions);
            if (payload.Length > MaximumJournalBytes)
            {
                throw new LocalMutationException("JOURNAL_INVALID");
            }

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

                File.Move(temporary, destination, overwrite: true);
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
        catch (LocalMutationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new LocalMutationException("JOURNAL_WRITE_FAILED", exception);
        }
    }

    private string GetPath(Guid commandId) => Path.Combine(_directory, $"{commandId:D}.json");

    private static bool IsCoherent(CommandJournalEntry entry)
    {
        bool needsStrategy = entry.Operation is RemoteOperation.StartStrategy or RemoteOperation.SwitchStrategy;
        if (needsStrategy != !string.IsNullOrEmpty(entry.StrategyId))
        {
            return false;
        }

        return entry.Stage switch
        {
            JournalStage.Accepted or JournalStage.Executing =>
                entry.ActionResult is null && entry.Snapshot is null && entry.ErrorCode is null,
            JournalStage.Completed =>
                entry.ActionResult is not null && entry.Snapshot is not null && entry.ErrorCode is null,
            JournalStage.Failed =>
                entry.ActionResult is null && entry.Snapshot is null && !string.IsNullOrWhiteSpace(entry.ErrorCode),
            _ => false,
        };
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = false,
            AllowTrailingCommas = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 16,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        return options;
    }
}

internal sealed class LocalMutationException : Exception
{
    internal LocalMutationException(string code, Exception? innerException = null)
        : base(code, innerException) => Code = code;

    internal string Code { get; }
}
