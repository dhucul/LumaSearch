using System.ComponentModel;
using System.IO;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace LumaSearch;

public enum RecoveryState { Prepared, Staged, Recycled, Restored, Unavailable }
public sealed record RecoveryRecord(SearchResult Original, string StagedPath, string SecurityDescriptor,
    RecoveryState State, DateTime CreatedUtc, string? RecycledPath = null, string? Detail = null);
public sealed record RecoveryEntry(string RecordPath, RecoveryRecord Record)
{
    public string Name => Record.Original.Name;
    public string OriginalPath => Record.Original.FullPath;
    public string Status => Record.State switch
    {
        RecoveryState.Prepared => "Interrupted preparation",
        RecoveryState.Staged => "Ready to restore",
        RecoveryState.Recycled => "In Recycle Bin or restored to staging",
        RecoveryState.Restored => "Restored",
        _ => Record.Detail ?? "Unavailable"
    };
    public DateTime? DateRecorded => Record.CreatedUtc == DateTime.MinValue ? null : Record.CreatedUtc.ToLocalTime();
}

internal sealed class RecoveryStorage
{
    private readonly string? _testRoot;
    internal RecoveryStorage(string? testRoot = null) { _testRoot = testRoot; }
    private bool TestOnly => _testRoot is not null;
    private const string FolderName = "$LumaSearchRecovery";

    private string RootFor(string originalPath)
    {
        string volume = Path.GetPathRoot(Path.GetFullPath(originalPath))!;
        if (volume.StartsWith("\\\\", StringComparison.Ordinal))
            throw new IOException("Protected recycling requires a local drive. Network locations are not supported.");
        return _testRoot ?? Path.Combine(volume, FolderName);
    }

