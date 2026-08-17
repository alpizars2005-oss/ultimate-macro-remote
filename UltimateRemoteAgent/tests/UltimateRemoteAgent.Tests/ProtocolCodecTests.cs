using System.Text;
using System.Text.Json;
using UltimateRemoteAgent.Protocol;

namespace UltimateRemoteAgent.Tests;

[TestClass]
public sealed class ProtocolCodecTests
{
    private static readonly Guid CommandId =
        Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly string[] AgentOperationNames =
        ["GET_STATUS", "LIST_STRATEGIES", "START_STRATEGY", "STOP_SAFE", "SWITCH_STRATEGY"];

    [TestMethod]
    public void ParseWelcomeUsesClosedSchemaAndReturnsReconciliationMetadataOnly()
    {
        const string json = """
            {
              "protocol": 1,
              "type": "WELCOME",
              "heartbeat_interval_seconds": 30,
              "server_time": "2026-08-15T18:00:00.000Z",
              "reconcile_commands": [
                {
                  "command_id": "11111111-1111-4111-8111-111111111111",
                  "operation": "STOP_SAFE"
                }
              ]
            }
            """;

        WelcomeMessage welcome = Assert.IsInstanceOfType<WelcomeMessage>(Parse(json));

        Assert.AreEqual(30, welcome.HeartbeatIntervalSeconds);
        Assert.AreEqual(TimeSpan.Zero, welcome.ServerTime.Offset);
        Assert.AreEqual(1, welcome.ReconcileCommands.Count);
        Assert.AreEqual(CommandId, welcome.ReconcileCommands[0].CommandId);
        Assert.AreEqual(RemoteOperation.StopSafe, welcome.ReconcileCommands[0].Operation);
        Assert.AreEqual(
            2,
            typeof(ReconciliationCommand).GetProperties().Length,
            "WELCOME reconciliation must contain metadata, not executable arguments.");
    }

    [TestMethod]
    public void ParseWelcomeAcceptsAnyPositiveProtocolHeartbeatValue()
    {
        const string json =
            "{\"protocol\":1,\"type\":\"WELCOME\",\"heartbeat_interval_seconds\":1," +
            "\"server_time\":\"2026-08-15T18:00:00.000Z\",\"reconcile_commands\":[]}";

        WelcomeMessage welcome = Assert.IsInstanceOfType<WelcomeMessage>(Parse(json));

        Assert.AreEqual(1, welcome.HeartbeatIntervalSeconds);
    }

    [TestMethod]
    public void ParseCommandAcceptsExactlyTheFiveV1Operations()
    {
        var cases = new (string WireName, RemoteOperation Operation, string Arguments)[]
        {
            ("GET_STATUS", RemoteOperation.GetStatus, "{}"),
            ("LIST_STRATEGIES", RemoteOperation.ListStrategies, "{}"),
            ("START_STRATEGY", RemoteOperation.StartStrategy, "{\"strategy_id\":\"strategy_alpha_01\"}"),
            ("STOP_SAFE", RemoteOperation.StopSafe, "{}"),
            ("SWITCH_STRATEGY", RemoteOperation.SwitchStrategy, "{\"strategy_id\":\"strategy_alpha_01\"}"),
        };

        foreach ((string wireName, RemoteOperation operation, string arguments) in cases)
        {
            string json = CommandJson(wireName, arguments);
            CommandMessage command = Assert.IsInstanceOfType<CommandMessage>(Parse(json));
            Assert.AreEqual(operation, command.Operation);
            Assert.AreEqual(
                operation is RemoteOperation.StartStrategy or RemoteOperation.SwitchStrategy
                    ? "strategy_alpha_01"
                    : null,
                command.Arguments.StrategyId);
        }
    }

    [TestMethod]
    public void ParseRejectsDuplicateUnknownAndMissingFields()
    {
        AssertCode(
            "DUPLICATE_FIELD",
            """{"protocol":1,"protocol":1,"type":"WELCOME","heartbeat_interval_seconds":30,"server_time":"2026-08-15T18:00:00.000Z","reconcile_commands":[]}""");
        AssertCode(
            "DUPLICATE_FIELD",
            """{"protocol":1,"type":"WELCOME","heartbeat_interval_seconds":30,"server_time":"2026-08-15T18:00:00.000Z","reconcile_commands":[{"command_id":"11111111-1111-4111-8111-111111111111","command_id":"11111111-1111-4111-8111-111111111111","operation":"STOP_SAFE"}]}""");
        AssertCode(
            "UNKNOWN_FIELD",
            """{"protocol":1,"type":"WELCOME","heartbeat_interval_seconds":30,"server_time":"2026-08-15T18:00:00.000Z","reconcile_commands":[],"extra":true}""");
        AssertCode(
            "MISSING_FIELD",
            """{"protocol":1,"type":"WELCOME","heartbeat_interval_seconds":30,"server_time":"2026-08-15T18:00:00.000Z"}""");
    }

