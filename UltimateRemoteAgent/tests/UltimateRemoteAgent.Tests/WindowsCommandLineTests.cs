using UltimateRemoteAgent.Local;

namespace UltimateRemoteAgent.Tests;

[TestClass]
public sealed class WindowsCommandLineTests
{
    [TestMethod]
    public void ScriptParserAcceptsNormalAndReloadedAutoHotkeyCommands()
    {
        Assert.IsTrue(WindowsCommandLine.TryGetScriptFileArgument(
            [@"C:\Macro\submacros\AutoHotkey64.exe", @"C:\Macro\Main_Remote.ahk"],
            out string? normal));
        Assert.AreEqual(@"C:\Macro\Main_Remote.ahk", normal);

        Assert.IsTrue(WindowsCommandLine.TryGetScriptFileArgument(
            [@"C:\Macro\submacros\AutoHotkey64.exe", "/restart", @"C:\Macro\Main_Remote.ahk"],
            out string? restarted));
        Assert.AreEqual(@"C:\Macro\Main_Remote.ahk", restarted);
    }

    [TestMethod]
    public void ScriptParserReturnsRelativeScriptForCallerToRejectAsIndeterminate()
    {
        Assert.IsTrue(WindowsCommandLine.TryGetScriptFileArgument(
            [@"C:\Macro\submacros\AutoHotkey64.exe", "/restart", "Main_Remote.ahk"],
            out string? script));
        Assert.AreEqual("Main_Remote.ahk", script);
        Assert.IsFalse(Path.IsPathFullyQualified(script!));
    }

    [TestMethod]
    public void ScriptParserFailsClosedOnUnknownPreScriptSwitch()
    {
        Assert.IsFalse(WindowsCommandLine.TryGetScriptFileArgument(
            [@"C:\Macro\submacros\AutoHotkey64.exe", "/unknown", @"C:\Macro\Main_Remote.ahk"],
            out string? script));
        Assert.IsNull(script);
    }
}
