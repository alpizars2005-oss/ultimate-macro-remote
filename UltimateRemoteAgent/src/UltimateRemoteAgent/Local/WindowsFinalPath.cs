using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace UltimateRemoteAgent.Local;

internal static class WindowsFinalPath
{
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileNameNormalized = 0;
    private const uint VolumeNameDos = 0;

    internal static string ResolveExistingFile(string path) => Resolve(path, directory: false);

    internal static string ResolveExistingDirectory(string path) => Resolve(path, directory: true);

    private static string Resolve(string path, bool directory)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        string fullPath = Path.GetFullPath(path);
        using SafeFileHandle handle = CreateFile(
            fullPath,
            desiredAccess: 0,
            FileShare.Read | FileShare.Write | FileShare.Delete,
            IntPtr.Zero,
            OpenExisting,
            directory ? FileFlagBackupSemantics : 0,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new IOException("Unable to resolve a local path.", new Win32Exception(Marshal.GetLastWin32Error()));
        }

        string? finalPath = null;
        int capacity = 512;
        while (capacity <= 32768)
        {
            var buffer = new char[capacity];
            uint written = GetFinalPathNameByHandle(
                handle,
                buffer,
                (uint)buffer.Length,
                FileNameNormalized | VolumeNameDos);
            if (written == 0)
            {
                throw new IOException("Unable to resolve a local path.", new Win32Exception(Marshal.GetLastWin32Error()));
            }

            if (written < buffer.Length)
            {
                finalPath = new string(buffer, 0, checked((int)written));
                break;
            }

            capacity = checked((int)written + 1);
        }

        if (finalPath is null)
        {
            throw new IOException("Unable to resolve a local path.");
        }
        if (finalPath.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + finalPath[8..];
        }

        return finalPath.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)
            ? finalPath[4..]
            : finalPath;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        [Out] char[] filePath,
        uint filePathLength,
        uint flags);
}
