using System.Reflection;
using UltimateRemoteAgent.Local;
using UltimateRemoteAgent.Protocol;

namespace UltimateRemoteAgent.Tests;

[TestClass]
public sealed class StrategyCatalogTests
{
    [TestMethod]
    public void Load_UsesDocumentedGoldenVector_AndWireSummaryContainsNoPath()
    {
        using var layout = new TestLayout();
        layout.CreateStrategy("Example.strat");

        StrategyCatalog catalog = StrategyCatalog.Load(layout.MacroRoot);

        StrategySummary summary = AssertSingle(catalog.Strategies);
        Assert.AreEqual(
            "s_kjr-1a5HJUSQFEg2FqPYT2mO4PCYETBQQIUyI-rvxC8",
            summary.StrategyId);
        Assert.AreEqual("Example", summary.Name);

        string[] publicProperties = typeof(StrategySummary)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        CollectionAssert.AreEqual(new[] { "Name", "StrategyId" }, publicProperties);

        string wireJson = System.Text.Encoding.UTF8.GetString(
            ProtocolCodec.EncodeCompletedStrategies(Guid.NewGuid(), catalog.Strategies));
        StringAssert.Contains(wireJson, "\"strategy_id\"");
        StringAssert.Contains(wireJson, "\"name\"");
        Assert.IsFalse(
            wireJson.Contains(layout.MacroRoot, StringComparison.OrdinalIgnoreCase),
            "The wire summary must never disclose the local macro path.");
    }

    [TestMethod]
    public void Load_EnumeratesOnlyTopLevelStratFiles()
    {
        using var layout = new TestLayout();
        layout.CreateStrategy("Top.strat");
        File.WriteAllText(Path.Combine(layout.StrategyRoot, "notes.txt"), "ignored");
        string nested = Directory.CreateDirectory(
            Path.Combine(layout.StrategyRoot, "nested")).FullName;
        File.WriteAllText(Path.Combine(nested, "Hidden.strat"), "ignored");

        StrategyCatalog catalog = StrategyCatalog.Load(layout.MacroRoot);

        StrategySummary summary = AssertSingle(catalog.Strategies);
        Assert.AreEqual("Top", summary.Name);
    }

    [TestMethod]
    public void Load_TreatsDiscordFormattingAndMentionsAsUntrustedDisplayText()
    {
        using var layout = new TestLayout();
        const string hostileName = "@everyone __bold__ [link](x)";
        layout.CreateStrategy($"{hostileName}.strat");

        StrategySummary summary = AssertSingle(
            StrategyCatalog.Load(layout.MacroRoot).Strategies);

        Assert.AreEqual(hostileName, summary.Name);
        Assert.IsFalse(summary.StrategyId.Contains(hostileName, StringComparison.Ordinal));
    }

