using System.Text;
using System.Text.Json;
using UltimateRemoteAgent.Commands;
using UltimateRemoteAgent.Local;
using UltimateRemoteAgent.Protocol;
using UltimateRemoteAgent.Transport;

namespace UltimateRemoteAgent.Tests;

[TestClass]
public sealed class ReadOnlyCommandDispatcherTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 15, 18, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task GetStatusEmitsAcceptedExecutingCompletedInOrder()
    {
        var bridge = new FakeBridge
        {
            Snapshot = new MacroSnapshot(MacroState.Idle, false, null),
        };
        var sent = new List<byte[]>();
        var dispatcher = new ReadOnlyCommandDispatcher(bridge, () => Now);

        await dispatcher.DispatchAsync(
            Command(RemoteOperation.GetStatus),
            Capture(sent),
            CancellationToken.None);

        CollectionAssert.AreEqual(
            new[] { "accepted", "executing", "completed" },
            sent.Select(Status).ToArray());
        Assert.AreEqual(1, bridge.StatusCalls);
        Assert.AreEqual(0, bridge.ListCalls);
    }

    [TestMethod]
    public async Task ListStrategiesReturnsPathFreeOpaqueSummaries()
    {
        var bridge = new FakeBridge
        {
            Strategies = [new StrategySummary("strategy_alpha_01", "@everyone **Alpha**")],
        };
        var sent = new List<byte[]>();
        var dispatcher = new ReadOnlyCommandDispatcher(bridge, () => Now);

        await dispatcher.DispatchAsync(
            Command(RemoteOperation.ListStrategies),
            Capture(sent),
            CancellationToken.None);

        string completed = Encoding.UTF8.GetString(sent[^1]);
        Assert.AreEqual("completed", Status(sent[^1]));
        Assert.IsTrue(completed.Contains("strategy_alpha_01", StringComparison.Ordinal));
        Assert.IsFalse(completed.Contains(@"C:\", StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(1, bridge.ListCalls);
    }

    [TestMethod]
    [DataRow(RemoteOperation.StartStrategy)]
    [DataRow(RemoteOperation.StopSafe)]
    [DataRow(RemoteOperation.SwitchStrategy)]
    public async Task MutatingOperationsAreRejectedWithoutTouchingLocalBridge(RemoteOperation operation)
    {
        var bridge = new FakeBridge();
        var sent = new List<byte[]>();
        var dispatcher = new ReadOnlyCommandDispatcher(bridge, () => Now);

        await dispatcher.DispatchAsync(Command(operation), Capture(sent), CancellationToken.None);

        Assert.AreEqual(1, sent.Count);
        Assert.AreEqual("failed", Status(sent[0]));
        Assert.AreEqual("OPERATION_UNSUPPORTED", ErrorCode(sent[0]));
        Assert.AreEqual(0, bridge.StatusCalls);
        Assert.AreEqual(0, bridge.ListCalls);
    }

    [TestMethod]
    public async Task ExpiredReadIsRejectedBeforeLocalAccess()
    {
        var bridge = new FakeBridge();
        var sent = new List<byte[]>();
        var dispatcher = new ReadOnlyCommandDispatcher(bridge, () => Now);
        CommandMessage command = Command(RemoteOperation.GetStatus) with { ExpiresAt = Now };

        await dispatcher.DispatchAsync(command, Capture(sent), CancellationToken.None);

        Assert.AreEqual("COMMAND_EXPIRED", ErrorCode(sent.Single()));
        Assert.AreEqual(0, bridge.StatusCalls);
    }

    [TestMethod]
    public async Task LocalFailureUsesFixedMessageAndNeverLeaksExceptionText()
    {
        const string sentinel = @"C:\private\secret-user-path.strat";
        var bridge = new FakeBridge { StatusException = new IOException(sentinel) };
        var sent = new List<byte[]>();
        var dispatcher = new ReadOnlyCommandDispatcher(bridge, () => Now);

        await dispatcher.DispatchAsync(
            Command(RemoteOperation.GetStatus),
            Capture(sent),
            CancellationToken.None);

        string failed = Encoding.UTF8.GetString(sent[^1]);
        Assert.AreEqual("STATUS_READ_FAILED", ErrorCode(sent[^1]));
        Assert.IsFalse(failed.Contains(sentinel, StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task OversizedValidCatalogBecomesSanitizedFailureWithoutTerminatingSession()
    {
        var bridge = new FakeBridge
        {
            Strategies = Enumerable.Range(0, 500)
                .Select(index => new StrategySummary(
                    $"strategy_{index:D3}",
                    new string((char)('A' + (index % 26)), 200)))
                .ToArray(),
        };
        var sent = new List<byte[]>();
        var dispatcher = new ReadOnlyCommandDispatcher(bridge, () => Now);

        await dispatcher.DispatchAsync(
            Command(RemoteOperation.ListStrategies),
            Capture(sent),
            CancellationToken.None);

        CollectionAssert.AreEqual(
            new[] { "accepted", "executing", "failed" },
            sent.Select(Status).ToArray());
        Assert.AreEqual("STRATEGY_LIST_TOO_LARGE", ErrorCode(sent[^1]));
        Assert.IsTrue(sent[^1].Length <= ProtocolConstants.MaximumMessageBytes);
    }

    [TestMethod]
    public async Task ExpiryUsesWelcomeServerTimeInsteadOfSkewedWindowsWallClock()
    {
        var time = new ManualTimeProvider(Now.AddDays(2));
        var serverClock = new ServerSynchronizedClock(time);
        serverClock.Synchronize(Now);
        var bridge = new FakeBridge();
        var dispatcher = new ReadOnlyCommandDispatcher(bridge, serverClock.GetUtcNow);
        var first = new List<byte[]>();

        await dispatcher.DispatchAsync(
            Command(RemoteOperation.GetStatus),
            Capture(first),
            CancellationToken.None);

        Assert.AreEqual("completed", Status(first[^1]));
        time.Advance(TimeSpan.FromSeconds(31));
        var expired = new List<byte[]>();
        await dispatcher.DispatchAsync(
            Command(RemoteOperation.GetStatus),
            Capture(expired),
            CancellationToken.None);
        Assert.AreEqual("COMMAND_EXPIRED", ErrorCode(expired.Single()));
    }

    private static CommandMessage Command(RemoteOperation operation) => new(
        Guid.Parse("11111111-1111-4111-8111-111111111111"),
        operation,
        Now,
        Now.AddSeconds(30),
        operation is RemoteOperation.StartStrategy or RemoteOperation.SwitchStrategy
            ? new CommandArguments("strategy_alpha_01")
            : CommandArguments.Empty);

    private static Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> Capture(
        List<byte[]> destination) => (payload, _) =>
    {
        destination.Add(payload.ToArray());
        return ValueTask.CompletedTask;
    };

    private static string Status(byte[] payload)
    {
        using JsonDocument document = JsonDocument.Parse(payload);
        return document.RootElement.GetProperty("status").GetString()!;
    }

    private static string ErrorCode(byte[] payload)
    {
        using JsonDocument document = JsonDocument.Parse(payload);
        return document.RootElement.GetProperty("error").GetProperty("code").GetString()!;
    }

    private sealed class FakeBridge : IReadOnlyLocalBridge
    {
        internal MacroSnapshot Snapshot { get; init; } =
            new(MacroState.NotRunning, false, null);

        internal IReadOnlyList<StrategySummary> Strategies { get; init; } = [];

        internal Exception? StatusException { get; init; }

        internal int StatusCalls { get; private set; }

        internal int ListCalls { get; private set; }

        public Task<MacroSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
        {
            StatusCalls++;
            return StatusException is null
                ? Task.FromResult(Snapshot)
                : Task.FromException<MacroSnapshot>(StatusException);
        }

        public Task<IReadOnlyList<StrategySummary>> ListStrategiesAsync(
            CancellationToken cancellationToken)
        {
            ListCalls++;
            return Task.FromResult(Strategies);
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset localNow) : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow() => localNow;

        public override long GetTimestamp() => _timestamp;

        internal void Advance(TimeSpan value) => _timestamp += value.Ticks;
    }

}
