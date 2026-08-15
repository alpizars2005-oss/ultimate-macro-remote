using System.Diagnostics;
using System.Globalization;
using System.Management;

namespace UltimateRemoteAgent.Local;

internal sealed record MacroProcessIdentity(int ProcessId, DateTime CreationTimeUtc);

internal sealed record ProcessCensus(
    IReadOnlyList<MacroProcessIdentity> ExactMacroProcesses,
    bool RobloxRunning,
    bool HasIndeterminateMacroCandidate = false);

internal interface IMacroProcessCensus
{
    ProcessCensus Sample();

    bool IsStillSame(MacroProcessIdentity identity);
}

internal sealed class WmiMacroProcessCensus : IMacroProcessCensus
{
    private readonly string _macroRoot;
    private readonly string _expectedExecutable;
    private readonly string _expectedScript;

    internal WmiMacroProcessCensus(string macroRoot)
    {
        _macroRoot = Enrollment.EnrollmentValidator.ValidateMacroRoot(macroRoot, requireFiles: true);
        string executable = Path.Combine(_macroRoot, "submacros", "AutoHotkey64.exe");
        string script = Path.Combine(_macroRoot, "Main_Remote.ahk");
        if ((File.GetAttributes(executable) & FileAttributes.ReparsePoint) != 0 ||
            (File.GetAttributes(script) & FileAttributes.ReparsePoint) != 0)
        {
            throw new Enrollment.EnrollmentException("MACRO_INSTALLATION_INVALID");
        }

        string finalRoot = Path.TrimEndingDirectorySeparator(
            WindowsFinalPath.ResolveExistingDirectory(_macroRoot));
        _expectedExecutable = WindowsFinalPath.ResolveExistingFile(executable);
        _expectedScript = WindowsFinalPath.ResolveExistingFile(script);
        string expectedFinalExecutable = Path.Combine(finalRoot, "submacros", "AutoHotkey64.exe");
        string expectedFinalScript = Path.Combine(finalRoot, "Main_Remote.ahk");
        if (!string.Equals(_expectedExecutable, expectedFinalExecutable, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(_expectedScript, expectedFinalScript, StringComparison.OrdinalIgnoreCase))
        {
            throw new Enrollment.EnrollmentException("MACRO_INSTALLATION_INVALID");
        }
    }

    public ProcessCensus Sample()
    {
        try
        {
            var matches = new List<MacroProcessIdentity>();
            using var searcher = CreateSearcher(
                "SELECT ProcessId, CreationDate, ExecutablePath, CommandLine " +
                "FROM Win32_Process WHERE Name = 'AutoHotkey64.exe'");
            using ManagementObjectCollection processes = searcher.Get();
            bool indeterminate = false;
            foreach (ManagementBaseObject process in processes)
            {
                using (process)
                {
                    CandidateAssessment assessment = Match(process);
                    if (assessment.Identity is not null)
                    {
                        matches.Add(assessment.Identity);
                    }
                    indeterminate |= assessment.Indeterminate;
                }
            }

            bool roblox = IsRobloxRunning();
            return new ProcessCensus(matches, roblox, indeterminate);
        }
        catch (Exception exception) when (IsCensusFailure(exception))
        {
            throw new LocalStatusException("PROCESS_CENSUS_FAILED", exception);
        }
    }

    public bool IsStillSame(MacroProcessIdentity identity)
    {
        try
        {
            using var searcher = CreateSearcher(
                "SELECT ProcessId, CreationDate, ExecutablePath, CommandLine " +
                $"FROM Win32_Process WHERE ProcessId = {identity.ProcessId.ToString(CultureInfo.InvariantCulture)}");
            using ManagementObjectCollection processes = searcher.Get();
            CandidateAssessment assessment = CandidateAssessment.NotMatch;
            int count = 0;
            foreach (ManagementBaseObject process in processes)
            {
                using (process)
                {
                    count++;
                    assessment = Match(process);
                }
            }

            return count == 1 && !assessment.Indeterminate && assessment.Identity == identity;
        }
        catch (Exception exception) when (IsCensusFailure(exception))
        {
            return false;
        }
    }

    private static ManagementObjectSearcher CreateSearcher(string query)
    {
        var options = new System.Management.EnumerationOptions
        {
            BlockSize = 16,
            DirectRead = true,
            EnsureLocatable = false,
            EnumerateDeep = false,
            ReturnImmediately = false,
            Rewindable = false,
            Timeout = TimeSpan.FromSeconds(3),
        };
        return new ManagementObjectSearcher(
            new ManagementScope(@"\\.\root\CIMV2"),
            new ObjectQuery(query),
            options);
    }

    private CandidateAssessment Match(ManagementBaseObject process)
    {
        if (process["ProcessId"] is not uint rawProcessId || rawProcessId > int.MaxValue ||
            process["ExecutablePath"] is not string executablePath ||
            process["CommandLine"] is not string commandLine ||
            process["CreationDate"] is not string creationDate)
        {
            return CandidateAssessment.Unknown;
        }

        string finalExecutable;
        try
        {
            finalExecutable = WindowsFinalPath.ResolveExistingFile(executablePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return CandidateAssessment.Unknown;
        }

        if (!string.Equals(finalExecutable, _expectedExecutable, StringComparison.OrdinalIgnoreCase))
        {
            return CandidateAssessment.NotMatch;
        }

        string[] arguments = WindowsCommandLine.Parse(commandLine);
        if (!WindowsCommandLine.TryGetScriptFileArgument(arguments, out string? scriptArgument))
        {
            return CandidateAssessment.Unknown;
        }
        if (scriptArgument is null)
        {
            return CandidateAssessment.NotMatch;
        }

        CandidateAssessment scriptAssessment = AssessScriptArgument(scriptArgument);
        if (scriptAssessment.Indeterminate || scriptAssessment.Identity is null)
        {
            return scriptAssessment;
        }

        DateTime created;
        try
        {
            created = ManagementDateTimeConverter.ToDateTime(creationDate).ToUniversalTime();
        }
        catch (ArgumentOutOfRangeException)
        {
            return CandidateAssessment.Unknown;
        }

        return CandidateAssessment.Match(
            new MacroProcessIdentity((int)rawProcessId, created));
    }

    private CandidateAssessment AssessScriptArgument(string argument)
    {
        if (string.IsNullOrWhiteSpace(argument) || argument.Contains('\0', StringComparison.Ordinal))
        {
            return CandidateAssessment.NotMatch;
        }

        try
        {
            if (!Path.IsPathFullyQualified(argument))
            {
                return CandidateAssessment.Unknown;
            }

            string candidate = Path.IsPathFullyQualified(argument)
                ? Path.GetFullPath(argument)
                : Path.GetFullPath(argument, _macroRoot);
            string expectedLexicalPath = Path.Combine(_macroRoot, "Main_Remote.ahk");
            if (!string.Equals(candidate, expectedLexicalPath, StringComparison.OrdinalIgnoreCase))
            {
                return CandidateAssessment.NotMatch;
            }
            if (!File.Exists(candidate))
            {
                return CandidateAssessment.Unknown;
            }

            return string.Equals(
                WindowsFinalPath.ResolveExistingFile(candidate),
                _expectedScript,
                StringComparison.OrdinalIgnoreCase)
                ? CandidateAssessment.ScriptMatch
                : CandidateAssessment.NotMatch;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return CandidateAssessment.Unknown;
        }
    }

    private static bool IsRobloxRunning()
    {
        Process[] processes = Process.GetProcessesByName("RobloxPlayerBeta");
        try
        {
            return processes.Any(process =>
            {
                try
                {
                    return !process.HasExited;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            });
        }
        finally
        {
            foreach (Process process in processes)
            {
                process.Dispose();
            }
        }
    }

    private static bool IsCensusFailure(Exception exception) =>
        exception is ManagementException or UnauthorizedAccessException or InvalidOperationException or TimeoutException or NotSupportedException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException;

    private sealed record CandidateAssessment(
        MacroProcessIdentity? Identity,
        bool Indeterminate)
    {
        internal static CandidateAssessment NotMatch { get; } = new(null, false);

        internal static CandidateAssessment Unknown { get; } = new(null, true);

        internal static CandidateAssessment ScriptMatch { get; } =
            new(new MacroProcessIdentity(0, default), false);

        internal static CandidateAssessment Match(MacroProcessIdentity identity) =>
            new(identity, false);
    }
}
