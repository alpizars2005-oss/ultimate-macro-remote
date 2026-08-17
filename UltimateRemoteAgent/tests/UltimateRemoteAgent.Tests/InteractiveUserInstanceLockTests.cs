using UltimateRemoteAgent.Runtime;

namespace UltimateRemoteAgent.Tests;

[TestClass]
public sealed class InteractiveUserInstanceLockTests
{
    [TestMethod]
    public void LockAllowsOnlyOneAgentForTheInteractiveUser()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"UltimateRemoteAgent.InstanceTests.{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "instance.lock");
        try
        {
            using (InteractiveUserInstanceLock first = InteractiveUserInstanceLock.Acquire(path))
            {
                AgentRuntimeException exception = Assert.ThrowsExactly<AgentRuntimeException>(
                    () => InteractiveUserInstanceLock.Acquire(path));
                Assert.AreEqual("AGENT_ALREADY_RUNNING", exception.Code);
            }

            using InteractiveUserInstanceLock second = InteractiveUserInstanceLock.Acquire(path);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
