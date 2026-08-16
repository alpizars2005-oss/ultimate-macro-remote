using UltimateRemoteAgent.Local;
using UltimateRemoteAgent.Protocol;

namespace UltimateRemoteAgent.Tests;

[TestClass]
public sealed class RemoteCommandJournalTests
{
    [TestMethod]
    public void JournalPersistsLifecycleAndStartBaselineWithoutArgumentsFromNetworkPaths()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"UltimateRemoteAgent.JournalTests.{Guid.NewGuid():N}");
        try
        {
            var journal = new RemoteCommandJournal(directory);
            Guid id = Guid.Parse("11111111-1111-4111-8111-111111111111");
            var command = new CommandMessage(
                id,
                RemoteOperation.StartStrategy,
                DateTimeOffset.Parse("2026-08-15T18:00:00+00:00"),
                DateTimeOffset.Parse("2026-08-15T18:00:30+00:00"),
                new CommandArguments("strategy_alpha_01"));

            CommandJournalEntry accepted = journal.CreateAccepted(command, 123U, 7L);
            CommandJournalEntry executing = journal.MarkExecuting(accepted);
            var snapshot = new MacroSnapshot(
                MacroState.Running,
                true,
                "strategy_alpha_01");
            CommandJournalEntry completed = journal.MarkCompleted(
                executing,
                ActionResult.StrategyStarted,
                snapshot);

            CommandJournalEntry loaded = Assert.IsNotNull(journal.TryLoad(id));
            Assert.AreEqual(JournalStage.Completed, loaded.Stage);
            Assert.AreEqual(123U, loaded.BaselineTimeWhenStartedPlaying);
            Assert.AreEqual(7L, loaded.BaselineRunCount);
            Assert.AreEqual(ActionResult.StrategyStarted, loaded.ActionResult);
            Assert.AreEqual(snapshot, loaded.Snapshot);
            Assert.AreEqual("strategy_alpha_01", loaded.StrategyId);
            Assert.AreEqual(completed.CommandId, loaded.CommandId);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [TestMethod]
    public void JournalRefusesConflictingOrCorruptContent()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"UltimateRemoteAgent.JournalTests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        Guid id = Guid.Parse("11111111-1111-4111-8111-111111111111");
        try
        {
            File.WriteAllText(Path.Combine(directory, $"{id:D}.json"), "{not json}");
            var journal = new RemoteCommandJournal(directory);

            LocalMutationException exception = Assert.ThrowsExactly<LocalMutationException>(
                () => journal.TryLoad(id));
            Assert.AreEqual("JOURNAL_READ_FAILED", exception.Code);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
