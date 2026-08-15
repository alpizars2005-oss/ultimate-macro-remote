namespace UltimateRemoteAgent.Transport;

internal sealed class ServerSynchronizedClock
{
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();
    private DateTimeOffset _serverTime;
    private long _localTimestamp;
    private bool _synchronized;

    internal ServerSynchronizedClock(TimeProvider? timeProvider = null) =>
        _timeProvider = timeProvider ?? TimeProvider.System;

    internal void Synchronize(DateTimeOffset serverTime)
    {
        if (serverTime.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Server time must be UTC.", nameof(serverTime));
        }

        lock (_gate)
        {
            _serverTime = serverTime;
            _localTimestamp = _timeProvider.GetTimestamp();
            _synchronized = true;
        }
    }

    internal DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            if (!_synchronized)
            {
                throw new InvalidOperationException("Server clock is not synchronized.");
            }

            return _serverTime + _timeProvider.GetElapsedTime(_localTimestamp);
        }
    }
}
