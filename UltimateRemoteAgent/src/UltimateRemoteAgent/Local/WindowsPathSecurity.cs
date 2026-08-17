using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Microsoft.Win32.SafeHandles;

namespace UltimateRemoteAgent.Local;

internal sealed record ApprovedStrategyRoot(
    string CanonicalPath,
    string FinalPath,
    string FinalMacroRootPath);

internal static class WindowsPathSecurity
{
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const int MaximumFinalPathCharacters = 32_768;

    internal static ApprovedStrategyRoot OpenApprovedStrategyRoot(string macroRoot)
    {
        if (!OperatingSystem.IsWindows()
            || string.IsNullOrWhiteSpace(macroRoot)
            || HasForbiddenPathPrefix(macroRoot)
            || ContainsTraversalSegment(macroRoot)
            || !Path.IsPathFullyQualified(macroRoot))
        {
            throw new StrategyCatalogException(StrategyCatalogError.InvalidMacroRoot);
        }

        string canonicalMacroRoot;
        try
        {
            canonicalMacroRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(macroRoot));
        }
        catch (Exception exception) when (exception is
                                           ArgumentException
                                           or NotSupportedException
                                           or PathTooLongException)
        {
            throw new StrategyCatalogException(StrategyCatalogError.InvalidMacroRoot);
        }

        EnsureLocalDrivePath(canonicalMacroRoot);
        EnsureExistingDirectoryWithoutReparsePoint(
            canonicalMacroRoot,
            StrategyCatalogError.InvalidMacroRoot);

        string resourcesPath = Path.Combine(canonicalMacroRoot, "Resources");
        string approvedPath = Path.Combine(resourcesPath, "Strats");
        EnsureExistingDirectoryWithoutReparsePoint(
            resourcesPath,
            StrategyCatalogError.StrategyRootUnavailable);
        EnsureExistingDirectoryWithoutReparsePoint(
            approvedPath,
            StrategyCatalogError.StrategyRootUnavailable);

        string finalMacroRoot = ResolveExistingPath(
            canonicalMacroRoot,
            expectDirectory: true,
            rejectFinalReparsePoint: true);
        string finalApprovedRoot = ResolveExistingPath(
            approvedPath,
            expectDirectory: true,
            rejectFinalReparsePoint: true);
        EnsureLocalDrivePath(finalMacroRoot);
        EnsureLocalDrivePath(finalApprovedRoot);

        string expectedRelativePath = Path.Combine("Resources", "Strats");
        string actualRelativePath = Path.GetRelativePath(finalMacroRoot, finalApprovedRoot);
        if (!string.Equals(
                actualRelativePath,
                expectedRelativePath,
                StringComparison.OrdinalIgnoreCase)
            || !IsStrictlyContained(finalMacroRoot, finalApprovedRoot))
        {
            throw new StrategyCatalogException(StrategyCatalogError.ReparsePointRejected);
        }