    [TestMethod]
    public void Catalog_RetainsValidatedLocalMappingWithoutPuttingPathsOnTheWire()
    {
        using var layout = new TestLayout();
        string strategyPath = layout.CreateStrategy("Mapped.strat");
        string outsidePath = Path.Combine(layout.BaseDirectory, "outside.strat");
        File.WriteAllText(outsidePath, "outside");
        string traversalPath = Path.Combine(
            layout.StrategyRoot,
            "..",
            "Strats",
            "Mapped.strat");
        StrategyCatalog catalog = StrategyCatalog.Load(layout.MacroRoot);
        StrategySummary summary = AssertSingle(catalog.Strategies);

        Assert.IsTrue(
            catalog.TryGetStrategyIdForCanonicalPath(strategyPath, out string? strategyId));
        Assert.AreEqual(summary.StrategyId, strategyId);
        Assert.IsTrue(
            catalog.TryResolveCanonicalPath(summary.StrategyId, out string? resolvedPath));
        Assert.IsTrue(string.Equals(
            Path.GetFullPath(strategyPath),
            resolvedPath,
            StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(
            catalog.TryGetStrategyIdForCanonicalPath(outsidePath, out string? outsideId));
        Assert.IsNull(outsideId);
        Assert.IsFalse(
            catalog.TryGetStrategyIdForCanonicalPath(traversalPath, out string? traversalId));
        Assert.IsNull(traversalId);
    }

    [TestMethod]
    public void Load_RejectsUnicodeNormalizedDuplicateNames()
    {
        using var layout = new TestLayout();
        layout.CreateStrategy("\u00e9.strat");
        layout.CreateStrategy("e\u0301.strat");
        if (Directory.EnumerateFiles(layout.StrategyRoot).Count() != 2)
        {
            Assert.Inconclusive("The test filesystem normalizes Unicode filenames.");
        }

        StrategyCatalogException exception = Assert.Throws<StrategyCatalogException>(
            () => StrategyCatalog.Load(layout.MacroRoot));

        Assert.AreEqual(StrategyCatalogError.DuplicateStrategy, exception.Error);
        Assert.IsFalse(
            exception.Message.Contains(layout.MacroRoot, StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void Load_RejectsTraversalAndDeviceMacroRootsBeforeFilesystemAccess()
    {
        using var layout = new TestLayout();
        string traversal = Path.Combine(layout.MacroRoot, "child", "..", "..");

        StrategyCatalogException traversalException =
            Assert.Throws<StrategyCatalogException>(
                () => StrategyCatalog.Load(traversal));
        StrategyCatalogException deviceException =
            Assert.Throws<StrategyCatalogException>(
                () => StrategyCatalog.Load($@"\\?\{layout.MacroRoot}"));
        StrategyCatalogException uncException =
            Assert.Throws<StrategyCatalogException>(
                () => StrategyCatalog.Load(@"\\server\share\macro"));

        Assert.AreEqual(StrategyCatalogError.InvalidMacroRoot, traversalException.Error);
        Assert.AreEqual(StrategyCatalogError.InvalidMacroRoot, deviceException.Error);
        Assert.AreEqual(StrategyCatalogError.InvalidMacroRoot, uncException.Error);
    }

    [TestMethod]
    public void Load_RejectsAReparsePointStrategyFile_WhenSymlinksAreAvailable()
    {
        using var layout = new TestLayout();
        string target = Path.Combine(layout.BaseDirectory, "outside.strat");
        File.WriteAllText(target, "outside");
        string link = Path.Combine(layout.StrategyRoot, "Linked.strat");

        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception exception) when (SymlinksUnavailable(exception))
        {
            Assert.Inconclusive("Creating symbolic links is unavailable for this test user.");
        }

        try
        {
            StrategyCatalogException exception =
                Assert.Throws<StrategyCatalogException>(
                    () => StrategyCatalog.Load(layout.MacroRoot));
            Assert.AreEqual(StrategyCatalogError.ReparsePointRejected, exception.Error);
        }
        finally
        {
            File.Delete(link);
        }
    }

    [TestMethod]
    public void Load_RejectsAReparsePointApprovedRoot_WhenSymlinksAreAvailable()
    {
        using var layout = new TestLayout(createStrategyRoot: false);
        string outside = Directory.CreateDirectory(
            Path.Combine(layout.BaseDirectory, "outside-root")).FullName;
        string link = layout.StrategyRoot;

        try
        {
            Directory.CreateSymbolicLink(link, outside);
        }
        catch (Exception exception) when (SymlinksUnavailable(exception))
        {
            Assert.Inconclusive("Creating symbolic links is unavailable for this test user.");
        }

        try
        {
            StrategyCatalogException exception =
                Assert.Throws<StrategyCatalogException>(
                    () => StrategyCatalog.Load(layout.MacroRoot));
            Assert.AreEqual(StrategyCatalogError.ReparsePointRejected, exception.Error);
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [TestMethod]
    public void Load_RejectsMoreThanFiveHundredStrategies()
    {
        using var layout = new TestLayout();
        for (int index = 0; index <= StrategyCatalog.MaximumStrategies; index++)
        {
            layout.CreateStrategy($"Strategy-{index:D3}.strat");
        }

        StrategyCatalogException exception = Assert.Throws<StrategyCatalogException>(
            () => StrategyCatalog.Load(layout.MacroRoot));

        Assert.AreEqual(StrategyCatalogError.TooManyStrategies, exception.Error);
    }

    private static StrategySummary AssertSingle(IReadOnlyList<StrategySummary> strategies)
    {
        Assert.AreEqual(1, strategies.Count);
        return strategies[0];
    }

    private static bool SymlinksUnavailable(Exception exception) => exception is
        UnauthorizedAccessException
        or IOException
        or PlatformNotSupportedException
        or NotSupportedException;

    private sealed class TestLayout : IDisposable
    {
        internal TestLayout(bool createStrategyRoot = true)
        {
            BaseDirectory = Path.Combine(
                Path.GetTempPath(),
                $"UltimateRemoteAgent.Tests-{Guid.NewGuid():N}");
            MacroRoot = Path.Combine(BaseDirectory, "macro");
            StrategyRoot = Path.Combine(MacroRoot, "Resources", "Strats");
            Directory.CreateDirectory(Path.Combine(MacroRoot, "Resources"));
            if (createStrategyRoot)
            {
                Directory.CreateDirectory(StrategyRoot);
            }
        }

        internal string BaseDirectory { get; }

        internal string MacroRoot { get; }

        internal string StrategyRoot { get; }

        internal string CreateStrategy(string fileName)
        {
            string path = Path.Combine(StrategyRoot, fileName);
            File.WriteAllText(path, "test");
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(BaseDirectory))
            {
                Directory.Delete(BaseDirectory, recursive: true);
            }
        }
    }
}
