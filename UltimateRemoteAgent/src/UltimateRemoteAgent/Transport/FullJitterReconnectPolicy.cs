namespace UltimateRemoteAgent.Transport;

public interface IJitterRandom
{
    long NextInt64(long exclusiveUpperBound);
}

public sealed class SystemJitterRandom : IJitterRandom
{
    public long NextInt64(long exclusiveUpperBound)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(exclusiveUpperBound);

        return Random.Shared.NextInt64(exclusiveUpperBound);
    }
}

public sealed class FullJitterReconnectPolicy
{
    private readonly long _baseTicks;
    private readonly long _maximumTicks;
    private readonly IJitterRandom _random;

    public FullJitterReconnectPolicy(
        TimeSpan baseDelay,
        TimeSpan maximumDelay,
        IJitterRandom? random = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(baseDelay, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumDelay, baseDelay);

        _baseTicks = baseDelay.Ticks;
        _maximumTicks = maximumDelay.Ticks;
        _random = random ?? new SystemJitterRandom();
    }

    public static FullJitterReconnectPolicy CreateDefault() =>
        new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60));

    public TimeSpan GetDelay(int consecutiveFailureCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(consecutiveFailureCount);

        long ceiling = _baseTicks;
        for (int index = 0; index < consecutiveFailureCount && ceiling < _maximumTicks; index++)
        {
            ceiling = ceiling > _maximumTicks / 2
                ? _maximumTicks
                : Math.Min(_maximumTicks, ceiling * 2);
        }

        long exclusiveUpperBound = ceiling == long.MaxValue ? long.MaxValue : ceiling + 1;
        return TimeSpan.FromTicks(_random.NextInt64(exclusiveUpperBound));
    }
}