        return new ApprovedStrategyRoot(
            Path.TrimEndingDirectorySeparator(approvedPath),
            Path.TrimEndingDirectorySeparator(finalApprovedRoot),
            Path.TrimEndingDirectorySeparator(finalMacroRoot));
    }

    internal static string ResolveApprovedStrategyFile(
        ApprovedStrategyRoot approvedRoot,
        string candidate)
    {
        string canonicalCandidate;
        try
        {
            if (string.IsNullOrWhiteSpace(candidate)
                || HasForbiddenPathPrefix(candidate)
                || ContainsTraversalSegment(candidate)
                || !Path.IsPathFullyQualified(candidate))
            {
                throw new StrategyCatalogException(StrategyCatalogError.UnsafeStrategyEntry);
            }

            canonicalCandidate = Path.GetFullPath(candidate);
        }
        catch (StrategyCatalogException)
        {
            throw;
        }
        catch (Exception exception) when (exception is
                                           ArgumentException
                                           or NotSupportedException
                                           or PathTooLongException)
        {
            throw new StrategyCatalogException(StrategyCatalogError.UnsafeStrategyEntry);
        }

        EnsureLocalDrivePath(canonicalCandidate);
        string? lexicalParent = Path.GetDirectoryName(canonicalCandidate);
        if (!string.Equals(
                Path.TrimEndingDirectorySeparator(lexicalParent ?? string.Empty),
                approvedRoot.CanonicalPath,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                Path.GetExtension(canonicalCandidate),
                ".strat",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new StrategyCatalogException(StrategyCatalogError.UnsafeStrategyEntry);
        }

        string finalCandidate = ResolveExistingPath(
            canonicalCandidate,
            expectDirectory: false,
            rejectFinalReparsePoint: true);
        EnsureLocalDrivePath(finalCandidate);

        string? finalParent = Path.GetDirectoryName(finalCandidate);
        if (!string.Equals(
                Path.TrimEndingDirectorySeparator(finalParent ?? string.Empty),
                approvedRoot.FinalPath,
                StringComparison.OrdinalIgnoreCase)
            || !IsStrictlyContained(approvedRoot.FinalPath, finalCandidate))
        {
            throw new StrategyCatalogException(StrategyCatalogError.ReparsePointRejected);
        }

        return finalCandidate;
    }

    internal static void EnsureApprovedRootUnchanged(ApprovedStrategyRoot approvedRoot)
    {
        EnsureExistingDirectoryWithoutReparsePoint(
            approvedRoot.CanonicalPath,
            StrategyCatalogError.StrategyRootUnavailable);
        string currentFinalPath = ResolveExistingPath(
            approvedRoot.CanonicalPath,
            expectDirectory: true,
            rejectFinalReparsePoint: true);
        if (!string.Equals(
                Path.TrimEndingDirectorySeparator(currentFinalPath),
                approvedRoot.FinalPath,
                StringComparison.OrdinalIgnoreCase)
            || !IsStrictlyContained(approvedRoot.FinalMacroRootPath, currentFinalPath))
        {
            throw new StrategyCatalogException(StrategyCatalogError.ReparsePointRejected);
        }
    }

    private static string ResolveExistingPath(
        string path,
        bool expectDirectory,
        bool rejectFinalReparsePoint)
    {
        uint flags = expectDirectory
            ? FileFlagBackupSemantics
            : FileFlagOpenReparsePoint;

        using SafeFileHandle handle = CreateFileW(
            path,
            FileReadAttributes,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            flags,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new StrategyCatalogException(StrategyCatalogError.StrategyRootUnavailable);
        }

        if (!GetFileInformationByHandle(handle, out ByHandleFileInformation information))
        {
            throw new StrategyCatalogException(StrategyCatalogError.StrategyRootUnavailable);
        }

        bool isDirectory = (information.FileAttributes & FileAttributeDirectory) != 0;
        if (isDirectory != expectDirectory)
        {
            throw new StrategyCatalogException(StrategyCatalogError.UnsafeStrategyEntry);
        }

        if (rejectFinalReparsePoint
            && (information.FileAttributes & FileAttributeReparsePoint) != 0)
        {
            throw new StrategyCatalogException(StrategyCatalogError.ReparsePointRejected);
        }

        int capacity = 512;
        while (capacity <= MaximumFinalPathCharacters)
        {
            var buffer = new char[capacity];
            uint length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
            if (length == 0)
            {
                throw new StrategyCatalogException(StrategyCatalogError.StrategyRootUnavailable);
            }

            if (length < buffer.Length)
            {
                return NormalizeFinalPath(new string(buffer, 0, checked((int)length)));
            }

            capacity = checked((int)length + 1);
        }

        throw new StrategyCatalogException(StrategyCatalogError.UnsafeStrategyEntry);
    }

    private static string NormalizeFinalPath(string finalPath)
    {
        if (finalPath.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)
            || finalPath.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase)
            || finalPath.StartsWith(@"\??\", StringComparison.OrdinalIgnoreCase))
        {
            throw new StrategyCatalogException(StrategyCatalogError.NetworkPathRejected);
        }

        string withoutExtendedPrefix = finalPath.StartsWith(@"\\?\", StringComparison.Ordinal)
            ? finalPath[4..]
            : finalPath;
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(withoutExtendedPrefix));
    }

    private static void EnsureExistingDirectoryWithoutReparsePoint(
        string path,
        StrategyCatalogError unavailableError)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) == 0)
            {
                throw new StrategyCatalogException(unavailableError);
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new StrategyCatalogException(StrategyCatalogError.ReparsePointRejected);
            }
        }
        catch (StrategyCatalogException)
        {
            throw;
        }
        catch (Exception exception) when (exception is
                                           IOException
                                           or UnauthorizedAccessException
                                           or System.Security.SecurityException
                                           or ArgumentException
                                           or NotSupportedException)
        {
            throw new StrategyCatalogException(unavailableError);
        }
    }

    private static void EnsureLocalDrivePath(string path)
    {
        if (HasForbiddenPathPrefix(path)
            || path.Length < 3
            || !char.IsAsciiLetter(path[0])
            || path[1] != ':'
            || !PathInternal.IsDirectorySeparator(path[2]))
        {
            throw new StrategyCatalogException(StrategyCatalogError.NetworkPathRejected);
        }

        string driveRoot = path[..3];
        try
        {
            DriveType type = new DriveInfo(driveRoot).DriveType;
            if (type is DriveType.Network or DriveType.NoRootDirectory or DriveType.Unknown)
            {
                throw new StrategyCatalogException(StrategyCatalogError.NetworkPathRejected);
            }
        }
        catch (StrategyCatalogException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new StrategyCatalogException(StrategyCatalogError.NetworkPathRejected);
        }
    }

    private static bool IsStrictlyContained(string root, string candidate)
    {
        string rootWithSeparator = Path.TrimEndingDirectorySeparator(root)
            + Path.DirectorySeparatorChar;
        return candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasForbiddenPathPrefix(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal)
        || path.StartsWith("//", StringComparison.Ordinal)
        || path.StartsWith(@"\??\", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsTraversalSegment(string path)
    {
        ReadOnlySpan<char> remaining = path.AsSpan();
        while (!remaining.IsEmpty)
        {
            int separator = remaining.IndexOfAny('\\', '/');
            ReadOnlySpan<char> segment = separator >= 0 ? remaining[..separator] : remaining;
            if (segment.SequenceEqual(".."))
            {
                return true;
            }

            if (separator < 0)
            {
                return false;
            }

            remaining = remaining[(separator + 1)..];
        }

        return false;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation fileInformation);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle file,
        [Out] char[] filePath,
        uint filePathLength,
        uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        internal uint FileAttributes;
        internal FILETIME CreationTime;
        internal FILETIME LastAccessTime;
        internal FILETIME LastWriteTime;
        internal uint VolumeSerialNumber;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh;
        internal uint FileIndexLow;
    }

    private static class PathInternal
    {
        internal static bool IsDirectorySeparator(char character) =>
            character is '\\' or '/';
    }
}