    internal async Task<DeletionOutcome> RecycleAsync(SearchResult original, CancellationToken token,
        Action<RecoveryEntry>? beforeNative = null)
    {
        token.ThrowIfCancellationRequested();
        if (FileDeletionService.ValidateTarget(original) is null) return FileDeletionService.Missing();
        using var lease = await Task.Run(() => Stage(original, token), CancellationToken.None).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            beforeNative?.Invoke(lease.Entry);
            var staged = original with { FullPath = lease.Entry.Record.StagedPath };
            var outcome = await RecycleBinService.RecycleStagedAsync(staged, token, recycledPath =>
            {
                lease.Entry = lease.Entry with { Record = lease.Entry.Record with { State = RecoveryState.Recycled, RecycledPath = recycledPath } };
                WriteRecord(lease.Entry);
            }).ConfigureAwait(false);
            return outcome with { Message = outcome.Message + ". Use Restore deleted items in LumaSearch to restore its original location." };
        }
        catch (OperationCanceledException)
        { return new(DeletionStatus.Cancelled, "Recycling stopped. " + original.Name + " remains recoverable through Restore deleted items in LumaSearch."); }
        catch (Exception ex)
        { return new(DeletionStatus.Failed, "Recycling was not completed: " + ex.Message + " Use Restore deleted items in LumaSearch to recover " + original.Name + "."); }
    }

    internal StagingLease Stage(SearchResult original, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _ = FileDeletionService.ValidateTarget(original) ?? throw new FileNotFoundException("The selected item no longer exists.");
        var folders = new List<SafeFileHandle>();
        RecoveryEntry? entry = null;
        using var source = PinnedPath.Open(original, securityAccess: true);
        if (original.Name != source.Item.Name) throw new IOException("The selected name does not match its verified path.");
        if (!original.IsDirectory && !original.IsLink && FileIdentityService.LinkCount(source.Handle) > 1)
            throw new IOException("Protected recycling is unavailable for files with multiple hard links because protection would also affect their other names. The item was left in place.");
        bool protectedOriginal = false, moved = false;
        try
        {
            string storage = RootFor(original.FullPath);
            folders.Add(RecoverySecurity.OpenDirectory(storage, create: true, TestOnly));
            string user = Path.Combine(storage, RecoverySecurity.UserSid);
            folders.Add(RecoverySecurity.OpenDirectory(user, create: true, TestOnly));
            string directory = Path.Combine(user, Guid.NewGuid().ToString("N"));
            folders.Add(RecoverySecurity.OpenDirectory(directory, create: true, TestOnly));
            string payload = Path.Combine(directory, "item");
            folders.Add(RecoverySecurity.OpenDirectory(payload, create: true, TestOnly));
            entry = new(Path.Combine(directory, "record.json"), new(original, Path.Combine(payload, original.Name),
                Convert.ToBase64String(RecoverySecurity.Capture(source.Handle)), RecoveryState.Prepared, DateTime.UtcNow));
            // Write-through record first: Prepared can be recovered whether the object
            // is still at its original path or the subsequent handle rename completed.
            WriteRecord(entry);
            token.ThrowIfCancellationRequested();
            RecoverySecurity.ProtectItem(source.Handle, TestOnly);
            protectedOriginal = true;
            RecoverySecurity.Rename(source.Handle, folders[^1], original.Name);
            moved = true;
            FileIdentityService.EnsureSame(original, FileIdentityService.Read(source.Handle, entry.Record.StagedPath));
            entry = entry with { Record = entry.Record with { State = RecoveryState.Staged } };
            WriteRecord(entry);
            return new(entry, folders);
        }
        catch (Exception failure)
        {
            if (protectedOriginal && !moved && entry is not null)
            {
                try
                {
                    RecoverySecurity.RestoreLabel(source.Handle, Convert.FromBase64String(entry.Record.SecurityDescriptor));
                    WriteRecord(entry with { Record = entry.Record with { State = RecoveryState.Restored } });
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
            }
            foreach (var handle in folders.AsEnumerable().Reverse()) handle.Dispose();
            if (moved) throw new IOException("Recycling preparation was interrupted after staging. Use Restore deleted items in LumaSearch to recover the original item. " + failure.Message, failure);
            throw;
        }
    }

    internal RecoveryEntry[] List()
    {
        var entries = new List<RecoveryEntry>();
        IEnumerable<string> roots = _testRoot is not null ? [_testRoot] : DriveInfo.GetDrives()
            .Where(drive => drive.DriveType is DriveType.Fixed or DriveType.Removable).Select(drive => Path.Combine(drive.RootDirectory.FullName, FolderName));
        foreach (string root in roots)
        {
            if (!Directory.Exists(root)) continue;
            using var rootGuard = RecoverySecurity.OpenDirectory(root, create: false, TestOnly);
            string user = Path.Combine(root, RecoverySecurity.UserSid);
            if (!Directory.Exists(user)) continue;
            using var userGuard = RecoverySecurity.OpenDirectory(user, create: false, TestOnly);
            foreach (string directory in Directory.EnumerateDirectories(user))
            {
                if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _)) continue;
                string path = Path.Combine(directory, "record.json");
                try
                {
                    using var guard = RecoverySecurity.OpenDirectory(directory, create: false, TestOnly);
                    if (!File.Exists(path)) continue; // No record means Stage could not begin.
                    var entry = ReadRecord(path);
                    if (entry.Record.State != RecoveryState.Restored) entries.Add(entry);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or Win32Exception)
                {
                    entries.Add(new(path, new(new("Unreadable recovery record", directory, true), "", "", RecoveryState.Unavailable, DateTime.MinValue, Detail: ex.Message)));
                }
            }
        }
        return entries.OrderByDescending(entry => entry.Record.CreatedUtc).ToArray();
    }

    internal ItemDeletionOutcome Restore(string recordPath, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string normalized = Path.GetFullPath(recordPath);
        string directory = Path.GetDirectoryName(normalized)!;
        string user = Path.GetDirectoryName(directory)!;
        string storage = Path.GetDirectoryName(user)!;
        if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _) || Path.GetFileName(normalized) != "record.json" ||
            !user.Equals(Path.Combine(storage, RecoverySecurity.UserSid), StringComparison.OrdinalIgnoreCase) ||
            !storage.Equals(RootFor(normalized), StringComparison.OrdinalIgnoreCase))
            throw new IOException("The selected record is outside protected recovery storage.");
        using var storageGuard = RecoverySecurity.OpenDirectory(storage, create: false, TestOnly);
        using var userGuard = RecoverySecurity.OpenDirectory(user, create: false, TestOnly);
        using var recordGuard = RecoverySecurity.OpenDirectory(directory, create: false, TestOnly);
        var entry = ReadRecord(normalized);
        var original = entry.Record.Original;
        if (entry.Record.State == RecoveryState.Restored) return new(original, DeletionStatus.Restored, "Already restored: " + original.Name);
        string expectedStage = Path.Combine(directory, "item", original.Name);
        if (!entry.Record.StagedPath.Equals(expectedStage, StringComparison.Ordinal) || original.Identity is null)
            throw new IOException("The recovery record is invalid.");
        string? sourcePath = FindOriginal(entry);
        if (sourcePath is null) throw new IOException("The recorded item could not be found. The Recycle Bin may have been emptied or the item moved manually.");
        using var source = PinnedPath.Open(original with { FullPath = sourcePath }, securityAccess: true);
        if (!sourcePath.Equals(original.FullPath, StringComparison.Ordinal))
        {
            string parent = Path.GetDirectoryName(original.FullPath)!;
            var parentItem = SearchResult.Capture(parent);
            if (!parentItem.IsDirectory || parentItem.IsLink) throw new IOException("The original parent must exist as a physical directory before restoring.");
            using var destination = PinnedPath.Open(parentItem, deleteAccess: false);
            token.ThrowIfCancellationRequested();
            RecoverySecurity.Rename(source.Handle, destination.Handle, original.Name);
        }
        // Restore permissions only after the object is back at the verified destination.
        RecoverySecurity.RestoreLabel(source.Handle, Convert.FromBase64String(entry.Record.SecurityDescriptor));
        WriteRecord(entry with { Record = entry.Record with { State = RecoveryState.Restored } });
        return new(original, DeletionStatus.Restored, "Restored to: " + original.FullPath);
    }

    internal async Task<DeletionOutcome> RestoreBatchAsync(DeletionRequest request, CancellationToken token, Func<DeletionOutcome, Task> progress)
    {
        var records = request.RecoveryRecords ?? throw new ArgumentException("Recovery records are required.");
        var items = request.Items ?? [request.Item];
        if (records.Length != items.Length || records.Length == 0) throw new ArgumentException("Invalid recovery selection.");
        var outcomes = new List<ItemDeletionOutcome>();
        string? error = null;
        for (int i = 0; i < records.Length; i++)
        {
            if (token.IsCancellationRequested) break;
            try { outcomes.Add(await Task.Run(() => Restore(records[i], token), CancellationToken.None)); }
            catch (OperationCanceledException) { outcomes.Add(new(items[i], DeletionStatus.Cancelled, "Restoration interrupted; inspect the recovery record.")); break; }
            catch (Exception ex) { outcomes.Add(new(items[i], DeletionStatus.Failed, ex.Message)); }
            try { await progress(new(DeletionStatus.Unknown, "Restoration in progress.", outcomes.ToArray())); }
            catch (Exception ex) { error = "Restoration stopped because its progress could not be saved: " + ex.Message; break; }
        }
        return BatchDeletion.Summarize(records.Length, outcomes.ToArray(), DeletionMode.RecycleBin, error, restoring: true);
    }

    private static string? FindOriginal(RecoveryEntry entry)
    {
        var candidates = new List<string> { entry.Record.StagedPath };
        if (entry.Record.RecycledPath is not null) candidates.Add(entry.Record.RecycledPath);
        // Recovery after a move whose terminal record was interrupted, or after native
        // Windows Restore, also checks the original and deterministic staging locations.
        candidates.Add(entry.Record.Original.FullPath);
        foreach (string path in candidates.Distinct(StringComparer.Ordinal))
        {
            try
            {
                if (SearchResult.Capture(path).Identity == entry.Record.Original.Identity) return path;
            }
            catch (Exception ex) when (FileSearchService.IsFileSystemException(ex)) { }
        }
        string bin = Path.Combine(Path.GetPathRoot(entry.Record.Original.FullPath)!, "$Recycle.Bin", RecoverySecurity.UserSid);
        if (Directory.Exists(bin))
            foreach (string path in Directory.EnumerateFileSystemEntries(bin, "$R*"))
            {
                try { if (SearchResult.Capture(path).Identity == entry.Record.Original.Identity) return path; }
                catch (Exception ex) when (FileSearchService.IsFileSystemException(ex)) { }
            }
        return null;
    }

    internal static RecoveryEntry ReadRecord(string path) => new(path,
        JsonSerializer.Deserialize<RecoveryRecord>(File.ReadAllText(path)) ?? throw new IOException("The recovery record is empty."));

    internal static void WriteRecord(RecoveryEntry entry)
    {
        string temporary = entry.RecordPath + ".pending";
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(entry.Record);
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { stream.Write(bytes); stream.Flush(flushToDisk: true); }
        File.Move(temporary, entry.RecordPath, overwrite: true);
    }

    internal sealed class StagingLease(RecoveryEntry entry, List<SafeFileHandle> folders) : IDisposable
    {
        internal RecoveryEntry Entry { get; set; } = entry;
        public void Dispose() { foreach (var handle in folders.AsEnumerable().Reverse()) handle.Dispose(); }
    }
}
