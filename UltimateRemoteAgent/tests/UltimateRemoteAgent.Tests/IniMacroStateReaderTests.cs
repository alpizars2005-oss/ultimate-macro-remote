using System.Text;
using UltimateRemoteAgent.Local;

namespace UltimateRemoteAgent.Tests;

[TestClass]
public sealed class IniMacroStateReaderTests
{
    [TestMethod]
    public void ReadParsesAllowlistedStateFieldsFromUtf16LittleEndianFile()
    {
        using var file = new TemporaryStateFile();
        file.WriteUtf16(
            """
            [Cache]
            Running=0
            [State]
            Running=1
            Strategy=C:\private\approved.strat
            CurrentRunCount=7
            StartTime=1234
            TimeWhenStartedPlaying=5678
            [Remote]
            LastDetails=C:\private\must-not-be-consumed.txt
            """);

        MacroStateData state = new IniMacroStateReader(file.Path).Read();

        Assert.IsTrue(state.Running);
        Assert.AreEqual(@"C:\private\approved.strat", state.StrategyPath);
        Assert.AreEqual(7L, state.CurrentRunCount);
        Assert.AreEqual(1234U, state.StartTime);
        Assert.AreEqual(5678U, state.TimeWhenStartedPlaying);
    }

    [TestMethod]
    public void ReadRejectsMalformedUtf16StateInsteadOfGuessing()
    {
        using var file = new TemporaryStateFile();
        file.WriteUtf16(
            """
            [State]
            Running=1
            Running=0
            Strategy=C:\private\ambiguous.strat
            """);

        LocalStatusException exception = Assert.ThrowsExactly<LocalStatusException>(
            () => new IniMacroStateReader(file.Path).Read());

        Assert.AreEqual("STATE_FORMAT_INVALID", exception.Code);
        Assert.IsFalse(exception.Message.Contains("private", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void ReadRejectsOversizedStateBeforeParsingIt()
    {
        using var file = new TemporaryStateFile();
        file.WriteBytes(new byte[(128 * 1024) + 1]);

        LocalStatusException exception = Assert.ThrowsExactly<LocalStatusException>(
            () => new IniMacroStateReader(file.Path).Read());

        Assert.AreEqual("STATE_UNAVAILABLE", exception.Code);
    }

    private sealed class TemporaryStateFile : IDisposable
    {
        private readonly string _directory;

        internal TemporaryStateFile()
        {
            _directory = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"UltimateRemoteAgent.IniTests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(_directory);
            Path = System.IO.Path.Combine(_directory, "state.ini");
        }

        internal string Path { get; }

        internal void WriteUtf16(string text) =>
            File.WriteAllText(Path, text, new UnicodeEncoding(false, true, true));

        internal void WriteBytes(byte[] payload) => File.WriteAllBytes(Path, payload);

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
