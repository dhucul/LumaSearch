using System.IO;

namespace LumaSearch;

internal sealed partial class RecoveryStorage
{
    internal ItemDeletionOutcome Empty(string recordPath, SearchResult approved, CancellationToken token)
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
        ItemDeletionOutcome result;
        using (var recordGuard = RecoverySecurity.OpenDirectory(directory, create: false, TestOnly))
        {
            var entry = ReadRecord(normalized);
            var original = entry.Record.Original;
            FileIdentityService.EnsureSame(approved, original);
            string stageDirectory = Path.Combine(directory, "item");
            string expectedStage = Path.Combine(stageDirectory, original.Name);
            if (original.FullPath != approved.FullPath || !Path.IsPathFullyQualified(original.FullPath) ||
                original.Name != Path.GetFileName(original.FullPath) ||
                original.Name.IndexOfAny(['\\', '/', ':', '\0']) >= 0 || original.Name is "" or "." or ".." ||
                entry.Record.StagedPath != expectedStage || !Enum.IsDefined(entry.Record.State))
                throw new IOException("The recovery record has changed or is invalid. Reload saved items before retrying.");
            if (entry.Record.State == RecoveryState.Restored)
                return new(original, DeletionStatus.AlreadyMissing, "Already restored; the item was kept.");

            using var stageGuard = CaptureIfPresent(stageDirectory) is null ? null
                : RecoverySecurity.OpenDirectory(stageDirectory, create: false, TestOnly);
            string bin = Path.Combine(Path.GetPathRoot(original.FullPath)!, "$Recycle.Bin", RecoverySecurity.UserSid);
            string? recycledPath = entry.Record.RecycledPath;
            if (recycledPath is not null && !IsBinPayload(recycledPath, bin))
                throw new IOException("The recorded Recycle Bin location is invalid.");

            // Unlike restoration, emptying never searches the original location for a deletion target.
            string? savedPath = FindSavedPayload(entry, bin, token);
            if (savedPath is null && CaptureIfPresent(original.FullPath)?.Identity == original.Identity)
            {
                // Prepared records can still own a temporary integrity label on an unmoved item.
                Restore(normalized, token);
                return new(original, DeletionStatus.AlreadyMissing, "The item is at its original location and was kept.");
            }

            using var payload = savedPath is null ? null : PinnedPath.Open(original with { FullPath = savedPath });
            SearchResult? metadata = entry.Record.EmptyingMetadata;
            if (metadata is not null && (recycledPath is null || metadata.FullPath != MetadataPath(recycledPath) || metadata.IsDirectory || metadata.IsLink))
                throw new IOException("The saved Recycle Bin metadata is invalid.");
            if (payload is not null && IsBinPayload(savedPath!, bin) && metadata is null)
            {
                recycledPath = savedPath;
                metadata = CaptureIfPresent(MetadataPath(savedPath!));
                if (metadata is { IsDirectory: true } or { IsLink: true })
                    throw new IOException("The Recycle Bin metadata is not a regular file.");
                // Pin the verified payload while recording its paired metadata identity. This lets
                // a retry remove the same metadata even if interruption follows payload removal.
                entry = entry with { Record = entry.Record with { RecycledPath = recycledPath, EmptyingMetadata = metadata } };
                WriteRecord(entry);
            }
            else if (metadata is null && recycledPath is not null && CaptureIfPresent(MetadataPath(recycledPath)) is not null)
                throw new IOException("Leftover Recycle Bin metadata could not be verified. The recovery record was kept.");

            using var metadataPin = metadata is null || CaptureIfPresent(metadata.FullPath) is null ? null : PinnedPath.Open(metadata);
            token.ThrowIfCancellationRequested();
            // Keep this durable warning even if deletion stops partway through a folder,
            // the process exits, or its operation journal cannot be updated afterward.
            entry = entry with { Record = entry.Record with { State = RecoveryState.Emptying,
                Detail = "Emptying started. Some contents may be permanently missing; only remaining contents can be restored." } };
            WriteRecord(entry);
            var cleanup = new RecoveryCleanup(normalized, metadata?.FullPath);
            token.ThrowIfCancellationRequested();
            if (payload is not null)
            {
                var deleted = FileDeletionService.DeletePinned(payload, token);
                if (deleted.Status != DeletionStatus.PermanentlyDeleted)
                    return new(original with { FullPath = savedPath! }, deleted.Status, deleted.Message, RecoveryCleanup: cleanup);
            }
            if (metadataPin is not null)
            {
                var deleted = FileDeletionService.DeletePinned(metadataPin, token);
                if (deleted.Status != DeletionStatus.PermanentlyDeleted)
                    return new(metadata!, deleted.Status, "The saved payload was removed, but its Recycle Bin metadata is still pending.", RecoveryCleanup: cleanup);
            }
            token.ThrowIfCancellationRequested();
            if (stageGuard is not null && Directory.EnumerateFileSystemEntries(stageDirectory).Any())
                throw new IOException("Unexpected items remain in staging. The recovery record was kept for inspection.");
            // Remove only known record files. Empty directories are cleaned non-recursively below.
            File.Delete(normalized + ".pending");
            File.Delete(normalized);
            result = new(original, payload is null ? DeletionStatus.AlreadyMissing : DeletionStatus.PermanentlyDeleted,
                payload is null ? "The saved payload was already missing; its recovery record was removed."
                    : "Permanently removed saved item: " + original.Name);
        }
        TryRemoveEmptyDirectory(Path.Combine(directory, "item"));
        TryRemoveEmptyDirectory(directory);
        return result;
    }

    internal async Task<DeletionOutcome> EmptyBatchAsync(DeletionRequest request, CancellationToken token, Func<DeletionOutcome, Task> progress)
    {
        var records = request.RecoveryRecords ?? throw new ArgumentException("Recovery records are required to empty saved items.");
        var items = request.Items ?? [request.Item];
        if (!request.EmptyRecovery || request.Mode != DeletionMode.Permanent || records.Length != items.Length || records.Length == 0)
            throw new ArgumentException("Invalid saved-item emptying request.");
        var outcomes = new List<ItemDeletionOutcome>();
        string? error = null;
        for (int i = 0; i < records.Length; i++)
        {
            if (token.IsCancellationRequested) break;
            try { outcomes.Add(await Task.Run(() => Empty(records[i], items[i], token), CancellationToken.None)); }
            catch (OperationCanceledException) { outcomes.Add(new(items[i], DeletionStatus.Cancelled, "Emptying interrupted; remaining saved data can be inspected in Restore deleted items.")); break; }
            catch (Exception ex) { outcomes.Add(new(items[i], DeletionStatus.Failed, ex.Message)); }
            try { await progress(new(DeletionStatus.Unknown, "Emptying saved items in progress.", outcomes.ToArray())); }
            catch (Exception ex) { error = "Emptying stopped because its progress could not be saved: " + ex.Message; break; }
        }
        return BatchDeletion.Summarize(records.Length, outcomes.ToArray(), DeletionMode.Permanent, error);
    }

    private static string? FindSavedPayload(RecoveryEntry entry, string bin, CancellationToken token)
    {
        var original = entry.Record.Original;
        var staged = CaptureIfPresent(entry.Record.StagedPath);
        if (staged is not null)
        {
            FileIdentityService.EnsureSame(original, staged);
            if (staged.FullPath == original.FullPath) throw new IOException("A saved item cannot be its original location.");
            return staged.FullPath;
        }
        if (entry.Record.RecycledPath is { } recorded && CaptureIfPresent(recorded) is { } recycled)
        {
            FileIdentityService.EnsureSame(original, recycled);
            return recorded;
        }
        if (CaptureIfPresent(bin) is null) return null;
        foreach (string path in Directory.EnumerateFileSystemEntries(bin, "$R*"))
        {
            token.ThrowIfCancellationRequested();
            if (CaptureIfPresent(path) is { } candidate && candidate.Identity == original.Identity)
            {
                FileIdentityService.EnsureSame(original, candidate);
                return path;
            }
        }
        return null;
    }

    private static bool IsBinPayload(string path, string bin) => Path.IsPathFullyQualified(path)
        && Path.GetFullPath(path).Equals(path, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Path.GetDirectoryName(path), bin, StringComparison.OrdinalIgnoreCase)
        && Path.GetFileName(path).StartsWith("$R", StringComparison.Ordinal) && Path.GetFileName(path).Length > 2;

    private static string MetadataPath(string recycledPath) => Path.Combine(Path.GetDirectoryName(recycledPath)!, "$I" + Path.GetFileName(recycledPath)[2..]);

    private static SearchResult? CaptureIfPresent(string path)
    {
        try { return SearchResult.Capture(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static void TryRemoveEmptyDirectory(string path)
    {
        try { Directory.Delete(path, recursive: false); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
