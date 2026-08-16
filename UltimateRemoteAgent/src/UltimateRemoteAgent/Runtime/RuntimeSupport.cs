using System.Text.RegularExpressions;

namespace UltimateRemoteAgent.Runtime;

internal sealed class AgentRuntimeException : Exception
{
    internal AgentRuntimeException(string code, Exception? innerException = null)
        : base(code, innerException) => Code = code;

    internal string Code { get; }
}

internal static partial class SafeLog
{
    internal static void Info(string code) => Write("INFO", code);

    internal static void Warning(string code) => Write("WARN", code);

    internal static void Error(string code) => Write("ERROR", code);

    private static void Write(string level, string code)
    {
        string safeCode = LogCodePattern().IsMatch(code ?? string.Empty)
            ? code
            : "INVALID_LOG_CODE";
        Console.Error.WriteLine($"{DateTimeOffset.UtcNow:O} {level} {safeCode}");
    }

    [GeneratedRegex("^[A-Z][A-Z0-9_]{0,63}$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex LogCodePattern();
}

internal static class SecretConsole
{
    internal static string ReadPairingTicket()
    {
        Console.Error.Write("Pairing ticket: ");
        if (Console.IsInputRedirected)
        {
            string redirected = (Console.ReadLine() ?? string.Empty).Trim();
            Console.Error.WriteLine();
            return redirected;
        }

        var buffer = new char[96];
        int length = 0;
        try
        {
            while (true)
            {
                ConsoleKeyInfo key = Console.ReadKey(intercept: true);
                if (key.Key is ConsoleKey.Enter)
                {
                    Console.Error.WriteLine();
                    return new string(buffer, 0, length);
                }

                if (key.Key is ConsoleKey.Backspace)
                {
                    if (length > 0)
                    {
                        buffer[--length] = '\0';
                    }
                    continue;
                }

                if (char.IsControl(key.KeyChar))
                {
                    continue;
                }

                if (length >= buffer.Length)
                {
                    throw new AgentRuntimeException("PAIRING_TICKET_TOO_LONG");
                }

                buffer[length++] = key.KeyChar;
            }
        }
        finally
        {
            Array.Clear(buffer);
        }
    }
}

internal sealed class InteractiveUserInstanceLock : IDisposable
{
    private readonly FileStream _stream;
    private bool _disposed;

    private InteractiveUserInstanceLock(FileStream stream) => _stream = stream;

    internal static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UltimateRemoteAgent",
        "instance.lock");

    internal static InteractiveUserInstanceLock Acquire(string? path = null)
    {
        string lockPath = Path.GetFullPath(path ?? DefaultPath);
        string? directory = Path.GetDirectoryName(lockPath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new AgentRuntimeException("INSTANCE_LOCK_PATH_INVALID");
        }

        try
        {
            Directory.CreateDirectory(directory);
            var stream = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                1,
                FileOptions.WriteThrough);
            return new InteractiveUserInstanceLock(stream);
        }
        catch (IOException exception)
        {
            throw new AgentRuntimeException("AGENT_ALREADY_RUNNING", exception);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new AgentRuntimeException("INSTANCE_LOCK_FAILED", exception);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stream.Dispose();
    }
}
