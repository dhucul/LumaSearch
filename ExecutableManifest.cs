using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Xml.Linq;

namespace LumaSearch;

public static class ExecutableManifest
{
    public static string? GetExecutionLevel(string executable)
    {
        // Load resources as data only; this never runs application code or triggers elevation.
        IntPtr module = LoadLibraryExW(Path.GetFullPath(executable), IntPtr.Zero, 0x2 | 0x20);
        if (module == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            IntPtr resource = FindResourceW(module, new IntPtr(1), new IntPtr(24));
            if (resource == IntPtr.Zero) throw new IOException("The executable has no application manifest.");
            uint size = SizeofResource(module, resource);
            IntPtr data = LockResource(LoadResource(module, resource));
            if (data == IntPtr.Zero || size == 0 || size > 1024 * 1024) throw new IOException("Invalid executable manifest resource.");
            byte[] bytes = new byte[checked((int)size)];
            Marshal.Copy(data, bytes, 0, bytes.Length);
            using var stream = new MemoryStream(bytes);
            var xml = XDocument.Load(stream);
            XNamespace ns = "urn:schemas-microsoft-com:asm.v3";
            return xml.Descendants(ns + "requestedExecutionLevel").SingleOrDefault()?.Attribute("level")?.Value;
        }
        finally { FreeLibrary(module); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr LoadLibraryExW(string file, IntPtr reserved, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr FindResourceW(IntPtr module, IntPtr name, IntPtr type);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SizeofResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LoadResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LockResource(IntPtr resource);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(IntPtr module);
}
