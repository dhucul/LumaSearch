using System.IO;
using System.Runtime.InteropServices;

namespace LumaSearch;

public static class RecycleBinService
{
    public static Task<DeletionOutcome> RecycleAsync(SearchResult item, CancellationToken token = default)
    {
        var completion = new TaskCompletionSource<DeletionOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.SetResult(Recycle(item, token)); }
            catch (Exception ex) { completion.SetException(ex); }
        }) { IsBackground = true, Name = "LumaSearch recycling" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static DeletionOutcome Recycle(SearchResult item, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var target = FileDeletionService.ValidateTarget(item);
        if (target is null) return FileDeletionService.Missing();
        Marshal.ThrowExceptionForHR(CoInitializeEx(IntPtr.Zero, 2));
        object? operationObject = null;
        IShellItem? shellItem = null;
        try
        {
            operationObject = Activator.CreateInstance(Type.GetTypeFromCLSID(
                new Guid("3AD05575-8857-4850-9277-11B85BDB8E09"), throwOnError: true)!)!;
            var operation = (IFileOperation)operationObject;
            // Recycle, preserve undo, avoid linked HTML companion files, and fail on errors.
            // A permanent-delete warning is never silently accepted by this operation.
            const uint flags = 0x00080000 | 0x20000000 | 0x2000 | 0x00100000 |
                0x0400 | 0x4000 | 0x0010 | 0x0004;
            Marshal.ThrowExceptionForHR(operation.SetOperationFlags(flags));
            Guid shellItemId = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");
            token.ThrowIfCancellationRequested();
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(target.FullPath,
                IntPtr.Zero, ref shellItemId, out shellItem));
            var sink = new RecycleProgressSink(item, token);
            Marshal.ThrowExceptionForHR(operation.DeleteItem(shellItem, sink));
            int result = operation.PerformOperations();
            Marshal.ThrowExceptionForHR(operation.GetAnyOperationsAborted(out bool aborted));
            GC.KeepAlive(sink);
            if (sink.RefusedPermanentDelete)
                throw new IOException("Windows cannot recycle this item. Choose Delete permanently only if you want to remove it without recovery.");
            if (sink.UnconfirmedDeletion)
                throw new IOException("Windows did not confirm that every item reached the Recycle Bin. Refresh the location; recoverability is not confirmed.");
            if (sink.ValidationError is not null) throw new IOException(sink.ValidationError);
            if (!sink.Recycled) token.ThrowIfCancellationRequested();
            Marshal.ThrowExceptionForHR(result);
            if (sink.Failure < 0) Marshal.ThrowExceptionForHR(sink.Failure);
            if (aborted || !sink.Recycled)
                throw new IOException("The item was not sent to the Recycle Bin. It may be locked or recycling may be unavailable for this location.");
            return new(DeletionStatus.Recycled, "Sent to Recycle Bin: " + item.Name);
        }
        finally
        {
            if (shellItem is not null) Marshal.FinalReleaseComObject(shellItem);
            if (operationObject is not null) Marshal.FinalReleaseComObject(operationObject);
            CoUninitialize();
        }
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(IntPtr reserved, uint apartment);
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext,
        ref Guid interfaceId, out IShellItem item);
}

[ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItem
{
    [PreserveSig] int BindToHandler(IntPtr context, ref Guid handler, ref Guid interfaceId, out IntPtr result);
    [PreserveSig] int GetParent(out IShellItem parent);
    [PreserveSig] int GetDisplayName(uint type, out IntPtr name);
    [PreserveSig] int GetAttributes(uint mask, out uint attributes);
    [PreserveSig] int Compare(IShellItem other, uint hint, out int order);
}

// The method order is the native IFileOperation vtable order, including unused slots.
[ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFileOperation
{
    [PreserveSig] int Advise(IntPtr sink, out uint cookie);
    [PreserveSig] int Unadvise(uint cookie);
    [PreserveSig] int SetOperationFlags(uint flags);
    [PreserveSig] int SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
    [PreserveSig] int SetProgressDialog(IntPtr dialog);
    [PreserveSig] int SetProperties(IntPtr properties);
    [PreserveSig] int SetOwnerWindow(IntPtr window);
    [PreserveSig] int ApplyPropertiesToItem(IntPtr item);
    [PreserveSig] int ApplyPropertiesToItems(IntPtr items);
    [PreserveSig] int RenameItem(IntPtr item, IntPtr name, IntPtr sink);
    [PreserveSig] int RenameItems(IntPtr items, IntPtr name);
    [PreserveSig] int MoveItem(IntPtr item, IntPtr destination, IntPtr name, IntPtr sink);
    [PreserveSig] int MoveItems(IntPtr items, IntPtr destination);
    [PreserveSig] int CopyItem(IntPtr item, IntPtr destination, IntPtr name, IntPtr sink);
    [PreserveSig] int CopyItems(IntPtr items, IntPtr destination);
    [PreserveSig] int DeleteItem(IShellItem item, IFileOperationProgressSink sink);
    [PreserveSig] int DeleteItems(IntPtr items);
    [PreserveSig] int NewItem(IntPtr destination, uint attributes, IntPtr name, IntPtr template, IntPtr sink);
    [PreserveSig] int PerformOperations();
    [PreserveSig] int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
}

[ComVisible(true), Guid("04B0F1A7-9490-44BC-96E1-4296A31252E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IFileOperationProgressSink
{
    [PreserveSig] int StartOperations();
    [PreserveSig] int FinishOperations(int result);
    [PreserveSig] int PreRenameItem(uint flags, IntPtr item, IntPtr name);
    [PreserveSig] int PostRenameItem(uint flags, IntPtr item, IntPtr name, int result, IntPtr created);
    [PreserveSig] int PreMoveItem(uint flags, IntPtr item, IntPtr destination, IntPtr name);
    [PreserveSig] int PostMoveItem(uint flags, IntPtr item, IntPtr destination, IntPtr name, int result, IntPtr created);
    [PreserveSig] int PreCopyItem(uint flags, IntPtr item, IntPtr destination, IntPtr name);
    [PreserveSig] int PostCopyItem(uint flags, IntPtr item, IntPtr destination, IntPtr name, int result, IntPtr created);
    [PreserveSig] int PreDeleteItem(uint flags, IntPtr item);
    [PreserveSig] int PostDeleteItem(uint flags, IntPtr item, int result, IntPtr created);
    [PreserveSig] int PreNewItem(uint flags, IntPtr destination, IntPtr name);
    [PreserveSig] int PostNewItem(uint flags, IntPtr destination, IntPtr name, IntPtr template, uint attributes, int result, IntPtr created);
    [PreserveSig] int UpdateProgress(uint total, uint completed);
    [PreserveSig] int ResetTimer();
    [PreserveSig] int PauseTimer();
    [PreserveSig] int ResumeTimer();
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class RecycleProgressSink : IFileOperationProgressSink
{
    private readonly SearchResult? _expected;
    private readonly CancellationToken _token;
    private string? _rootPath;
    public RecycleProgressSink(SearchResult? expected = null, CancellationToken token = default)
    { _expected = expected; _token = token; }
    public bool RefusedPermanentDelete { get; private set; }
    public bool UnconfirmedDeletion { get; private set; }
    public string? ValidationError { get; private set; }
    public bool Recycled { get; private set; }
    public int Failure { get; private set; }
    public int PreDeleteItem(uint flags, IntPtr item)
    {
        if (_token.IsCancellationRequested) return unchecked((int)0x800704C7);
        if ((flags & 0x80) == 0)
        { RefusedPermanentDelete = true; return unchecked((int)0x80004004); }
        try
        {
            if (_expected is not null)
            {
                string path = GetPath(item);
                var actual = SearchResult.Capture(path);
                if (_rootPath is null || SamePath(path, _rootPath))
                {
                    // Compare filesystem identity even if Explorer normalizes the drive-letter casing.
                    FileIdentityService.EnsureSame(_expected, actual);
                    _rootPath = path;
                }
                else if (!_expected.IsDirectory || _expected.IsLink ||
                    !path.StartsWith(Path.TrimEndingDirectorySeparator(_rootPath) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    throw new IOException("Windows attempted to operate outside the verified selected item.");
            }
            return 0;
        }
        catch (Exception ex) { ValidationError = ex.Message; return unchecked((int)0x80004004); }
    }
    public int PostDeleteItem(uint flags, IntPtr item, int result, IntPtr created)
    {
        if (result < 0) { Failure = result; return result; }
        if (created == IntPtr.Zero)
        { UnconfirmedDeletion = true; Failure = unchecked((int)0x80004005); return Failure; }
        try
        {
            if (_expected is null || (_rootPath is not null && SamePath(GetPath(item), _rootPath))) Recycled = true;
            return 0;
        }
        catch (Exception ex) { ValidationError = ex.Message; return unchecked((int)0x80004004); }
    }
    private static bool SamePath(string left, string right) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(left))
        .Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.Ordinal);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDisplayNameDelegate(IntPtr item, uint mode, out IntPtr name);
    private static string GetPath(IntPtr item)
    {
        if (item == IntPtr.Zero) throw new IOException("Windows did not identify the operation's target.");
        IntPtr method = Marshal.ReadIntPtr(Marshal.ReadIntPtr(item), 5 * IntPtr.Size);
        var getName = Marshal.GetDelegateForFunctionPointer<GetDisplayNameDelegate>(method);
        Marshal.ThrowExceptionForHR(getName(item, 0x80058000, out IntPtr name));
        try { return Marshal.PtrToStringUni(name) ?? throw new IOException("Windows returned an empty target path."); }
        finally { Marshal.FreeCoTaskMem(name); }
    }
    public int StartOperations() => _token.IsCancellationRequested ? unchecked((int)0x800704C7) : 0;
    public int FinishOperations(int result) { if (result < 0) Failure = result; return 0; }
    public int PreRenameItem(uint flags, IntPtr item, IntPtr name) => 0;
    public int PostRenameItem(uint flags, IntPtr item, IntPtr name, int result, IntPtr created) => 0;
    public int PreMoveItem(uint flags, IntPtr item, IntPtr destination, IntPtr name) => 0;
    public int PostMoveItem(uint flags, IntPtr item, IntPtr destination, IntPtr name, int result, IntPtr created) => 0;
    public int PreCopyItem(uint flags, IntPtr item, IntPtr destination, IntPtr name) => 0;
    public int PostCopyItem(uint flags, IntPtr item, IntPtr destination, IntPtr name, int result, IntPtr created) => 0;
    public int PreNewItem(uint flags, IntPtr destination, IntPtr name) => 0;
    public int PostNewItem(uint flags, IntPtr destination, IntPtr name, IntPtr template, uint attributes, int result, IntPtr created) => 0;
    public int UpdateProgress(uint total, uint completed) => _token.IsCancellationRequested ? unchecked((int)0x800704C7) : 0;
    public int ResetTimer() => 0;
    public int PauseTimer() => 0;
    public int ResumeTimer() => 0;
}
