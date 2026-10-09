using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Sintonia.Infrastructure.Git;

/// <summary>Recognize existing Windows directory aliases without trusting their spelling or following junctions.</summary>
internal static class GitDirectoryIdentity
{
    private const int FileIdInformationClass = 18;
    private const uint BackupSemantics = 0x02000000;
    private const uint OpenReparsePoint = 0x00200000;
    public static bool Same(string first, string second)
    {
        first = Path.TrimEndingDirectorySeparator(Path.GetFullPath(first));
        second = Path.TrimEndingDirectorySeparator(Path.GetFullPath(second));
        if (!IsDirectPath(first) || !IsDirectPath(second)) return false;
        if (string.Equals(first, second, StringComparison.OrdinalIgnoreCase)) return true;
        if (!OperatingSystem.IsWindows() || !Directory.Exists(first) || !Directory.Exists(second)) return false;

        // Packaged desktop apps may see a logical LocalAppData path while Git reports its physical path.
        // Prove both handles identify the same directory; never substitute a known package name or path prefix.
        using var firstHandle = OpenDirectory(first);
        using var secondHandle = OpenDirectory(second);
        if (firstHandle.IsInvalid || secondHandle.IsInvalid
            || !GetFileInformationByHandleEx(firstHandle, FileIdInformationClass, out var firstId, Marshal.SizeOf<FileIdInfo>())
            || !GetFileInformationByHandleEx(secondHandle, FileIdInformationClass, out var secondId, Marshal.SizeOf<FileIdInfo>())) return false;
        if (!IsDirectPath(first) || !IsDirectPath(second)) return false;
        return firstId.VolumeSerialNumber == secondId.VolumeSerialNumber
            && firstId.FileIdLow == secondId.FileIdLow && firstId.FileIdHigh == secondId.FileIdHigh;
    }

    private static SafeFileHandle OpenDirectory(string path) =>
        CreateFile(path, 0, FileShare.Read | FileShare.Write | FileShare.Delete, 0, FileMode.Open, BackupSemantics | OpenReparsePoint, 0);

    private static bool IsDirectPath(string path)
    {
        try { GitTaskWorktreeManager.CheckPath(path); return true; }
        // A different, unsupported entry in worktree list must not hide the valid checkout being sought.
        catch (InvalidOperationException) { return false; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo { public ulong VolumeSerialNumber, FileIdLow, FileIdHigh; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, FileShare share, nint securityAttributes,
        FileMode creationDisposition, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass, out FileIdInfo information, int size);
}
