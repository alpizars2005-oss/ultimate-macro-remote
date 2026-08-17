using System.Net.WebSockets;
using UltimateRemoteAgent.Protocol;
using UltimateRemoteAgent.Runtime;
using UltimateRemoteAgent.Transport;

namespace UltimateRemoteAgent.Tests;

[TestClass]
public sealed class AgentHostTests
{
    [TestMethod]
    public void NormalCloseBeforeWelcomeIsTransientButPolicyCloseIsTerminal()
    {
        var normal = new WebSocketInboundMessage(
            WebSocketMessageType.Close,
            ReadOnlyMemory<byte>.Empty,
            WebSocketCloseStatus.EndpointUnavailable,
            "untrusted server text must not be logged");
        Assert.ThrowsExactly<IOException>(() => AgentHost.ThrowForCloseBeforeWelcome(normal));

        var policy = new WebSocketInboundMessage(
            WebSocketMessageType.Close,
            ReadOnlyMemory<byte>.Empty,
            WebSocketCloseStatus.PolicyViolation,
            "untrusted server text must not be logged");
        ProtocolException exception = Assert.ThrowsExactly<ProtocolException>(
            () => AgentHost.ThrowForCloseBeforeWelcome(policy));
        Assert.AreEqual("SERVER_POLICY_REJECTION", exception.Code);
        Assert.IsFalse(exception.Message.Contains("untrusted", StringComparison.Ordinal));
    }
}
