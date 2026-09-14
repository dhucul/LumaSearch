using System.IO;
using System.Runtime.InteropServices;

namespace LumaSearch;

public static class ExplorerService
{
    public static Task ShowAsync(SearchResult item, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                string path = GetExistingPath(item);
                Marshal.ThrowExceptionForHR(CoInitializeEx(IntPtr.Zero, 2)); // COINIT_APARTMENTTHREADED
                try
                {
                    IntPtr itemId = IntPtr.Zero;
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        Marshal.ThrowExceptionForHR(SHParseDisplayName(path, IntPtr.Zero, out itemId, 0, out _));
                        if (itemId == IntPtr.Zero) throw new IOException("Windows could not locate this item in Explorer.");
                        // With a zero item count, Windows opens the parent and selects this exact item.
                        // Passing a shell item avoids command-line quoting and never launches the file.
                        token.ThrowIfCancellationRequested();
                        Marshal.ThrowExceptionForHR(SHOpenFolderAndSelectItems(itemId, 0, IntPtr.Zero, 0));
                    }
                    finally { if (itemId != IntPtr.Zero) Marshal.FreeCoTaskMem(itemId); }
                }
                finally { CoUninitialize(); }
                completion.SetResult();
            }
            catch (Exception ex) { completion.SetException(ex); }
        }) { IsBackground = true, Name = "LumaSearch Explorer navigation" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    public static string GetExistingPath(SearchResult item)
    {
        if (!Path.IsPathFullyQualified(item.FullPath))
            throw new ArgumentException("The selected item must have a full filesystem path.");
        string path = Path.GetFullPath(item.FullPath);
        FileAttributes attributes = File.GetAttributes(path);
        if (((attributes & FileAttributes.Directory) != 0) != item.IsDirectory)
            throw new IOException("The selected item's type has changed. Search again to refresh it.");
        return path;
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(IntPtr reserved, uint apartment);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHParseDisplayName(string name, IntPtr bindContext,
        out IntPtr itemId, uint requestedAttributes, out uint attributes);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHOpenFolderAndSelectItems(IntPtr itemId, uint count, IntPtr children, uint flags);
}
