using System.Text.RegularExpressions;

namespace UltimateRemoteAgent.Enrollment;

internal sealed record EnrollmentRecord(
    int Version,
    Uri HttpsOrigin,
    Uri WebSocketUri,
    string MacroRoot,
    string DeviceCredential)
{
    internal const int CurrentVersion = 1;

    public override string ToString() =>
        $"EnrollmentRecord {{ Version = {Version}, Endpoint = [redacted], MacroRoot = [redacted], DeviceCredential = [redacted] }}";
}

internal static partial class EnrollmentValidator
{
    internal const string AgentPath = "/remote/v1/agent";

    [GeneratedRegex(
        @"\Aurad_v1\.[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\.[A-Za-z0-9_-]{43}\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex DeviceCredentialPattern();

    internal static EnrollmentRecord Validate(EnrollmentRecord record, bool requireFiles)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Version != EnrollmentRecord.CurrentVersion)
        {
            throw new EnrollmentException("ENROLLMENT_VERSION_UNSUPPORTED");
        }

        Uri origin = ValidateOrigin(record.HttpsOrigin);
        Uri socket = ValidateWebSocketUri(record.WebSocketUri, origin);
        if (!DeviceCredentialPattern().IsMatch(record.DeviceCredential))
        {
            throw new EnrollmentException("ENROLLMENT_CREDENTIAL_INVALID");
        }

        string macroRoot = ValidateMacroRoot(record.MacroRoot, requireFiles);
        return new EnrollmentRecord(record.Version, origin, socket, macroRoot, record.DeviceCredential);
    }

    internal static Uri ValidateOrigin(Uri origin)
    {
        ArgumentNullException.ThrowIfNull(origin);
        if (!origin.IsAbsoluteUri ||
            !string.Equals(origin.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(origin.UserInfo) ||
            !string.IsNullOrEmpty(origin.Query) ||
            !string.IsNullOrEmpty(origin.Fragment) ||
            origin.AbsolutePath is not ("" or "/"))
        {
            throw new EnrollmentException("HTTPS_ORIGIN_INVALID");
        }

        var builder = new UriBuilder(origin)
        {
            Scheme = Uri.UriSchemeHttps,
            Path = "/",
            Query = string.Empty,
            Fragment = string.Empty,
        };
        return builder.Uri;
    }

    internal static Uri ValidateWebSocketUri(Uri socket, Uri origin)
    {
        ArgumentNullException.ThrowIfNull(socket);
        if (!socket.IsAbsoluteUri ||
            !string.Equals(socket.Scheme, "wss", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(socket.UserInfo) ||
            !string.IsNullOrEmpty(socket.Query) ||
            !string.IsNullOrEmpty(socket.Fragment) ||
            !string.Equals(socket.AbsolutePath, AgentPath, StringComparison.Ordinal) ||
            !string.Equals(socket.IdnHost, origin.IdnHost, StringComparison.OrdinalIgnoreCase) ||
            socket.Port != origin.Port)
        {
            throw new EnrollmentException("WSS_ENDPOINT_INVALID");
        }

        return socket;
    }

    internal static string ValidateMacroRoot(string value, bool requireFiles)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\0', StringComparison.Ordinal))
        {
            throw new EnrollmentException("MACRO_ROOT_INVALID");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(value.Trim());
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new EnrollmentException("MACRO_ROOT_INVALID", exception);
        }

        if (!Path.IsPathFullyQualified(fullPath) || IsNetworkOrDevicePath(fullPath))
        {
            throw new EnrollmentException("MACRO_ROOT_INVALID");
        }

        try
        {
            string? root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root) || new DriveInfo(root).DriveType == DriveType.Network)
            {
                throw new EnrollmentException("MACRO_ROOT_INVALID");
            }
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            throw new EnrollmentException("MACRO_ROOT_INVALID", exception);
        }

        if (requireFiles &&
            (!Directory.Exists(fullPath) ||
             !File.Exists(Path.Combine(fullPath, "Main_Remote.ahk")) ||
             !File.Exists(Path.Combine(fullPath, "submacros", "AutoHotkey64.exe"))))
        {
            throw new EnrollmentException("MACRO_INSTALLATION_INVALID");
        }

        return Path.TrimEndingDirectorySeparator(fullPath);
    }

    private static bool IsNetworkOrDevicePath(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal) ||
        path.StartsWith("//", StringComparison.Ordinal) ||
        path.StartsWith(@"\\?\", StringComparison.Ordinal) ||
        path.StartsWith(@"\\.\", StringComparison.Ordinal);
}

internal sealed class EnrollmentException : Exception
{
    internal EnrollmentException(string code, Exception? innerException = null)
        : base(code, innerException) => Code = code;

    internal string Code { get; }
}
