using UltimateRemoteAgent.Local;

namespace UltimateRemoteAgent.Tests;

[TestClass]
public sealed class MacroStatusInspectorTests
{
    private static readonly MacroProcessIdentity FirstIdentity =
        new(4201, DateTime.UnixEpoch);

    [TestMethod]
    public void GetSnapshotIgnoresStaleRunningStateWhenExactMacroIsAbsent()
    {
        var census = new FakeProcessCensus(
            new ProcessCensus(Array.Empty<MacroProcessIdentity>(), RobloxRunning: true));
        var state = new FakeStateReader(
            new MacroStateData(
                Running: true,
                StrategyPath: @"C:\private\stale.strat",
                CurrentRunCount: 9,
                StartTime: 123,
                TimeWhenStartedPlaying: 456));
        var strategies = new FakeStrategyLookup();

        LocalMacroSnapshot snapshot = CreateInspector(census, state, strategies).GetSnapshot();

        Assert.AreEqual(LocalMacroState.NotRunning, snapshot.MacroState);
        Assert.IsTrue(snapshot.RobloxRunning);
        Assert.IsNull(snapshot.CurrentStrategyId);
        Assert.AreEqual(0, state.ReadCount);
        Assert.AreEqual(0, census.RecheckCount);
        Assert.AreEqual(0, strategies.LookupCount);
    }

    [TestMethod]
    public void GetSnapshotReportsIdleOnlyAfterTheExactProcessSurvivesRecheck()
    {
        var census = ExactProcessCensus(robloxRunning: false);
        var state = new FakeStateReader(
            new MacroStateData(
                Running: false,
                StrategyPath: @"C:\private\stale.strat",
                CurrentRunCount: 10,
                StartTime: 123,
                TimeWhenStartedPlaying: 456));
        var strategies = new FakeStrategyLookup();

        LocalMacroSnapshot snapshot = CreateInspector(census, state, strategies).GetSnapshot();

        Assert.AreEqual(LocalMacroState.Idle, snapshot.MacroState);
        Assert.IsFalse(snapshot.RobloxRunning);
        Assert.IsNull(snapshot.CurrentStrategyId);
        Assert.AreEqual(1, state.ReadCount);
        Assert.AreEqual(1, census.RecheckCount);
        Assert.AreEqual(0, strategies.LookupCount);
    }

    [TestMethod]
    public void GetSnapshotTreatsEarlyRunningFlagWithoutLifecycleMarkerAsUnknown()
    {
        using var strategy = new TemporaryStrategyFile();
        var census = ExactProcessCensus(robloxRunning: true);
        var state = new FakeStateReader(
            RunningState(strategy.Path, currentRunCount: 0, startTime: 0, playingTime: 0));
        var strategies = new FakeStrategyLookup(strategy.Path, "s_approved_strategy_01");

        LocalMacroSnapshot snapshot = CreateInspector(census, state, strategies).GetSnapshot();

        Assert.AreEqual(LocalMacroState.Unknown, snapshot.MacroState);
        Assert.IsTrue(snapshot.RobloxRunning);
        Assert.IsNull(snapshot.CurrentStrategyId);
        Assert.AreEqual(0, strategies.LookupCount);
    }

    [TestMethod]
    public void GetSnapshotRejectsLifecycleMarkersFromBeforeTheExactProcessStarted()
    {
        using var strategy = new TemporaryStrategyFile();
        var census = ExactProcessCensus(robloxRunning: true);
        MacroStateData stale = RunningState(
            strategy.Path,
            currentRunCount: 4,
            startTime: 123,
            playingTime: 456) with
        {
            LastWriteTimeUtc = FirstIdentity.CreationTimeUtc.AddMinutes(-1),
        };

        LocalMacroSnapshot snapshot = CreateInspector(
            census,
            new FakeStateReader(stale),
            new FakeStrategyLookup(strategy.Path, "s_approved_strategy_01")).GetSnapshot();

        Assert.AreEqual(LocalMacroState.Unknown, snapshot.MacroState);
        Assert.IsNull(snapshot.CurrentStrategyId);
    }