    [TestMethod]
    public void ParseRejectsNoncanonicalProtocolUuidAndTimestamps()
    {
        AssertCode(
            "UNSUPPORTED_PROTOCOL",
            """{"protocol":1.0,"type":"WELCOME","heartbeat_interval_seconds":30,"server_time":"2026-08-15T18:00:00.000Z","reconcile_commands":[]}""");
        AssertCode(
            "INVALID_COMMAND_ID",
            CommandJson("GET_STATUS", "{}", "AAAAAAAA-AAAA-4AAA-8AAA-AAAAAAAAAAAA"));
        AssertCode(
            "INVALID_TIMESTAMP",
            CommandJson("GET_STATUS", "{}", issuedAt: "2026-08-15T18:00:00Z"));
        AssertCode(
            "INVALID_TIMESTAMP",
            CommandJson("GET_STATUS", "{}", expiresAt: "2026-08-15T17:59:59.999Z"));
    }

    [TestMethod]
    public void ParseRejectsUnknownOperationsAndArgumentShapeChanges()
    {
        AssertCode("UNSUPPORTED_OPERATION", CommandJson("EXEC", "{}"));
        AssertCode(
            "UNKNOWN_FIELD",
            CommandJson("GET_STATUS", "{\"strategy_id\":\"strategy_alpha_01\"}"));
        AssertCode("MISSING_FIELD", CommandJson("START_STRATEGY", "{}"));
        AssertCode(
            "INVALID_STRATEGY_ID",
            CommandJson("START_STRATEGY", "{\"strategy_id\":\"C:\\\\secret.strat\"}"));
    }

    [TestMethod]
    public void ParseRejectsMessagesAboveSixtyFourKiB()
    {
        byte[] oversized = new byte[ProtocolConstants.MaximumMessageBytes + 1];
        ProtocolException exception = Assert.ThrowsExactly<ProtocolException>(
            () => ProtocolCodec.ParseServerMessage(oversized));

        Assert.AreEqual("MESSAGE_TOO_LARGE", exception.Code);
    }

    [TestMethod]
    public void EncodeHelloAdvertisesTheFiveR4Capabilities()
    {
        byte[] encoded = ProtocolCodec.EncodeHello(
            "0.4.0",
            new MacroSnapshot(MacroState.Idle, false, null));
        using JsonDocument document = JsonDocument.Parse(encoded);
        JsonElement root = document.RootElement;
        string[] operations = root.GetProperty("supported_operations")
            .EnumerateArray()
            .Select(item => item.GetString()!)
            .ToArray();

        CollectionAssert.AreEqual(AgentOperationNames, operations);
        Assert.AreEqual(1, root.GetProperty("protocol").GetInt32());
        Assert.AreEqual("HELLO", root.GetProperty("type").GetString());
        Assert.AreEqual(JsonValueKind.Null, root.GetProperty("snapshot").GetProperty("current_strategy_id").ValueKind);
    }

    [TestMethod]
    public void EncodersProduceExactLifecycleResultShapes()
    {
        AssertProperties(ProtocolCodec.EncodeAccepted(CommandId), "protocol", "type", "command_id", "status");
        AssertProperties(ProtocolCodec.EncodeExecuting(CommandId), "protocol", "type", "command_id", "status");
        AssertProperties(
            ProtocolCodec.EncodeCompletedStatus(
                CommandId,
                new MacroSnapshot(MacroState.NotRunning, false, null)),
            "protocol",
            "type",
            "command_id",
            "status",
            "snapshot");
        AssertProperties(
            ProtocolCodec.EncodeCompletedStrategies(
                CommandId,
                new[] { new StrategySummary("strategy_alpha_01", "Alpha") }),
            "protocol",
            "type",
            "command_id",
            "status",
            "strategies");
        AssertProperties(
            ProtocolCodec.EncodeCompletedAction(
                CommandId,
                new MacroSnapshot(MacroState.Running, true, "strategy_alpha_01"),
                ActionResult.StrategyStarted),
            "protocol",
            "type",
            "command_id",
            "status",
            "snapshot",
            "action_result");
        AssertProperties(
            ProtocolCodec.EncodeFailed(
                CommandId,
                new CommandError("READ_FAILED", "Status could not be read.")),
            "protocol",
            "type",
            "command_id",
            "status",
            "error");
    }

    private static ServerMessage Parse(string json) =>
        ProtocolCodec.ParseServerMessage(Encoding.UTF8.GetBytes(json));

    private static void AssertCode(string expectedCode, string json)
    {
        ProtocolException exception = Assert.ThrowsExactly<ProtocolException>(
            () => Parse(json));
        Assert.AreEqual(expectedCode, exception.Code);
    }

    private static void AssertProperties(byte[] json, params string[] names)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        string[] actual = document.RootElement
            .EnumerateObject()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] expected = names.Order(StringComparer.Ordinal).ToArray();
        CollectionAssert.AreEqual(expected, actual);
        Assert.IsTrue(json.Length <= ProtocolConstants.MaximumMessageBytes);
    }

    private static string CommandJson(
        string operation,
        string arguments,
        string commandId = "11111111-1111-4111-8111-111111111111",
        string issuedAt = "2026-08-15T18:00:00.000Z",
        string expiresAt = "2026-08-15T18:00:30.000Z") =>
        $$"""
          {
            "protocol": 1,
            "type": "COMMAND",
            "command_id": "{{commandId}}",
            "operation": "{{operation}}",
            "issued_at": "{{issuedAt}}",
            "expires_at": "{{expiresAt}}",
            "arguments": {{arguments}}
          }
          """;
}
