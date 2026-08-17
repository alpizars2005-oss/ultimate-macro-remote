namespace UltimateRemoteAgent.Local;

internal enum LocalMacroState
{
    NotRunning,
    Idle,
    Running,
    Unknown,
}

internal sealed record LocalMacroSnapshot(
    LocalMacroState MacroState,
    bool RobloxRunning,
    string? CurrentStrategyId);

internal interface IStrategyPathLookup
{
    bool TryGetStrategyIdForCanonicalPath(string path, out string? strategyId);
}

internal interface IMacroSnapshotProvider
{
    LocalMacroSnapshot GetSnapshot();
}

internal sealed class MacroStatusInspector : IMacroSnapshotProvider
{
    private readonly IMacroProcessCensus _processCensus;
    private readonly IMacroStateReader _stateReader;
    private readonly IStrategyPathLookup _strategyLookup;

    internal MacroStatusInspector(
        IMacroProcessCensus processCensus,
        IMacroStateReader stateReader,
        IStrategyPathLookup strategyLookup)
    {
        _processCensus = processCensus;
        _stateReader = stateReader;
        _strategyLookup = strategyLookup;
    }

    public LocalMacroSnapshot GetSnapshot()
    {
        ProcessCensus census = _processCensus.Sample();
        if (census.HasIndeterminateMacroCandidate)
        {
            return new LocalMacroSnapshot(LocalMacroState.Unknown, census.RobloxRunning, null);
        }

        if (census.ExactMacroProcesses.Count == 0)
        {
            return new LocalMacroSnapshot(LocalMacroState.NotRunning, census.RobloxRunning, null);
        }

        if (census.ExactMacroProcesses.Count != 1)
        {
            return new LocalMacroSnapshot(LocalMacroState.Unknown, census.RobloxRunning, null);
        }

        MacroProcessIdentity identity = census.ExactMacroProcesses[0];
        MacroStateData state;
        try
        {
            state = _stateReader.Read();
        }
        catch (LocalStatusException)
        {
            return new LocalMacroSnapshot(LocalMacroState.Unknown, census.RobloxRunning, null);
        }

        if (!_processCensus.IsStillSame(identity))
        {
            return new LocalMacroSnapshot(LocalMacroState.Unknown, census.RobloxRunning, null);
        }

        if (!state.Running)
        {
            return new LocalMacroSnapshot(LocalMacroState.Idle, census.RobloxRunning, null);
        }

        if (!HasFreshStateEvidence(identity, state) ||
            !HasLifecycleEvidence(state) ||
            !IsCoherentStrategyPath(state.StrategyPath))
        {
            return new LocalMacroSnapshot(LocalMacroState.Unknown, census.RobloxRunning, null);
        }

        string? strategyId = null;
        _ = _strategyLookup.TryGetStrategyIdForCanonicalPath(state.StrategyPath!, out strategyId);
        return new LocalMacroSnapshot(LocalMacroState.Running, census.RobloxRunning, strategyId);
    }

    private static bool HasLifecycleEvidence(MacroStateData state) =>
        state.CurrentRunCount > 0 || state.StartTime > 0 || state.TimeWhenStartedPlaying > 0;

    private static bool HasFreshStateEvidence(
        MacroProcessIdentity process,
        MacroStateData state) =>
        state.LastWriteTimeUtc != default &&
        state.LastWriteTimeUtc >= process.CreationTimeUtc.AddSeconds(-2);

    private static bool IsCoherentStrategyPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            !string.Equals(Path.GetExtension(path), ".strat", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(@"\\", StringComparison.Ordinal) ||
            path.StartsWith("//", StringComparison.Ordinal) ||
            path.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            path.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            string root = Path.GetPathRoot(path)!;
            return new DriveInfo(root).DriveType != DriveType.Network && File.Exists(path);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

internal sealed class LocalStatusException : Exception
{
    internal LocalStatusException(string code, Exception? innerException = null)
        : base(code, innerException) => Code = code;

    internal string Code { get; }
}
