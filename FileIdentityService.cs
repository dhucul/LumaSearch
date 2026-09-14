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

    internal static SafeFileHandle Open(string path, bool deleteAccess = false, bool pin = false)
    {
        var handle = CreateFileW(path, 0x80u | (deleteAccess ? 0x10000u : 0),
            pin ? 3u : 7u, IntPtr.Zero, 3, 0x02000000 | 0x00200000 | 0x01000000, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        int error = Marshal.GetLastWin32Error();
        handle.Dispose();
        ThrowFileError(error, path);
        throw new IOException("Cannot open filesystem item.");
    }

    internal static SearchResult Read(SafeFileHandle handle, string path)
    {
        if (!GetFileInformationByHandle(handle, out var info)) ThrowFileError(Marshal.GetLastWin32Error(), path);
        if (!GetFileInformationByHandleEx(handle, 18, out var id, 24)) ThrowFileError(Marshal.GetLastWin32Error(), path);
        var identity = new FileIdentity(id.Volume, id.Low, id.High,
            unchecked((long)(((ulong)info.CreationHigh << 32) | info.CreationLow)));
        return new SearchResult(Path.GetFileName(Path.TrimEndingDirectorySeparator(path)), path,
            (info.Attributes & 0x10) != 0, (info.Attributes & 0x400) != 0, identity);
    }

    internal static void EnsureSame(SearchResult expected, SearchResult actual)
    {
        if (expected.Identity is null) throw new IOException("This result has no verified file identity. Search again before deleting it.");
        if (expected.Identity != actual.Identity || expected.IsDirectory != actual.IsDirectory || expected.IsLink != actual.IsLink)
            throw new IOException("This item has been replaced or changed since the search. Nothing was deleted. Search again.");
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

    internal static PinnedPath Open(SearchResult expected)
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
                var handle = FileIdentityService.Open(path, pin: true);
                pinned._parents.Add(handle);
                if (FileIdentityService.Read(handle, path).IsLink)
                    throw new IOException("Deletion through a linked parent directory is not supported. Search the target's physical location instead.");
            }
            pinned.Handle = FileIdentityService.Open(expected.FullPath, deleteAccess: true, pin: true);
            pinned.Item = FileIdentityService.Read(pinned.Handle, expected.FullPath);
            FileIdentityService.EnsureSame(expected, pinned.Item);
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
