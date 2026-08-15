using System.Runtime.InteropServices;

namespace UltimateRemoteAgent.Local;

internal static class WindowsCommandLine
{
    internal static string[] Parse(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return [];
        }

        IntPtr argv = CommandLineToArgvW(commandLine, out int count);
        if (argv == IntPtr.Zero)
        {
            return [];
        }

        try
        {
            var result = new string[count];
            for (int index = 0; index < count; index++)
            {
                IntPtr item = Marshal.ReadIntPtr(argv, index * IntPtr.Size);
                result[index] = Marshal.PtrToStringUni(item) ?? string.Empty;
            }

            return result;
        }
        finally
        {
            _ = LocalFree(argv);
        }
    }

    internal static bool TryGetScriptFileArgument(
        IReadOnlyList<string> arguments,
        out string? scriptArgument)
    {
        scriptArgument = null;
        if (arguments.Count == 0)
        {
            return false;
        }

        for (int index = 1; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            if (IsKnownInterpreterSwitch(argument))
            {
                continue;
            }

            if (argument.StartsWith('/') || argument.StartsWith('-'))
            {
                return false;
            }

            scriptArgument = argument;
            return true;
        }

        return true;
    }

    private static bool IsKnownInterpreterSwitch(string value)
    {
        if (value.Equals("/force", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("/restart", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("/script", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("/ErrorStdOut", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("/Debug", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (value.StartsWith("/ErrorStdOut=", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("/Debug=", StringComparison.OrdinalIgnoreCase))
        {
            return value.Length > value.IndexOf('=') + 1;
        }

        return value.Length > 3 &&
            value.StartsWith("/CP", StringComparison.OrdinalIgnoreCase) &&
            value.AsSpan(3).IndexOfAnyExceptInRange('0', '9') < 0;
    }

    [DllImport("shell32.dll", EntryPoint = "CommandLineToArgvW", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int argumentCount);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr LocalFree(IntPtr memory);
}
