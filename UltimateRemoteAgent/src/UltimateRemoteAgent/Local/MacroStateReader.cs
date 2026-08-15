using System.Globalization;
using System.Text;

namespace UltimateRemoteAgent.Local;

internal sealed record MacroStateData(
    bool Running,
    string? StrategyPath,
    long CurrentRunCount,
    uint StartTime,
    uint TimeWhenStartedPlaying,
    DateTime LastWriteTimeUtc = default);

internal interface IMacroStateReader
{
    MacroStateData Read();
}

internal sealed class IniMacroStateReader : IMacroStateReader
{
    private const int MaxStateBytes = 128 * 1024;
    private readonly string _statePath;

    internal IniMacroStateReader(string? statePath = null) =>
        _statePath = statePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Ultimate_Macro",
            "state.ini");

    public MacroStateData Read()
    {
        try
        {
            var before = new FileInfo(_statePath);
            if (!before.Exists || before.Length is <= 0 or > MaxStateBytes)
            {
                throw new LocalStatusException("STATE_UNAVAILABLE");
            }

            long length = before.Length;
            DateTime lastWrite = before.LastWriteTimeUtc;
            byte[] payload;
            using (var stream = new FileStream(
                _statePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                4096,
                FileOptions.SequentialScan))
            {
                payload = new byte[length];
                int total = 0;
                while (total < payload.Length)
                {
                    int read = stream.Read(payload, total, payload.Length - total);
                    if (read == 0)
                    {
                        break;
                    }
                    total += read;
                }
                if (total != payload.Length || stream.ReadByte() != -1)
                {
                    throw new LocalStatusException("STATE_CHANGED_DURING_READ");
                }
            }

            var after = new FileInfo(_statePath);
            if (!after.Exists || after.Length != length || after.LastWriteTimeUtc != lastWrite)
            {
                throw new LocalStatusException("STATE_CHANGED_DURING_READ");
            }

            return Parse(payload) with { LastWriteTimeUtc = lastWrite };
        }
        catch (LocalStatusException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new LocalStatusException("STATE_READ_FAILED", exception);
        }
    }

    private static MacroStateData Parse(byte[] payload)
    {
        string text;
        try
        {
            text = Decode(payload);
        }
        catch (DecoderFallbackException exception)
        {
            throw new LocalStatusException("STATE_FORMAT_INVALID", exception);
        }

        var state = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool inState = false;
        foreach (string rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line[0] is ';' or '#')
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                inState = string.Equals(line[1..^1].Trim(), "State", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inState)
            {
                continue;
            }

            int separator = line.IndexOf('=');
            if (separator <= 0)
            {
                throw new LocalStatusException("STATE_FORMAT_INVALID");
            }

            string key = line[..separator].Trim();
            string value = line[(separator + 1)..].Trim();
            if (!state.TryAdd(key, value))
            {
                throw new LocalStatusException("STATE_FORMAT_INVALID");
            }
        }

        if (!state.TryGetValue("Running", out string? runningText) ||
            runningText is not ("0" or "1"))
        {
            throw new LocalStatusException("STATE_FORMAT_INVALID");
        }

        return new MacroStateData(
            Running: runningText == "1",
            StrategyPath: state.GetValueOrDefault("Strategy"),
            CurrentRunCount: ParseNonNegativeInt64(state.GetValueOrDefault("CurrentRunCount")),
            StartTime: ParseUInt32(state.GetValueOrDefault("StartTime")),
            TimeWhenStartedPlaying: ParseUInt32(state.GetValueOrDefault("TimeWhenStartedPlaying")));
    }

    private static string Decode(byte[] payload)
    {
        if (payload.Length >= 2 && payload[0] == 0xff && payload[1] == 0xfe)
        {
            return new UnicodeEncoding(false, true, true).GetString(payload, 2, payload.Length - 2);
        }

        if (payload.Length >= 2 && payload[0] == 0xfe && payload[1] == 0xff)
        {
            return new UnicodeEncoding(true, true, true).GetString(payload, 2, payload.Length - 2);
        }

        if (payload.Length >= 3 && payload[0] == 0xef && payload[1] == 0xbb && payload[2] == 0xbf)
        {
            return new UTF8Encoding(false, true).GetString(payload, 3, payload.Length - 3);
        }

        bool looksUtf16 = payload.Take(Math.Min(payload.Length, 128)).Where((_, index) => index % 2 == 1).Count(value => value == 0) > 8;
        return looksUtf16
            ? new UnicodeEncoding(false, true, true).GetString(payload)
            : new UTF8Encoding(false, true).GetString(payload);
    }

    private static long ParseNonNegativeInt64(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return 0;
        }

        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed) || parsed < 0)
        {
            throw new LocalStatusException("STATE_FORMAT_INVALID");
        }

        return parsed;
    }

    private static uint ParseUInt32(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return 0;
        }

        if (!uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out uint parsed))
        {
            throw new LocalStatusException("STATE_FORMAT_INVALID");
        }

        return parsed;
    }
}
