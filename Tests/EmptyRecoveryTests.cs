using System.IO;
using System.Text.Json;
using LumaSearch;

internal static class EmptyRecoveryTests
{
    internal static async Task RunAsync(string root, Action<bool, string> check)
    {
        string fixtures = Path.Combine(root, "empty-recovery-tests");
        Directory.CreateDirectory(fixtures);
        var store = new RecoveryStorage(Path.Combine(fixtures, "store"));
        RecoveryEntry Stage(string name, bool folder = false)
        {
            string path = Path.Combine(fixtures, name);
            if (folder) { Directory.CreateDirectory(path); File.WriteAllText(Path.Combine(path, "child.txt"), "Disposable child"); }
            else File.WriteAllText(path, "Disposable saved item");
            using var lease = store.Stage(SearchResult.Capture(path), CancellationToken.None);
            return lease.Entry;
        }

        var kept = Stage("kept.txt");
        var file = Stage("empty.txt");
        File.WriteAllText(file.OriginalPath, "Replacement must survive");
        var emptied = store.Empty(file.RecordPath, file.Record.Original, CancellationToken.None);
        check(emptied.Status == DeletionStatus.PermanentlyDeleted && !File.Exists(file.Record.StagedPath)
            && !Directory.Exists(Path.GetDirectoryName(file.RecordPath)) && File.ReadAllText(file.OriginalPath) == "Replacement must survive"
            && File.Exists(kept.Record.StagedPath), "emptying removes the saved file and record while preserving replacements and unselected saved items");

        var folder = Stage("folder", folder: true);
        store.Empty(folder.RecordPath, folder.Record.Original, CancellationToken.None);
        check(!Directory.Exists(folder.Record.StagedPath) && !File.Exists(folder.RecordPath), "emptying removes saved folders and their contents");

        var partial = Stage("partial-folder", folder: true);
        File.WriteAllText(Path.Combine(partial.Record.StagedPath, "second.txt"), "Second disposable child");
        string[] children = Directory.GetFiles(partial.Record.StagedPath);
        bool partialFailure = false;
        using (var locked = new FileStream(children[1], FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try { store.Empty(partial.RecordPath, partial.Record.Original, CancellationToken.None); }
            catch (IOException) { partialFailure = true; }
        }
        var reloaded = new RecoveryStorage(Path.Combine(fixtures, "store")).List().Single(item => item.RecordPath == partial.RecordPath);
        check(partialFailure && Directory.GetFiles(partial.Record.StagedPath).Length == 1 && reloaded.Record.State == RecoveryState.Emptying
            && reloaded.MayBeIncomplete && reloaded.Status.Contains("incomplete"),
            "partial emptying persists an incomplete-content warning that survives reloading recovery storage");
        var restoredPartial = await store.RestoreBatchAsync(new(partial.Record.Original, DeletionMode.RecycleBin,
            RecoveryRecords: [partial.RecordPath]), CancellationToken.None, _ => Task.CompletedTask);
        check(restoredPartial.Status == DeletionStatus.Restored && Directory.GetFiles(partial.OriginalPath).Length == 1
            && restoredPartial.Items![0].Warning is not null && restoredPartial.Message.Contains("Only remaining contents were restored")
            && RecoveryStorage.ReadRecord(partial.RecordPath).Record.State == RecoveryState.Restored,
            "restoration of a partly emptied folder warns in both the item outcome and the visible batch summary");

        var checkpointFailure = Stage("checkpoint-failure.txt");
        bool checkpointRefused = false;
        using (var lockedRecord = new FileStream(checkpointFailure.RecordPath + ".pending", FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            try { store.Empty(checkpointFailure.RecordPath, checkpointFailure.Record.Original, CancellationToken.None); }
            catch (IOException) { checkpointRefused = true; }
        }
        check(checkpointRefused && File.Exists(checkpointFailure.Record.StagedPath)
            && RecoveryStorage.ReadRecord(checkpointFailure.RecordPath).Record.State == RecoveryState.Staged,
            "emptying performs no deletion if its durable incomplete-content checkpoint cannot be saved");
        File.Delete(checkpointFailure.RecordPath + ".pending");

        var cancelled = Stage("cancelled.txt");
        using (var cancel = new CancellationTokenSource())
        {
            cancel.Cancel();
            bool stopped = false;
            try { store.Empty(cancelled.RecordPath, cancelled.Record.Original, cancel.Token); }
            catch (OperationCanceledException) { stopped = true; }
            check(stopped && File.Exists(cancelled.Record.StagedPath)
                && RecoveryStorage.ReadRecord(cancelled.RecordPath).Record.State == RecoveryState.Staged,
                "cancelling emptying before it starts preserves the saved item and its original recovery state");
        }

        var changed = Stage("changed.txt");
        bool refused = false;
        try { store.Empty(changed.RecordPath, kept.Record.Original, CancellationToken.None); }
        catch (IOException) { refused = true; }
        check(refused && File.Exists(changed.Record.StagedPath), "emptying refuses a record that differs from the approved item");
        RecoveryStorage.WriteRecord(changed with { Record = changed.Record with { RecycledPath = file.OriginalPath } });
        refused = false;
        try { store.Empty(changed.RecordPath, changed.Record.Original, CancellationToken.None); }
        catch (IOException) { refused = true; }
        check(refused && File.Exists(changed.Record.StagedPath) && File.Exists(file.OriginalPath), "emptying rejects a recorded recycle location outside the user's Recycle Bin");
        RecoveryStorage.WriteRecord(changed);

        var movedBack = Stage("moved-back.txt");
        store.Restore(movedBack.RecordPath, CancellationToken.None);
        RecoveryStorage.WriteRecord(movedBack); // Simulate interruption before restoration's terminal record.
        var preserved = store.Empty(movedBack.RecordPath, movedBack.Record.Original, CancellationToken.None);
        check(preserved.Status == DeletionStatus.AlreadyMissing && File.Exists(movedBack.OriginalPath)
            && RecoveryStorage.ReadRecord(movedBack.RecordPath).Record.State == RecoveryState.Restored,
            "emptying preserves an original restored before its recovery record was updated");

        var pending = Stage("pending.txt");
        ItemDeletionOutcome pendingOutcome;
        using (var reader = new FileStream(pending.Record.StagedPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            pendingOutcome = store.Empty(pending.RecordPath, pending.Record.Original, CancellationToken.None);
            check(pendingOutcome.Status == DeletionStatus.Pending && pendingOutcome.Item.FullPath == pending.Record.StagedPath
                && pendingOutcome.RecoveryCleanup?.RecordPath == pending.RecordPath && File.Exists(pending.RecordPath),
                "pending removal retains its recovery record and cleanup context for reconciliation");
        }
        string journal = Path.Combine(fixtures, "pending-journal");
        string operation = Path.Combine(journal, "operation");
        Directory.CreateDirectory(operation);
        string resultPath = Path.Combine(operation, "result.json");
        await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(BatchDeletion.Summarize(1, [pendingOutcome], DeletionMode.Permanent)));
        string startup = (await DeletionJob.GetPreviousOutcomeAsync(journal))!;
        check(FileDeletionService.IsDefinitelyMissing(pending.Record.StagedPath) && startup.Contains("need confirmation")
            && startup.Contains("Use Empty saved items"), "startup keeps emptying unresolved when its payload is gone but its recovery record remains");
        var retried = store.Empty(pending.RecordPath, pending.Record.Original, CancellationToken.None);
        check(retried.Status == DeletionStatus.AlreadyMissing && !File.Exists(pending.RecordPath), "emptying can finish cleanup after a pending payload disappears");
        check(!(await DeletionJob.GetPreviousOutcomeAsync(journal))!.Contains("need confirmation"),
            "startup resolves pending emptying after the saved payload and recovery record are both removed");

        string leftoverMetadata = Path.Combine(fixtures, "leftover-metadata");
        File.WriteAllText(leftoverMetadata, "Disposable metadata fixture");
        var metadataPending = pendingOutcome with { RecoveryCleanup = new(pending.RecordPath, leftoverMetadata) };
        await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(BatchDeletion.Summarize(1, [metadataPending], DeletionMode.Permanent)));
        check((await DeletionJob.GetPreviousOutcomeAsync(journal))!.Contains("need confirmation"),
            "startup still requires cleanup when only the recorded Recycle Bin metadata remains");
        File.Delete(leftoverMetadata);
        check(!(await DeletionJob.GetPreviousOutcomeAsync(journal))!.Contains("need confirmation"),
            "startup resolves metadata cleanup only after the final recorded file is gone");

        var ordinaryPending = pendingOutcome with { RecoveryCleanup = null };
        await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(BatchDeletion.Summarize(1, [ordinaryPending], DeletionMode.Permanent)));
        check(!(await DeletionJob.GetPreviousOutcomeAsync(journal))!.Contains("need confirmation"),
            "ordinary permanent deletions retain their existing pending-removal reconciliation");

        var request = new DeletionRequest(kept.Record.Original, DeletionMode.Permanent,
            [kept.Record.Original, cancelled.Record.Original], [kept.RecordPath, cancelled.RecordPath], EmptyRecovery: true);
        var batch = await store.EmptyBatchAsync(request, CancellationToken.None, _ => Task.CompletedTask);
        check(batch.Counts?.Completed == 2 && !File.Exists(kept.RecordPath) && !File.Exists(cancelled.RecordPath),
            "emptying a batch processes precisely the approved recovery records");
        var invalid = await DeletionJob.ExecuteRecordedAsync(new(changed.Record.Original, DeletionMode.Permanent, EmptyRecovery: true),
            CancellationToken.None, _ => Task.CompletedTask);
        check(invalid.Status == DeletionStatus.Failed && File.Exists(changed.Record.StagedPath), "an emptying request without recovery records cannot fall through to ordinary deletion");
    }
}
