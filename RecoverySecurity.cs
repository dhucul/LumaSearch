using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LumaSearch;

internal static class RecoverySecurity
{
    internal static string UserSid => WindowsIdentity.GetCurrent().User!.Value;
    internal static bool IsAdministrator => new WindowsPrincipal(WindowsIdentity.GetCurrent())
        .IsInRole(WindowsBuiltInRole.Administrator);
    private const uint SecurityParts = 1 | 4 | 16; // owner, DACL, mandatory integrity label

    internal static byte[] Capture(SafeFileHandle handle)
    {
        uint error = GetSecurityInfo(handle, 1, SecurityParts, out _, out _, out _, out _, out IntPtr descriptor);
        if (error != 0) throw new Win32Exception((int)error);
        try
        {
            byte[] bytes = new byte[GetSecurityDescriptorLength(descriptor)];
            Marshal.Copy(descriptor, bytes, 0, bytes.Length);
            return bytes;
        }
        finally { LocalFree(descriptor); }
    }

    internal static byte[] ProtectedDescriptor(bool testOnly, bool directory)
    {
        string owner = testOnly ? UserSid : "BA";
        string inheritance = directory ? "OICI" : "";
        string sddl = $"O:{owner}D:P(A;{inheritance};FA;;;SY)(A;{inheritance};FA;;;{owner})"
            + (testOnly ? "" : "S:(ML;;NW;;;HI)");
        var descriptor = new RawSecurityDescriptor(sddl);
        byte[] bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);
        return bytes;
    }

    internal static void ProtectItem(SafeFileHandle handle, bool testOnly)
    {
        var label = new RawSecurityDescriptor(testOnly ? "S:(ML;;NW;;;ME)" : "S:(ML;;NW;;;HI)");
        byte[] bytes = new byte[label.BinaryLength];
        label.GetBinaryForm(bytes, 0);
        RestoreLabel(handle, bytes);
    }

    internal static void RestoreLabel(SafeFileHandle handle, byte[] bytes)
    {
        var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            IntPtr pointer = pinned.AddrOfPinnedObject();
            if (!GetSecurityDescriptorSacl(pointer, out _, out IntPtr sacl, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            // Only the root object's non-inheritable integrity label changes. Its owner,
            // DACL and descendants' permissions remain untouched throughout recovery.
            uint error = SetSecurityInfo(handle, 1, 16, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, sacl);
            if (error != 0) throw new Win32Exception((int)error);
        }
        finally { pinned.Free(); }
    }

    internal static SafeFileHandle OpenDirectory(string path, bool create, bool testOnly)
    {
        if (!testOnly && !IsAdministrator) throw new UnauthorizedAccessException("Protected recovery requires running LumaSearch as administrator.");
        if (create)
        {
            byte[] bytes = ProtectedDescriptor(testOnly, directory: true);
            var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = pinned.AddrOfPinnedObject() };
                if (!CreateDirectoryW(path, ref security) && Marshal.GetLastWin32Error() != 183)
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally { pinned.Free(); }
        }
        var handle = FileIdentityService.Open(path, pin: true, denyWrite: true, securityAccess: true);
        try
        {
            var item = FileIdentityService.Read(handle, path);
            if (!item.IsDirectory || item.IsLink) throw new IOException("Recovery storage cannot be a link.");
            var descriptor = new RawSecurityDescriptor(Capture(handle), 0);
            var trusted = new HashSet<string>(StringComparer.Ordinal) { "S-1-5-18", testOnly ? UserSid : "S-1-5-32-544" };
            if (descriptor.Owner is null || !trusted.Contains(descriptor.Owner.Value) || descriptor.DiscretionaryAcl is null ||
                !descriptor.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected) ||
                descriptor.DiscretionaryAcl.Cast<GenericAce>().Any(ace => ace is not CommonAce common ||
                    (common.AceQualifier == AceQualifier.AccessAllowed && !trusted.Contains(common.SecurityIdentifier.Value))))
                throw new UnauthorizedAccessException("Recovery storage does not have the required protected permissions.");
            if (create) File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden | FileAttributes.System);
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    internal static void Rename(SafeFileHandle source, SafeFileHandle destinationDirectory, string name)
    {
        if (string.IsNullOrEmpty(name) || name is "." or ".." || name.IndexOfAny(['\\', '/', ':', '\0']) >= 0)
            throw new ArgumentException("A single destination name is required.");
        var destinationPath = new StringBuilder(32768);
        uint pathLength = GetFinalPathNameByHandleW(destinationDirectory, destinationPath, (uint)destinationPath.Capacity, 0);
        if (pathLength == 0 || pathLength >= destinationPath.Capacity) throw new Win32Exception(Marshal.GetLastWin32Error());
        byte[] encoded = Encoding.Unicode.GetBytes(Path.Combine(destinationPath.ToString(), name) + "\0");
        int rootOffset = IntPtr.Size == 8 ? 8 : 4;
        int lengthOffset = rootOffset + IntPtr.Size;
        int nameOffset = lengthOffset + 4;
        int size = nameOffset + encoded.Length;
        IntPtr buffer = Marshal.AllocHGlobal(size);
        bool held = false;
        try
        {
            destinationDirectory.DangerousAddRef(ref held);
            Marshal.Copy(new byte[size], 0, buffer, size);
            // ReplaceIfExists remains FALSE: the kernel rejects collisions atomically.
            // The destination directory and its ancestors stay pinned. Use the handle's
            // canonical absolute path because Win32 providers may reject RootDirectory.
            Marshal.WriteInt32(buffer, lengthOffset, encoded.Length - 2);
            Marshal.Copy(encoded, 0, buffer + nameOffset, encoded.Length);
            if (!SetFileInformationByHandle(source, 3, buffer, (uint)size))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            if (held) destinationDirectory.DangerousRelease();
            Marshal.FreeHGlobal(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { public int Length; public IntPtr Descriptor; public int Inherit; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryW(string path, ref SecurityAttributes security);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint GetSecurityInfo(SafeFileHandle handle, int type, uint parts, out IntPtr owner, out IntPtr group,
        out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint SetSecurityInfo(SafeFileHandle handle, int type, uint parts, IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);
    [DllImport("advapi32.dll")] private static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSecurityDescriptorSacl(IntPtr descriptor, [MarshalAs(UnmanagedType.Bool)] out bool present, out IntPtr sacl, [MarshalAs(UnmanagedType.Bool)] out bool defaulted);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, IntPtr buffer, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint size, uint flags);
}
