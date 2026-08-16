using System.Globalization;
using System.Text.Json;
using UltimateRemoteAgent.Commands;
using UltimateRemoteAgent.Local;
using UltimateRemoteAgent.Protocol;

namespace UltimateRemoteAgent.Tests;

[TestClass]
public sealed class RemoteCommandDispatcherTests
{
    private static readonly Guid CommandId =
        Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly DateTimeOffset ServerNow =
        DateTimeOffset.Parse(
            "2026-08-15T18:00:10.000+00:00",
            CultureInfo.InvariantCulture);

    [TestMethod]
    public async Task StartSendsAcceptedExecutingAndTypedCompletion()
    {
        var bridge = new FakeBridge
        {
            Outcome = new LocalActionOutcome(
                ActionResult.StrategyStarted,
                new MacroSnapshot(MacroState.Running, true, "strategy_alpha_01")),
        };
        using var dispatcher = new RemoteCommandDispatcher(bridge, () => ServerNow);
        var messages = new List<byte[]>();

        await dispatcher.DispatchAsync(
            Command(RemoteOperation.StartStrategy, "strategy_alpha_01"),
            (payload, _) =>
            {
                messages.Add(payload.ToArray());
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        Assert.AreEqual(1, bridge.PrepareCalls);
        Assert.AreEqual(1, bridge.ExecuteCalls);
        Assert.AreEqual(3, messages.Count);
        AssertStatus(messages[0], "accepted");
        AssertStatus(messages[1], "executing");
        using JsonDocument completed = JsonDocument.Parse(messages[2]);
        Assert.AreEqual("completed", completed.RootElement.GetProperty("status").GetString());
        Assert.AreEqual(
            "strategy_started",
            completed.RootElement.GetProperty("action_result").GetString());
        Assert.AreEqual(
            "strategy_alpha_01",
            completed.RootElement.GetProperty("snapshot")
                .GetProperty("current_strategy_id").GetString());
    }

    [TestMethod]
    public async Task ExpiredMutationNeverTouchesLocalBridge()
    {
        var bridge = new FakeBridge();
        using var dispatcher = new RemoteCommandDispatcher(
            bridge,
            () => ServerNow.AddMinutes(5));
        var messages = new List<byte[]>();

        await dispatcher.DispatchAsync(
            Command(RemoteOperation.StopSafe),
            (payload, _) =>
            {
                messages.Add(payload.ToArray());
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        Assert.AreEqual(0, bridge.PrepareCalls);
        Assert.AreEqual(0, bridge.ExecuteCalls);
        Assert.AreEqual(1, messages.Count);
        using JsonDocument failed = JsonDocument.Parse(messages[0]);
        Assert.AreEqual("failed", failed.RootElement.GetProperty("status").GetString());
        Assert.AreEqual(
            "COMMAND_EXPIRED",
            failed.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [TestMethod]
    public async Task ReconciliationUsesEvidenceOnlyBridgeAndDoesNotPrepareOrExecute()
    {
        var bridge = new FakeBridge
        {
            Outcome = new LocalActionOutcome(
                ActionResult.StoppedSafe,
                new MacroSnapshot(MacroState.Idle, false, null)),
        };
        using var dispatcher = new RemoteCommandDispatcher(bridge, () => ServerNow);
        var messages = new List<byte[]>();

        await dispatcher.ReconcileAsync(
            new ReconciliationCommand(CommandId, RemoteOperation.StopSafe),
            (payload, _) =>
            {
                messages.Add(payload.ToArray());
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        Assert.AreEqual(0, bridge.PrepareCalls);
        Assert.AreEqual(0, bridge.ExecuteCalls);
        Assert.AreEqual(1, bridge.ReconcileCalls);
        Assert.AreEqual(1, messages.Count);
        using JsonDocument completed = JsonDocument.Parse(messages[0]);
        Assert.AreEqual("completed", completed.RootElement.GetProperty("status").GetString());
        Assert.AreEqual("stopped_safe", completed.RootElement.GetProperty("action_result").GetString());
    }

    private static CommandMessage Command(RemoteOperation operation, string? strategyId = null) =>
        new(
            CommandId,
            operation,
            ServerNow.AddSeconds(-1),
            ServerNow.AddSeconds(30),
            strategyId is null ? CommandArguments.Empty : new CommandArguments(strategyId));

    private static void AssertStatus(byte[] payload, string expected)
    {
        using JsonDocument document = JsonDocument.Parse(payload);
        Assert.AreEqual(expected, document.RootElement.GetProperty("status").GetString());
    }

    private sealed class FakeBridge : IRemoteLocalBridge
    {
        internal int PrepareCalls { get; private set; }
        internal int ExecuteCalls { get; private set; }
        internal int ReconcileCalls { get; private set; }
        internal LocalActionOutcome Outcome { get; set; } =
            new(ActionResult.StoppedSafe, new MacroSnapshot(MacroState.Idle, false, null));

        public Task<MacroSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Outcome.Snapshot);

        public Task<IReadOnlyList<StrategySummary>> ListStrategiesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<StrategySummary>>([]);

        public Task<PreparedMutation> PrepareMutationAsync(
            CommandMessage command,
            CancellationToken cancellationToken)
        {
            PrepareCalls++;
            var journal = new CommandJournalEntry(
                CommandJournalEntry.CurrentVersion,
                command.CommandId,
                command.Operation,
                command.Arguments.StrategyId,
                JournalStage.Accepted,
                0,
                0,
                null,
                null,
                null,
                DateTimeOffset.UtcNow);
            return Task.FromResult(new PreparedMutation(command, journal, null, null));
        }

        public Task<LocalActionOutcome> ExecuteMutationAsync(
            PreparedMutation prepared,
            CancellationToken cancellationToken)
        {
            ExecuteCalls++;
            return Task.FromResult(Outcome);
        }

        public Task<LocalActionOutcome> ReconcileAsync(
            ReconciliationCommand reconciliation,
            CancellationToken cancellationToken)
        {
            ReconcileCalls++;
            return Task.FromResult(Outcome);
        }
    }
}
