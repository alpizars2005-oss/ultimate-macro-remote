using UltimateRemoteAgent.Local;
using UltimateRemoteAgent.Protocol;

namespace UltimateRemoteAgent.Tests;

[TestClass]
public sealed class FailClosedMutationTests
{
    [TestMethod]
    public async Task CancelledStopKeepsMailboxAndExecutingJournalInsteadOfReportingFailure()
    {
        using var installation = new TemporaryRemoteInstallation();
        string mailboxPath = Path.Combine(installation.StateDirectory, "remote_command.ini");
        string statePath = Path.Combine(installation.StateDirectory, "state.ini");
        string journalDirectory = Path.Combine(installation.StateDirectory, "journal");
        using var bridge = new RemoteLocalBridge(
            installation.Root,
            mailboxPath,
            statePath,
            journalDirectory);
        var journal = new RemoteCommandJournal(journalDirectory);
        Guid commandId = Guid.Parse("11111111-1111-4111-8111-111111111111");
        var command = new CommandMessage(
            commandId,
            RemoteOperation.StopSafe,
            DateTimeOffset.UtcNow.AddSeconds(-1),
            DateTimeOffset.UtcNow.AddMinutes(1),
            CommandArguments.Empty);
        CommandJournalEntry accepted = journal.CreateAccepted(command, 0, 0);
        var prepared = new PreparedMutation(command, accepted, null, null);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => bridge.ExecuteMutationAsync(prepared, cancellation.Token));

        Assert.IsTrue(File.Exists(mailboxPath));
        CommandJournalEntry persisted = journal.TryLoad(commandId)
            ?? throw new AssertFailedException("Mutation journal was not persisted.");
        Assert.AreEqual(JournalStage.Executing, persisted.Stage);
        Assert.IsNull(persisted.ErrorCode);
    }

    [TestMethod]
    public async Task ReconcilingExecutingStopNeverReplaysOrTimesOutWithoutEvidence()
    {
        using var installation = new TemporaryRemoteInstallation();
        string mailboxPath = Path.Combine(installation.StateDirectory, "remote_command.ini");
        string statePath = Path.Combine(installation.StateDirectory, "state.ini");
        string journalDirectory = Path.Combine(installation.StateDirectory, "journal");
        using var bridge = new RemoteLocalBridge(
            installation.Root,
            mailboxPath,
            statePath,
            journalDirectory);
        var journal = new RemoteCommandJournal(journalDirectory);
        Guid commandId = Guid.Parse("22222222-2222-4222-8222-222222222222");
        var command = new CommandMessage(
            commandId,
            RemoteOperation.StopSafe,
            DateTimeOffset.UtcNow.AddSeconds(-1),
            DateTimeOffset.UtcNow.AddMinutes(1),
            CommandArguments.Empty);
        CommandJournalEntry executing = journal.MarkExecuting(
            journal.CreateAccepted(command, 0, 0));
        File.WriteAllText(mailboxPath, "sentinel");
        DateTime before = File.GetLastWriteTimeUtc(mailboxPath);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => bridge.ReconcileAsync(
                new ReconciliationCommand(commandId, RemoteOperation.StopSafe),
                cancellation.Token));

        Assert.AreEqual("sentinel", File.ReadAllText(mailboxPath));
        Assert.AreEqual(before, File.GetLastWriteTimeUtc(mailboxPath));
        CommandJournalEntry persisted = journal.TryLoad(commandId)
            ?? throw new AssertFailedException("Mutation journal was not persisted.");
        Assert.AreEqual(JournalStage.Executing, persisted.Stage);
        Assert.AreEqual(executing.CommandId, persisted.CommandId);
    }

    private sealed class TemporaryRemoteInstallation : IDisposable
    {
        internal TemporaryRemoteInstallation()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                $"UltimateRemoteAgent.FailClosed.{Guid.NewGuid():N}");
            StateDirectory = Path.Combine(Root, "test-state");
            Directory.CreateDirectory(Path.Combine(Root, "submacros"));
            Directory.CreateDirectory(Path.Combine(Root, "Resources", "Strats"));
            Directory.CreateDirectory(StateDirectory);
            File.WriteAllText(Path.Combine(Root, "Main_Remote.ahk"), "; test");
            File.WriteAllBytes(Path.Combine(Root, "submacros", "AutoHotkey64.exe"), [0x4d, 0x5a]);
        }

        internal string Root { get; }
        internal string StateDirectory { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