    [TestMethod]
    public void GetSnapshotReportsRunningWhenAConservativeLifecycleMarkerExists()
    {
        using var strategy = new TemporaryStrategyFile();
        var census = ExactProcessCensus(robloxRunning: true);
        var state = new FakeStateReader(
            RunningState(strategy.Path, currentRunCount: 1, startTime: 0, playingTime: 0));
        var strategies = new FakeStrategyLookup(strategy.Path, "s_approved_strategy_01");

        LocalMacroSnapshot snapshot = CreateInspector(census, state, strategies).GetSnapshot();

        Assert.AreEqual(LocalMacroState.Running, snapshot.MacroState);
        Assert.IsTrue(snapshot.RobloxRunning);
        Assert.AreEqual("s_approved_strategy_01", snapshot.CurrentStrategyId);
        Assert.AreEqual(1, strategies.LookupCount);
        Assert.AreEqual(strategy.Path, strategies.LastLookupPath);
    }

    [TestMethod]
    public void GetSnapshotTreatsMultipleExactMacroProcessesAsUnknownWithoutReadingState()
    {
        var identities = new[]
        {
            FirstIdentity,
            new MacroProcessIdentity(4202, DateTime.UnixEpoch.AddSeconds(1)),
        };
        var census = new FakeProcessCensus(
            new ProcessCensus(identities, RobloxRunning: false));
        var state = new FakeStateReader(
            new MacroStateData(false, null, 0, 0, 0));
        var strategies = new FakeStrategyLookup();

        LocalMacroSnapshot snapshot = CreateInspector(census, state, strategies).GetSnapshot();

        Assert.AreEqual(LocalMacroState.Unknown, snapshot.MacroState);
        Assert.IsNull(snapshot.CurrentStrategyId);
        Assert.AreEqual(0, state.ReadCount);
        Assert.AreEqual(0, census.RecheckCount);
        Assert.AreEqual(0, strategies.LookupCount);
    }

    [TestMethod]
    public void GetSnapshotTreatsUnreadableTargetProcessAsUnknown()
    {
        var census = new FakeProcessCensus(
            new ProcessCensus(
                Array.Empty<MacroProcessIdentity>(),
                RobloxRunning: false,
                HasIndeterminateMacroCandidate: true));
        var state = new FakeStateReader(new MacroStateData(false, null, 0, 0, 0));

        LocalMacroSnapshot snapshot = CreateInspector(
            census,
            state,
            new FakeStrategyLookup()).GetSnapshot();

        Assert.AreEqual(LocalMacroState.Unknown, snapshot.MacroState);
        Assert.AreEqual(0, state.ReadCount);
    }

    [TestMethod]
    public void GetSnapshotTreatsPidIdentityRecheckFailureAsUnknown()
    {
        using var strategy = new TemporaryStrategyFile();
        var census = ExactProcessCensus(robloxRunning: false);
        census.IsStillSameResult = false;
        var state = new FakeStateReader(
            RunningState(strategy.Path, currentRunCount: 1, startTime: 0, playingTime: 0));
        var strategies = new FakeStrategyLookup(strategy.Path, "s_approved_strategy_01");

        LocalMacroSnapshot snapshot = CreateInspector(census, state, strategies).GetSnapshot();

        Assert.AreEqual(LocalMacroState.Unknown, snapshot.MacroState);
        Assert.IsNull(snapshot.CurrentStrategyId);
        Assert.AreEqual(1, state.ReadCount);
        Assert.AreEqual(1, census.RecheckCount);
        Assert.AreEqual(0, strategies.LookupCount);
    }

