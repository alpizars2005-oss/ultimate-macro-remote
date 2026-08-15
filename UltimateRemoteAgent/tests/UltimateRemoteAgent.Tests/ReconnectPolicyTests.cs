using UltimateRemoteAgent.Transport;

namespace UltimateRemoteAgent.Tests;

[TestClass]
public sealed class ReconnectPolicyTests
{
    [TestMethod]
    public void FullJitterUsesExponentialCeilingAndCapsIt()
    {
        var random = new MaximumRandom();
        var policy = new FullJitterReconnectPolicy(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(60),
            random);

        Assert.AreEqual(TimeSpan.FromSeconds(1), policy.GetDelay(0));
        Assert.AreEqual(TimeSpan.FromSeconds(2), policy.GetDelay(1));
        Assert.AreEqual(TimeSpan.FromSeconds(32), policy.GetDelay(5));
        Assert.AreEqual(TimeSpan.FromSeconds(60), policy.GetDelay(6));
        Assert.AreEqual(TimeSpan.FromSeconds(60), policy.GetDelay(200));
    }

    [TestMethod]
    public void FullJitterAllowsZeroAndRejectsNegativeAttempts()
    {
        var policy = new FullJitterReconnectPolicy(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(60),
            new ZeroRandom());

        Assert.AreEqual(TimeSpan.Zero, policy.GetDelay(0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => policy.GetDelay(-1));
    }

    private sealed class MaximumRandom : IJitterRandom
    {
        public long NextInt64(long exclusiveUpperBound) => exclusiveUpperBound - 1;
    }

    private sealed class ZeroRandom : IJitterRandom
    {
        public long NextInt64(long exclusiveUpperBound) => 0;
    }
}
