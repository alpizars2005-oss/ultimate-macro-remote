using UltimateRemoteAgent.Protocol;

namespace UltimateRemoteAgent.Local;

internal interface IReadOnlyLocalBridge
{
    Task<MacroSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<StrategySummary>> ListStrategiesAsync(CancellationToken cancellationToken);
}

internal sealed class ReadOnlyLocalBridge : IReadOnlyLocalBridge, IDisposable
{
    private static readonly TimeSpan CatalogCacheLifetime = TimeSpan.FromSeconds(5);
    private readonly string _macroRoot;
    private readonly IMacroProcessCensus _processCensus;
    private readonly IMacroStateReader _stateReader;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _sampleGate = new(1, 1);
    private StrategyCatalog? _catalog;
    private DateTimeOffset _catalogLoadedAt;

    internal ReadOnlyLocalBridge(string macroRoot, TimeProvider? timeProvider = null)
        : this(
            macroRoot,
            new WmiMacroProcessCensus(macroRoot),
            new IniMacroStateReader(),
            timeProvider ?? TimeProvider.System)
    {
    }

    internal ReadOnlyLocalBridge(
        string macroRoot,
        IMacroProcessCensus processCensus,
        IMacroStateReader stateReader,
        TimeProvider timeProvider)
    {
        _macroRoot = Enrollment.EnrollmentValidator.ValidateMacroRoot(macroRoot, requireFiles: true);
        _processCensus = processCensus;
        _stateReader = stateReader;
        _timeProvider = timeProvider;
    }

    public async Task<MacroSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        await _sampleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(GetSnapshot, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sampleGate.Release();
        }
    }

    public async Task<IReadOnlyList<StrategySummary>> ListStrategiesAsync(
        CancellationToken cancellationToken)
    {
        await _sampleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(
                () => LoadCatalog(forceRefresh: true).Strategies,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sampleGate.Release();
        }
    }

    public void Dispose() => _sampleGate.Dispose();

    private MacroSnapshot GetSnapshot()
    {
        IStrategyPathLookup lookup;
        try
        {
            lookup = LoadCatalog(forceRefresh: false);
        }
        catch (StrategyCatalogException)
        {
            lookup = EmptyStrategyPathLookup.Instance;
        }

        var inspector = new MacroStatusInspector(_processCensus, _stateReader, lookup);
        LocalMacroSnapshot snapshot = inspector.GetSnapshot();
        return new MacroSnapshot(
            snapshot.MacroState switch
            {
                LocalMacroState.NotRunning => MacroState.NotRunning,
                LocalMacroState.Idle => MacroState.Idle,
                LocalMacroState.Running => MacroState.Running,
                _ => MacroState.Unknown,
            },
            snapshot.RobloxRunning,
            snapshot.CurrentStrategyId);
    }

    private StrategyCatalog LoadCatalog(bool forceRefresh)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        if (!forceRefresh && _catalog is not null && now - _catalogLoadedAt <= CatalogCacheLifetime)
        {
            return _catalog;
        }

        StrategyCatalog catalog = StrategyCatalog.Load(_macroRoot);
        _catalog = catalog;
        _catalogLoadedAt = now;
        return catalog;
    }

    private sealed class EmptyStrategyPathLookup : IStrategyPathLookup
    {
        internal static EmptyStrategyPathLookup Instance { get; } = new();

        public bool TryGetStrategyIdForCanonicalPath(string path, out string? strategyId)
        {
            strategyId = null;
            return false;
        }
    }
}
