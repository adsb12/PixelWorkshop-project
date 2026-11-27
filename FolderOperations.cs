using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PixelWorkshop;

internal readonly record struct FolderIdentity(uint Volume, ulong Index, ulong Created);

internal static class FolderOperations
{
    private const int FileNotFound = 2;
    private const int PathNotFound = 3;
    private const int DirectoryNotEmpty = 145;
    private const int AlreadyExists = 183;
    private const uint DeleteAccess = 0x00010000;
    private const uint ShareReadWrite = 3;
    private const uint OpenExisting = 3;
    private const uint OpenDirectoryWithoutFollowingLinks = 0x02200000;
    private const uint DirectoryAttribute = 0x10;
    private const uint ReparsePoint = 0x400;

    // Move an opened staging folder into place, holding its identity across the rename.
    internal static FolderIdentity? TryCreate(string path)
    {
        if (Directory.Exists(path))
            return null;

        var staging = Path.Combine(Path.GetDirectoryName(path)!, $".PixelWorkshop-{Guid.NewGuid():N}");
        if (!CreateDirectoryW(staging, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        try
        {
            using var handle = Open(staging, 0, shareDelete: true);
            var identity = Identity(handle);
            if (MoveFileW(staging, path))
                return identity;

            var error = Marshal.GetLastWin32Error();
            if (error is AlreadyExists or 80)
                return null;
            throw new Win32Exception(error);
        }
        finally
        {
            RemoveDirectoryW(staging);
        }
    }

    internal static string Clean(string path, FolderIdentity expected)
    {
        // Deny delete sharing while checking and deleting so the path cannot
        // be swapped out between the identity check and the delete.
        using var handle = Open(path, DeleteAccess, shareDelete: false);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            if (error is FileNotFound or PathNotFound)
                return "오늘 폴더가 이미 없어졌어요.";
            throw new Win32Exception(error);
        }
        if (Identity(handle) != expected)
            return "다른 폴더로 바뀌어서 그대로 뒀어요.";

        // FileDispositionInfo only removes this directory if it is empty.
        byte delete = 1;
        if (SetFileInformationByHandle(handle, 4, ref delete, 1))
            return "빈 폴더를 정리했어요!";

        var deleteError = Marshal.GetLastWin32Error();
        if (deleteError == DirectoryNotEmpty)
            return "안에 항목이 있어서 그대로 뒀어요.";
        throw new Win32Exception(deleteError);
    }

    private static SafeFileHandle Open(string path, uint access, bool shareDelete) =>
        CreateFileW(path, access, ShareReadWrite | (shareDelete ? 4u : 0u),
            IntPtr.Zero, OpenExisting, OpenDirectoryWithoutFollowingLinks, IntPtr.Zero);

    private static FolderIdentity? Identity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if ((info.Attributes & (DirectoryAttribute | ReparsePoint)) != DirectoryAttribute)
            return null;
        return new FolderIdentity(info.Volume, ((ulong)info.IndexHigh << 32) | info.IndexLow,
            ((ulong)info.CreatedHigh << 32) | info.CreatedLow);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        internal uint Attributes;
        internal uint CreatedLow, CreatedHigh;
        internal uint AccessedLow, AccessedHigh;
        internal uint WrittenLow, WrittenHigh;
        internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool CreateDirectoryW(string path, IntPtr attributes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool MoveFileW(string existing, string destination);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool RemoveDirectoryW(string path);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share,
        IntPtr attributes, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation info);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass,
        ref byte info, uint size);
}
