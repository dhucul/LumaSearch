using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LumaSearch;

public static class FileIdentityService
{
    public static SearchResult Capture(string path)
    {
        path = Path.GetFullPath(path);
        using var handle = Open(path);
        return Read(handle, path);
    }

    internal static SearchResult CaptureForSearch(string path)
    {
        path = Path.GetFullPath(path);
        using var handle = Open(path);
        return Read(handle, path, requireIdentity: false);
    }

    internal static SafeFileHandle Open(string path, bool deleteAccess = false, bool pin = false,
        bool readData = false, bool denyWrite = false, bool securityAccess = false)
    {
        var handle = CreateFileW(path, 0x80u | (deleteAccess ? 0x10000u : 0) | (readData ? 0x80000000u : 0) | (securityAccess ? 0xE0000u : 0),
            pin ? (denyWrite ? 1u : 3u) : 7u, IntPtr.Zero, 3,
            0x02000000u | 0x00200000u | 0x01000000u | (readData ? 0x40000000u : 0), IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        int error = Marshal.GetLastWin32Error();
        handle.Dispose();
        ThrowFileError(error, path);
        throw new IOException("Cannot open filesystem item.");
    }

    internal static SearchResult Read(SafeFileHandle handle, string path, bool requireIdentity = true)
    {
        if (!GetFileInformationByHandle(handle, out var info)) ThrowFileError(Marshal.GetLastWin32Error(), path);
        bool hasIdentity = GetFileInformationByHandleEx(handle, 18, out var id, 24);
        if (!hasIdentity && requireIdentity) ThrowFileError(Marshal.GetLastWin32Error(), path);
        FileIdentity? identity = hasIdentity ? new FileIdentity(id.Volume, id.Low, id.High,
            unchecked((long)(((ulong)info.CreationHigh << 32) | info.CreationLow))) : null;
        return new SearchResult(Path.GetFileName(Path.TrimEndingDirectorySeparator(path)), path,
            (info.Attributes & 0x10) != 0, (info.Attributes & 0x400) != 0, identity,
            ReadTimestamp(info.WriteHigh, info.WriteLow));
    }

    private static DateTime? ReadTimestamp(uint high, uint low)
    {
        try { return DateTime.FromFileTimeUtc(unchecked((long)(((ulong)high << 32) | low))); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    internal static void EnsureSame(SearchResult expected, SearchResult actual)
    {
        if (expected.Identity is null) throw new IOException("This result has no verified file identity. Search again before deleting it.");
        if (expected.Identity != actual.Identity || expected.IsDirectory != actual.IsDirectory || expected.IsLink != actual.IsLink)
            throw new IOException("This item has been replaced or changed since the search. Nothing was deleted. Search again.");
    }

    internal static uint LinkCount(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info)) ThrowFileError(Marshal.GetLastWin32Error(), "Selected item");
        return info.Links;
    }

    internal static void MarkDeleted(SafeFileHandle handle, string path)
    {
        byte remove = 1;
        if (!SetFileInformationByHandle(handle, 4, ref remove, 1)) ThrowFileError(Marshal.GetLastWin32Error(), path);
    }

    private static void ThrowFileError(int error, string path)
    {
        if (error == 2) throw new FileNotFoundException("The item no longer exists.", path);
        if (error == 3) throw new DirectoryNotFoundException("The containing directory no longer exists: " + path);
        if (error == 5) throw new UnauthorizedAccessException("Access denied: " + path);
        throw new IOException(new Win32Exception(error).Message + ": " + path, unchecked((int)(0x80070000u | (uint)error)));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IdInfo { public ulong Volume, Low, High; }
    [StructLayout(LayoutKind.Sequential)]
    private struct HandleInfo
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out HandleInfo info);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out IdInfo info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, ref byte info, uint size);
}

internal sealed class PinnedPath : IDisposable
{
    private readonly List<SafeFileHandle> _parents = [];
    internal SafeFileHandle Handle { get; private set; } = null!;
    internal SearchResult Item { get; private set; } = null!;

    internal static PinnedPath Open(SearchResult expected, bool deleteAccess = true, bool denyWrite = true, bool securityAccess = false,
        bool requireIdentity = true)
    {
        var pinned = new PinnedPath();
        try
        {
            var ancestors = new Stack<string>();
            string? parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(expected.FullPath));
            while (!string.IsNullOrEmpty(parent))
            {
                ancestors.Push(parent);
                parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(parent));
            }
            foreach (string path in ancestors)
            {
                var handle = FileIdentityService.Open(path, pin: true, denyWrite: true);
                pinned._parents.Add(handle);
                if (FileIdentityService.Read(handle, path, requireIdentity: false).IsLink)
                    throw new IOException("Operations through a linked parent directory are not supported. Choose the target's physical location instead.");
            }
            pinned.Handle = FileIdentityService.Open(expected.FullPath, deleteAccess: deleteAccess, pin: true, denyWrite: denyWrite, securityAccess: securityAccess);
            pinned.Item = FileIdentityService.Read(pinned.Handle, expected.FullPath, requireIdentity);
            if (requireIdentity || expected.Identity is not null) FileIdentityService.EnsureSame(expected, pinned.Item);
            else if (expected.IsDirectory != pinned.Item.IsDirectory || expected.IsLink != pinned.Item.IsLink)
                throw new IOException("The item's type changed while opening it.");
            return pinned;
        }
        catch { pinned.Dispose(); throw; }
    }

    public void Dispose()
    {
        Handle?.Dispose();
        for (int i = _parents.Count - 1; i >= 0; i--) _parents[i].Dispose();
    }
}
