using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UltimateRemoteAgent.Protocol;

public static partial class ProtocolCodec
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static ServerMessage ParseServerMessage(ReadOnlyMemory<byte> utf8Json)
    {
        if (utf8Json.Length > ProtocolConstants.MaximumMessageBytes)
        {
            throw Error("MESSAGE_TOO_LARGE", "Message exceeds the protocol size limit.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(
                utf8Json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 32,
                });
        }
        catch (JsonException exception)
        {
            throw Error("INVALID_JSON", "Message must be valid UTF-8 JSON.", exception);
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            RequireObject(root, "INVALID_JSON", "Message root must be an object.");
            RejectDuplicateProperties(root);

            RequireProtocol(root);
            string type = RequireString(root, "type", "MISSING_FIELD");
            return type switch
            {
                "WELCOME" => ParseWelcome(root),
                "COMMAND" => ParseCommand(root),
                _ => throw Error("UNKNOWN_MESSAGE_TYPE", "Unknown server message type."),
            };
        }
    }

    public static byte[] EncodeHello(string agentVersion, MacroSnapshot snapshot)
    {
        ValidateCleanText(agentVersion, "INVALID_AGENT_VERSION", 1, 64);
        ValidateSnapshot(snapshot);

        return Encode(writer =>
        {
            writer.WriteStartObject();
            WriteEnvelope(writer, "HELLO");
            writer.WriteString("agent_version", agentVersion);
            writer.WritePropertyName("supported_operations");
            writer.WriteStartArray();
            foreach (RemoteOperation operation in ProtocolConstants.R3Capabilities)
            {
                writer.WriteStringValue(OperationToWire(operation));
            }

            writer.WriteEndArray();
            writer.WritePropertyName("snapshot");
            WriteSnapshot(writer, snapshot);
            writer.WriteEndObject();
        });
    }

    public static byte[] EncodeHeartbeat(MacroSnapshot snapshot)
    {
        ValidateSnapshot(snapshot);
        return Encode(writer =>
        {
            writer.WriteStartObject();
            WriteEnvelope(writer, "HEARTBEAT");
            writer.WritePropertyName("snapshot");
            WriteSnapshot(writer, snapshot);
            writer.WriteEndObject();
        });
    }

    public static byte[] EncodeAccepted(Guid commandId) =>
        EncodeStatusOnly(commandId, CommandUpdateStatus.Accepted);

    public static byte[] EncodeExecuting(Guid commandId) =>
        EncodeStatusOnly(commandId, CommandUpdateStatus.Executing);

    public static byte[] EncodeCompletedStatus(Guid commandId, MacroSnapshot snapshot)
    {
        ValidateSnapshot(snapshot);
        return Encode(writer =>
        {
            WriteUpdateStart(writer, commandId, CommandUpdateStatus.Completed);
            writer.WritePropertyName("snapshot");
            WriteSnapshot(writer, snapshot);
            writer.WriteEndObject();
        });
    }

    public static byte[] EncodeCompletedStrategies(
        Guid commandId,
        IReadOnlyCollection<StrategySummary> strategies)
    {
        ArgumentNullException.ThrowIfNull(strategies);
        ValidateStrategies(strategies);
        return Encode(writer =>
        {
            WriteUpdateStart(writer, commandId, CommandUpdateStatus.Completed);
            writer.WritePropertyName("strategies");
            writer.WriteStartArray();
            foreach (StrategySummary strategy in strategies)
            {
                writer.WriteStartObject();
                writer.WriteString("strategy_id", strategy.StrategyId);
                writer.WriteString("name", strategy.Name);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }

    public static byte[] EncodeCompletedAction(
        Guid commandId,
        MacroSnapshot snapshot,
        ActionResult actionResult)
    {
        ValidateSnapshot(snapshot);
        return Encode(writer =>
        {
            WriteUpdateStart(writer, commandId, CommandUpdateStatus.Completed);
            writer.WritePropertyName("snapshot");
            WriteSnapshot(writer, snapshot);
            writer.WriteString("action_result", ActionResultToWire(actionResult));
            writer.WriteEndObject();
        });
    }

    public static byte[] EncodeFailed(Guid commandId, CommandError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (!ErrorCodeRegex().IsMatch(error.Code))
        {
            throw Error("INVALID_COMMAND_ERROR", "Invalid command error code.");
        }

        ValidateCleanText(error.Message, "INVALID_COMMAND_ERROR", 1, 500);
        return Encode(writer =>
        {
            WriteUpdateStart(writer, commandId, CommandUpdateStatus.Failed);
            writer.WritePropertyName("error");
            writer.WriteStartObject();
            writer.WriteString("code", error.Code);
            writer.WriteString("message", error.Message);
            writer.WriteEndObject();
            writer.WriteEndObject();
        });
    }

    public static string OperationToWire(RemoteOperation operation) => operation switch
    {
        RemoteOperation.GetStatus => "GET_STATUS",
        RemoteOperation.ListStrategies => "LIST_STRATEGIES",
        RemoteOperation.StartStrategy => "START_STRATEGY",
        RemoteOperation.StopSafe => "STOP_SAFE",
        RemoteOperation.SwitchStrategy => "SWITCH_STRATEGY",
        _ => throw Error("UNSUPPORTED_OPERATION", "Unsupported operation."),
    };

    private static WelcomeMessage ParseWelcome(JsonElement root)
    {
        RequireExactProperties(
            root,
            "protocol",
            "type",
            "heartbeat_interval_seconds",
            "server_time",
            "reconcile_commands");

        int heartbeatInterval = RequireInt32(root, "heartbeat_interval_seconds");
        if (heartbeatInterval <= 0)
        {
            throw Error("INVALID_HEARTBEAT_INTERVAL", "Heartbeat interval must be positive.");
        }

        DateTimeOffset serverTime = ParseCanonicalTimestamp(
            RequireString(root, "server_time", "MISSING_FIELD"));
        JsonElement reconciliations = RequireProperty(root, "reconcile_commands");
        if (reconciliations.ValueKind != JsonValueKind.Array)
        {
            throw Error("INVALID_RECONCILIATION", "Reconciliation metadata must be an array.");
        }

        var entries = new List<ReconciliationCommand>(reconciliations.GetArrayLength());
        var identifiers = new HashSet<Guid>();
        foreach (JsonElement entry in reconciliations.EnumerateArray())
        {
            RequireObject(entry, "INVALID_RECONCILIATION", "Reconciliation entry must be an object.");
            RequireExactProperties(entry, "command_id", "operation");
            Guid commandId = ParseCanonicalGuid(
                RequireString(entry, "command_id", "MISSING_FIELD"));
            if (!identifiers.Add(commandId))
            {
                throw Error("INVALID_RECONCILIATION", "Duplicate reconciliation command ID.");
            }

            entries.Add(
                new ReconciliationCommand(
                    commandId,
                    ParseOperation(RequireString(entry, "operation", "MISSING_FIELD"))));
        }

        return new WelcomeMessage(heartbeatInterval, serverTime, entries.AsReadOnly());
    }

    private static CommandMessage ParseCommand(JsonElement root)
    {
        RequireExactProperties(
            root,
            "protocol",
            "type",
            "command_id",
            "operation",
            "issued_at",
            "expires_at",
            "arguments");

        Guid commandId = ParseCanonicalGuid(
            RequireString(root, "command_id", "MISSING_FIELD"));
        RemoteOperation operation = ParseOperation(
            RequireString(root, "operation", "MISSING_FIELD"));
        DateTimeOffset issuedAt = ParseCanonicalTimestamp(
            RequireString(root, "issued_at", "MISSING_FIELD"));
        DateTimeOffset expiresAt = ParseCanonicalTimestamp(
            RequireString(root, "expires_at", "MISSING_FIELD"));
        if (expiresAt <= issuedAt)
        {
            throw Error("INVALID_TIMESTAMP", "Command expiry must follow issue time.");
        }

        JsonElement arguments = RequireProperty(root, "arguments");
        RequireObject(arguments, "INVALID_ARGUMENTS", "Command arguments must be an object.");

        CommandArguments parsedArguments;
        if (operation is RemoteOperation.StartStrategy or RemoteOperation.SwitchStrategy)
        {
            RequireExactProperties(arguments, "strategy_id");
            string strategyId = RequireString(arguments, "strategy_id", "INVALID_ARGUMENTS");
            ValidateStrategyId(strategyId);
            parsedArguments = new CommandArguments(strategyId);
        }
        else
        {
            RequireExactProperties(arguments);
            parsedArguments = CommandArguments.Empty;
        }

        return new CommandMessage(
            commandId,
            operation,
            issuedAt,
            expiresAt,
            parsedArguments);
    }

    private static void RequireProtocol(JsonElement root)
    {
        JsonElement protocol = RequireProperty(root, "protocol");
        if (protocol.ValueKind != JsonValueKind.Number
            || !string.Equals(protocol.GetRawText(), "1", StringComparison.Ordinal))
        {
            throw Error("UNSUPPORTED_PROTOCOL", "Only protocol 1 is supported.");
        }
    }

    private static RemoteOperation ParseOperation(string value) => value switch
    {
        "GET_STATUS" => RemoteOperation.GetStatus,
        "LIST_STRATEGIES" => RemoteOperation.ListStrategies,
        "START_STRATEGY" => RemoteOperation.StartStrategy,
        "STOP_SAFE" => RemoteOperation.StopSafe,
        "SWITCH_STRATEGY" => RemoteOperation.SwitchStrategy,
        _ => throw Error("UNSUPPORTED_OPERATION", "Unsupported operation."),
    };

    private static DateTimeOffset ParseCanonicalTimestamp(string value)
    {
        if (!CanonicalTimestampRegex().IsMatch(value)
            || !DateTimeOffset.TryParseExact(
                value,
                "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset timestamp)
            || timestamp.Offset != TimeSpan.Zero)
        {
            throw Error("INVALID_TIMESTAMP", "Timestamp must use canonical UTC milliseconds.");
        }

        return timestamp;
    }

    private static Guid ParseCanonicalGuid(string value)
    {
        if (!Guid.TryParseExact(value, "D", out Guid parsed)
            || !string.Equals(parsed.ToString("D"), value, StringComparison.Ordinal))
        {
            throw Error("INVALID_COMMAND_ID", "Command ID must be a canonical lowercase UUID.");
        }

        return parsed;
    }

    private static byte[] Encode(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            write(writer);
        }

        if (buffer.WrittenCount > ProtocolConstants.MaximumMessageBytes)
        {
            throw Error("MESSAGE_TOO_LARGE", "Encoded message exceeds the protocol size limit.");
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static byte[] EncodeStatusOnly(Guid commandId, CommandUpdateStatus status)
    {
        if (status is not (CommandUpdateStatus.Accepted or CommandUpdateStatus.Executing))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        return Encode(writer =>
        {
            WriteUpdateStart(writer, commandId, status);
            writer.WriteEndObject();
        });
    }

    private static void WriteUpdateStart(
        Utf8JsonWriter writer,
        Guid commandId,
        CommandUpdateStatus status)
    {
        writer.WriteStartObject();
        WriteEnvelope(writer, "COMMAND_UPDATE");
        writer.WriteString("command_id", commandId.ToString("D"));
        writer.WriteString("status", StatusToWire(status));
    }

    private static void WriteEnvelope(Utf8JsonWriter writer, string type)
    {
        writer.WriteNumber("protocol", ProtocolConstants.Version);
        writer.WriteString("type", type);
    }

    private static void WriteSnapshot(Utf8JsonWriter writer, MacroSnapshot snapshot)
    {
        writer.WriteStartObject();
        writer.WriteString("macro_state", MacroStateToWire(snapshot.MacroState));
        writer.WriteBoolean("roblox_running", snapshot.RobloxRunning);
        if (snapshot.CurrentStrategyId is null)
        {
            writer.WriteNull("current_strategy_id");
        }
        else
        {
            writer.WriteString("current_strategy_id", snapshot.CurrentStrategyId);
        }

        writer.WriteEndObject();
    }

    private static string MacroStateToWire(MacroState state) => state switch
    {
        MacroState.NotRunning => "not_running",
        MacroState.Idle => "idle",
        MacroState.Running => "running",
        MacroState.Unknown => "unknown",
        _ => throw Error("INVALID_SNAPSHOT", "Invalid macro state."),
    };

    private static string StatusToWire(CommandUpdateStatus status) => status switch
    {
        CommandUpdateStatus.Accepted => "accepted",
        CommandUpdateStatus.Executing => "executing",
        CommandUpdateStatus.Completed => "completed",
        CommandUpdateStatus.Failed => "failed",
        _ => throw Error("INVALID_COMMAND_STATUS", "Invalid command status."),
    };

    private static string ActionResultToWire(ActionResult actionResult) => actionResult switch
    {
        ActionResult.StrategyStarted => "strategy_started",
        ActionResult.StoppedSafe => "stopped_safe",
        ActionResult.SwitchedSafe => "switched_safe",
        _ => throw Error("INVALID_COMMAND_RESULT", "Invalid action result."),
    };

    private static void ValidateSnapshot(MacroSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _ = MacroStateToWire(snapshot.MacroState);
        if (snapshot.CurrentStrategyId is not null)
        {
            ValidateStrategyId(snapshot.CurrentStrategyId);
        }
    }

    private static void ValidateStrategies(IReadOnlyCollection<StrategySummary> strategies)
    {
        if (strategies.Count > ProtocolConstants.MaximumStrategies)
        {
            throw Error("INVALID_STRATEGIES", "Too many strategies.");
        }

        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        foreach (StrategySummary strategy in strategies)
        {
            if (strategy is null)
            {
                throw Error("INVALID_STRATEGIES", "Strategy cannot be null.");
            }

            ValidateStrategyId(strategy.StrategyId);
            ValidateCleanText(strategy.Name, "INVALID_STRATEGIES", 1, 200);
            if (strategy.Name.IndexOfAny(['/', '\\', ':']) >= 0)
            {
                throw Error("INVALID_STRATEGIES", "Strategy names cannot contain path syntax.");
            }

            if (!identifiers.Add(strategy.StrategyId))
            {
                throw Error("INVALID_STRATEGIES", "Duplicate strategy ID.");
            }
        }
    }

    private static void ValidateStrategyId(string value)
    {
        if (value is null || !StrategyIdRegex().IsMatch(value))
        {
            throw Error("INVALID_STRATEGY_ID", "Invalid opaque strategy ID.");
        }
    }

    private static void ValidateCleanText(
        string value,
        string code,
        int minimumScalars,
        int maximumScalars)
    {
        if (value is null || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw Error(code, "Text field has invalid whitespace.");
        }

        int scalarCount = 0;
        ReadOnlySpan<char> remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            OperationStatus status = Rune.DecodeFromUtf16(
                remaining,
                out Rune rune,
                out int consumed);
            if (status != OperationStatus.Done)
            {
                throw Error(code, "Text field contains invalid Unicode.");
            }

            UnicodeCategory category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control
                or UnicodeCategory.Format
                or UnicodeCategory.Surrogate
                or UnicodeCategory.PrivateUse
                or UnicodeCategory.OtherNotAssigned)
            {
                throw Error(code, "Text field contains forbidden characters.");
            }

            scalarCount++;
            remaining = remaining[consumed..];
        }

        if (scalarCount < minimumScalars || scalarCount > maximumScalars)
        {
            throw Error(code, "Text field has invalid length.");
        }

        _ = StrictUtf8.GetByteCount(value);
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw Error("DUPLICATE_FIELD", "Message contains a duplicate JSON field.");
                }

                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                RejectDuplicateProperties(item);
            }
        }
    }

    private static void RequireExactProperties(JsonElement element, params string[] expected)
    {
        var expectedNames = new HashSet<string>(expected, StringComparer.Ordinal);
        var actualNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            actualNames.Add(property.Name);
        }

        if (expectedNames.Except(actualNames).Any())
        {
            throw Error("MISSING_FIELD", "Message is missing a required field.");
        }

        if (actualNames.Except(expectedNames).Any())
        {
            throw Error("UNKNOWN_FIELD", "Message contains an unknown field.");
        }
    }

    private static JsonElement RequireProperty(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value))
        {
            throw Error("MISSING_FIELD", "Message is missing a required field.");
        }

        return value;
    }

    private static string RequireString(JsonElement element, string name, string code)
    {
        JsonElement value = RequireProperty(element, name);
        if (value.ValueKind != JsonValueKind.String)
        {
            throw Error(code, $"{name} must be a string.");
        }

        return value.GetString()!;
    }

    private static int RequireInt32(JsonElement element, string name)
    {
        JsonElement value = RequireProperty(element, name);
        if (value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out int result)
            || value.GetRawText().ContainsAny('.', 'e', 'E'))
        {
            throw Error("INVALID_NUMBER", $"{name} must be an integer.");
        }

        return result;
    }

    private static void RequireObject(JsonElement element, string code, string message)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Error(code, message);
        }
    }

    private static ProtocolException Error(string code, string message) => new(code, message);

    private static ProtocolException Error(string code, string message, Exception innerException) =>
        new(code, message, innerException);

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]{7,63}$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex StrategyIdRegex();

    [GeneratedRegex("^[A-Z][A-Z0-9_]{0,63}$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex ErrorCodeRegex();

    [GeneratedRegex("^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\\.[0-9]{3}Z$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex CanonicalTimestampRegex();
}

internal static class StringSearchExtensions
{
    public static bool ContainsAny(this string value, params char[] characters) =>
        value.IndexOfAny(characters) >= 0;
}
