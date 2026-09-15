using System.IO;
using Microsoft.Win32.SafeHandles;

namespace LumaSearch;

public enum DeletionMode { RecycleBin, Permanent }
// Append values: numeric statuses are also stored in journals from previous releases.
public enum DeletionStatus { Recycled, PermanentlyDeleted, AlreadyMissing, Cancelled, Failed, Unknown, Pending, NotStarted, Restored }
public sealed record ItemDeletionOutcome(SearchResult Item, DeletionStatus Status, string Message);
public sealed record BatchCounts(int Completed, int Missing, int Failed, int Cancelled, int NotAttempted, int Pending = 0)
{
    public int Total => Completed + Missing + Failed + Cancelled + NotAttempted + Pending;
}
public sealed record DeletionOutcome(DeletionStatus Status, string Message, ItemDeletionOutcome[]? Items = null, BatchCounts? Counts = null);

public static class FileDeletionService
{
    public static string GetConfirmationMessage(SearchResult item, DeletionMode mode = DeletionMode.Permanent)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        string parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(item.FullPath)) ?? item.FullPath;
        string action = mode == DeletionMode.RecycleBin ? "sent to the Recycle Bin" : "permanently deleted";
        string scope = item.IsLink ? $"Only this link will be {action}. Its target and the target's contents will stay."
            : item.IsDirectory ? $"This folder and everything inside it will be {action}.\nIts parent folder and other items beside it will stay."
            : $"Only this file will be {action}.\nIts containing folder and all other files will stay.";
        string ending = mode == DeletionMode.RecycleBin
            ? "Items can be restored to their original locations with Restore deleted items in LumaSearch before the Recycle Bin is emptied. Windows Restore returns the item to protected staging."
            : "This cannot be undone.";
        return $"{item.Type}: {item.Name}\n\nLocated in: {parent}\n\n{scope}\n\n{ending}";
    }

    public static DeletionOutcome Delete(SearchResult item, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var current = ValidateTarget(item);
        if (current is null) return Missing();
        using var pinned = PinnedPath.Open(item);
        bool removed = DeletePinnedTree(pinned.Handle, pinned.Item, token);
        if (!removed) return new(DeletionStatus.Pending,
            "Removal is not yet confirmed: " + item.Name + ". Another program may still have a file open. "
            + "Folders may be partly processed. Close open files and refresh the search before retrying.");
        return new(DeletionStatus.PermanentlyDeleted, "Permanently deleted: " + item.Name);
    }

    public static Task<DeletionOutcome> DeleteAsync(SearchResult item, DeletionMode mode, CancellationToken token = default) => mode switch
    {
        DeletionMode.RecycleBin => RecycleBinService.RecycleAsync(item, token),
        DeletionMode.Permanent => Task.Run(() => Delete(item, token), CancellationToken.None),
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    public static SearchResult? ValidateTarget(SearchResult item)
    {
        if (!Path.IsPathFullyQualified(item.FullPath)) throw new ArgumentException("A full filesystem path is required.");
        string path = Path.GetFullPath(item.FullPath);
        if (Path.TrimEndingDirectorySeparator(path).Equals(Path.TrimEndingDirectorySeparator(Path.GetPathRoot(path)!), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Drive roots and network share roots cannot be deleted.");
        SearchResult current;
        try { current = SearchResult.Capture(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        FileIdentityService.EnsureSame(item, current);
        return current;
    }

    internal static DeletionOutcome Missing() => new(DeletionStatus.AlreadyMissing,
        "The original item is already missing. No deletion or recycling was performed.");

    private sealed class Frame(SafeFileHandle handle, SearchResult item, bool ownsHandle) : IDisposable
    {
        internal SafeFileHandle Handle { get; } = handle;
        internal SearchResult Item { get; } = item;
        internal IEnumerator<string>? Children { get; set; }
        public void Dispose() { Children?.Dispose(); if (ownsHandle) Handle.Dispose(); }
    }

    private static bool DeletePinnedTree(SafeFileHandle rootHandle, SearchResult root, CancellationToken token)
    {
        var stack = new Stack<Frame>();
        stack.Push(new Frame(rootHandle, root, false));
        try
        {
            while (stack.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var frame = stack.Peek();
                if (frame.Item.IsDirectory && !frame.Item.IsLink)
                {
                    frame.Children ??= Directory.EnumerateFileSystemEntries(frame.Item.FullPath, "*",
                        new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false }).GetEnumerator();
                    if (frame.Children.MoveNext())
                    {
                        string path = frame.Children.Current;
                        SafeFileHandle handle;
                        try { handle = FileIdentityService.Open(path, deleteAccess: true, pin: true, denyWrite: true); }
                        catch (FileNotFoundException) { continue; }
                        catch (DirectoryNotFoundException) { continue; }
                        try { stack.Push(new Frame(handle, FileIdentityService.Read(handle, path), true)); }
                        catch { handle.Dispose(); throw; }
                        continue;
                    }
                    frame.Children.Dispose();
                    frame.Children = null;
                }
                token.ThrowIfCancellationRequested();
                FileIdentityService.MarkDeleted(frame.Handle, frame.Item.FullPath);
                stack.Pop().Dispose();
                // Closing our handle commits the disposition, but other readers can keep
                // the name present. Never promote that state to confirmed removal.
                frame.Handle.Dispose();
                if (!IsOriginalRemoved(frame.Item)) return false;
            }
            return true;
        }
        finally { while (stack.Count > 0) stack.Pop().Dispose(); }
    }

    private static bool IsOriginalRemoved(SearchResult item)
    {
        try { return SearchResult.Capture(item.FullPath).Identity != item.Identity; }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
        catch (Exception ex) when (FileSearchService.IsFileSystemException(ex)) { return false; }
    }

    public static bool IsSameOrDescendant(string candidate, SearchResult deleted) =>
        candidate.Equals(deleted.FullPath, StringComparison.Ordinal) ||
        (deleted.IsDirectory && !deleted.IsLink && candidate.StartsWith(
            Path.TrimEndingDirectorySeparator(deleted.FullPath) + Path.DirectorySeparatorChar, StringComparison.Ordinal));

    public static bool IsDefinitelyMissing(string path)
    {
        try { _ = File.GetAttributes(path); return false; }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
        catch (Exception ex) when (FileSearchService.IsFileSystemException(ex)) { return false; }
    }

    public static SearchResult[] Reconcile(SearchResult[] results, SearchResult target, bool succeeded) =>
        results.Where(result => !IsSameOrDescendant(result.FullPath, target) ||
            (!succeeded && !IsDefinitelyMissing(result.FullPath))).ToArray();
}