    [TestMethod]
    public void GetSnapshotMapsOnlyApprovedStrategyAndNeverReturnsItsLocalPath()
    {
        using var strategy = new TemporaryStrategyFile();
        var state = new FakeStateReader(
            RunningState(strategy.Path, currentRunCount: 0, startTime: 789, playingTime: 0));
        var strategies = new FakeStrategyLookup(strategy.Path, "s_approved_strategy_01");

        LocalMacroSnapshot snapshot = CreateInspector(
            ExactProcessCensus(robloxRunning: false),
            state,
            strategies).GetSnapshot();

        Assert.AreEqual(LocalMacroState.Running, snapshot.MacroState);
        Assert.AreEqual("s_approved_strategy_01", snapshot.CurrentStrategyId);
        Assert.IsFalse(
            snapshot.ToString().Contains(strategy.Path, StringComparison.OrdinalIgnoreCase),
            "The local strategy path must not appear in the status snapshot.");
    }

    [TestMethod]
    public void GetSnapshotRunningOutsideApprovedCatalogUsesNullWithoutPathLeakage()
    {
        using var strategy = new TemporaryStrategyFile();
        var state = new FakeStateReader(
            RunningState(strategy.Path, currentRunCount: 0, startTime: 0, playingTime: 789));
        var strategies = new FakeStrategyLookup();

        LocalMacroSnapshot snapshot = CreateInspector(
            ExactProcessCensus(robloxRunning: true),
            state,
            strategies).GetSnapshot();

        Assert.AreEqual(LocalMacroState.Running, snapshot.MacroState);
        Assert.IsNull(snapshot.CurrentStrategyId);
        Assert.AreEqual(1, strategies.LookupCount);
        Assert.IsFalse(
            snapshot.ToString().Contains(strategy.Path, StringComparison.OrdinalIgnoreCase),
            "An unapproved local strategy path must not appear in the status snapshot.");
    }

    private static MacroStatusInspector CreateInspector(
        IMacroProcessCensus census,
        IMacroStateReader state,
        IStrategyPathLookup strategies) => new(census, state, strategies);

    private static FakeProcessCensus ExactProcessCensus(bool robloxRunning) =>
        new(new ProcessCensus(new[] { FirstIdentity }, robloxRunning));

    private static MacroStateData RunningState(
        string strategyPath,
        long currentRunCount,
        uint startTime,
        uint playingTime) =>
        new(
            Running: true,
            StrategyPath: strategyPath,
            CurrentRunCount: currentRunCount,
            StartTime: startTime,
            TimeWhenStartedPlaying: playingTime,
            LastWriteTimeUtc: FirstIdentity.CreationTimeUtc.AddSeconds(1));

    private sealed class FakeProcessCensus(ProcessCensus sample) : IMacroProcessCensus
    {
        internal bool IsStillSameResult { get; set; } = true;

        internal int RecheckCount { get; private set; }

        public ProcessCensus Sample() => sample;

        public bool IsStillSame(MacroProcessIdentity identity)
        {
            RecheckCount++;
            Assert.AreEqual(FirstIdentity, identity);
            return IsStillSameResult;
        }
    }

    private sealed class FakeStateReader(MacroStateData state) : IMacroStateReader
    {
        internal int ReadCount { get; private set; }

        public MacroStateData Read()
        {
            ReadCount++;
            return state;
        }
    }

    private sealed class FakeStrategyLookup : IStrategyPathLookup
    {
        private readonly string? _approvedPath;
        private readonly string? _strategyId;

        internal FakeStrategyLookup(string? approvedPath = null, string? strategyId = null)
        {
            _approvedPath = approvedPath;
            _strategyId = strategyId;
        }

        internal int LookupCount { get; private set; }

        internal string? LastLookupPath { get; private set; }

        public bool TryGetStrategyIdForCanonicalPath(string path, out string? strategyId)
        {
            LookupCount++;
            LastLookupPath = path;
            if (_approvedPath is not null &&
                string.Equals(path, _approvedPath, StringComparison.OrdinalIgnoreCase))
            {
                strategyId = _strategyId;
                return true;
            }

            strategyId = null;
            return false;
        }
    }

    private sealed class TemporaryStrategyFile : IDisposable
    {
        private readonly string _directory;

        internal TemporaryStrategyFile()
        {
            _directory = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"UltimateRemoteAgent.StatusTests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(_directory);
            Path = System.IO.Path.Combine(_directory, "private-local-name.strat");
            File.WriteAllText(Path, "test");
        }

        internal string Path { get; }

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
