using UltimateRemoteAgent.Local;

namespace UltimateRemoteAgent.Tests;

[TestClass]
public sealed class RemoteMailboxTests
{
    [TestMethod]
    public void EnqueueWritesBomAndRefusesToOverwriteAnotherCommand()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"UltimateRemoteAgent.MailboxTests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "remote_command.ini");
        try
        {
            var mailbox = new RemoteMailbox(path);
            Guid first = Guid.Parse("11111111-1111-4111-8111-111111111111");
            mailbox.Enqueue(first, "stop", null);

            byte[] payload = File.ReadAllBytes(path);
            Assert.IsGreaterThanOrEqualTo(payload.Length, 2);
            Assert.AreEqual(0xff, payload[0]);
            Assert.AreEqual(0xfe, payload[1]);
            string text = BoundedIni.Decode(payload, 16 * 1024);
            Dictionary<string, string> section = BoundedIni.ReadSection(text, "Command");
            Assert.AreEqual(first.ToString("D"), section["Id"]);
            Assert.AreEqual("stop", section["Action"]);

            LocalMutationException exception = Assert.ThrowsExactly<LocalMutationException>(
                () => mailbox.Enqueue(
                    Guid.Parse("22222222-2222-4222-8222-222222222222"),
                    "stop",
                    null));
            Assert.AreEqual("MAILBOX_BUSY", exception.Code);
            Assert.AreEqual(first.ToString("D"), BoundedIni.ReadSection(
                BoundedIni.Decode(File.ReadAllBytes(path), 16 * 1024),
                "Command")["Id"]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void SameCommandMayBeObservedIdempotentlyWithoutRewriting()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"UltimateRemoteAgent.MailboxTests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "remote_command.ini");
        try
        {
            var mailbox = new RemoteMailbox(path);
            Guid commandId = Guid.Parse("11111111-1111-4111-8111-111111111111");
            mailbox.Enqueue(commandId, "stop", null);
            DateTime before = File.GetLastWriteTimeUtc(path);

            mailbox.Enqueue(commandId, "stop", null);

            Assert.AreEqual(before, File.GetLastWriteTimeUtc(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
