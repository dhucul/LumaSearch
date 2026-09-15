using System.IO;
using LumaSearch;

internal static class BatchAndDateTests
{
    internal static async Task RunAsync(string root, Action<bool, string> check)
    {
        string folder = Path.Combine(root, "batch-fixture");
        Directory.CreateDirectory(folder);
        string first = Path.Combine(folder, "first.txt");
        string second = Path.Combine(folder, "second.txt");
        File.WriteAllText(first, "first"); File.WriteAllText(second, "second");
        var expected = new DateTime(2025, 3, 2, 14, 30, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(first, expected);
        Directory.SetLastWriteTimeUtc(folder, expected.AddDays(2));
        var a = SearchResult.Capture(first);
        var b = SearchResult.Capture(second);
        var parent = SearchResult.Capture(folder);
        check(a.LastWriteTimeUtc == expected && a.DateModified == expected.ToLocalTime(), "file date comes from metadata and displays local time");
        check(parent.LastWriteTimeUtc == expected.AddDays(2), "folder results include their modified date");
        check(BatchDeletion.Plan([a, parent, b, a]).SequenceEqual([parent]), "batch planning removes duplicate and selected-child targets");
        var upper = new SearchResult("Upper", Path.Combine(root, "Upper"), true);
        var lowerChild = new SearchResult("child", Path.Combine(root, "upper", "child"), false);
        check(BatchDeletion.Plan([upper, lowerChild]).Length == 2, "batch planning preserves case-distinct paths");
        File.Move(first, first + ".saved");
        File.WriteAllText(first, "replacement fixture");
        try
        {
            var preflight = BatchDeletion.ValidateSelection([a, b], CancellationToken.None);
            check(preflight.Eligible.Length == 1 && preflight.Eligible[0].FullPath == second &&
                preflight.Unavailable.Length == 1 && preflight.Unavailable[0].Status == DeletionStatus.Failed,
                "preflight retains valid targets when another selected file has been replaced");
        }
        finally { File.Delete(first); File.Move(first + ".saved", first); }
        var unverifiedParent = parent with { Identity = null };
        var childFallback = BatchDeletion.ValidateSelection([unverifiedParent, a], CancellationToken.None);
        check(childFallback.Eligible.Length == 1 && childFallback.Eligible[0].FullPath == first && childFallback.Unavailable.Length == 1,
            "an unavailable selected parent does not discard a valid selected child");
        using (var locked = new FileStream(first, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = await BatchDeletion.ExecuteAsync([a, b], DeletionMode.Permanent, CancellationToken.None);
            check(result.Status == DeletionStatus.Failed && result.Items?.Length == 2 && File.Exists(first) && !File.Exists(second),
                "batch deletion continues past a locked item and reports per-item outcomes");
            check(BatchDeletion.Reconcile([a, b], result.Items!).SequenceEqual([a]), "batch reconciliation keeps failed items and removes successful ones");
        }
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            var result = await BatchDeletion.ExecuteAsync([a], DeletionMode.Permanent, cancelled.Token);
            check(result.Status == DeletionStatus.Cancelled && File.Exists(first), "cancelled batch performs no new deletion");
        }
        File.WriteAllText(second, "second");
        b = SearchResult.Capture(second);
        using var job = await DeletionJob.StartAsync([a, b], DeletionMode.Permanent, CancellationToken.None,
            Path.Combine(root, "batch-journals"), useManagedTestHost: true);
        var completed = await job.WaitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        check(completed.Status == DeletionStatus.PermanentlyDeleted && completed.Items?.Length == 2 && !File.Exists(first) && !File.Exists(second),
            "one worker processes and journals multiple selected targets");

        File.WriteAllText(first, "checkpoint fixture"); File.WriteAllText(second, "must remain");
        a = SearchResult.Capture(first); b = SearchResult.Capture(second);
        var checkpointFailure = await BatchDeletion.ExecuteAsync([a, b], DeletionMode.Permanent, CancellationToken.None,
            _ => throw new IOException("Injected checkpoint failure."));
        check(checkpointFailure.Status == DeletionStatus.Failed && checkpointFailure.Items is [{ Status: DeletionStatus.PermanentlyDeleted }]
            && !File.Exists(first) && File.Exists(second) && checkpointFailure.Counts?.NotAttempted == 1,
            "checkpoint failure retains completed outcomes and stops the next deletion");

        File.WriteAllText(first, "transient checkpoint fixture");
        a = SearchResult.Capture(first);
        int writes = 0;
        DeletionOutcome? recorded = null;
        var recordedResult = await DeletionJob.ExecuteRecordedAsync(new DeletionRequest(a, DeletionMode.Permanent, [a, b]),
            CancellationToken.None, outcome =>
            {
                if (++writes == 2) throw new IOException("Injected transient journal failure.");
                recorded = outcome;
                return Task.CompletedTask;
            });
        check(recordedResult.Status == DeletionStatus.Failed && recorded?.Items is [{ Status: DeletionStatus.PermanentlyDeleted }]
            && File.Exists(second), "final journal write preserves known outcomes after a checkpoint error");

        File.WriteAllText(first, "persistent checkpoint fixture");
        a = SearchResult.Capture(first);
        writes = 0;
        var unsaved = await DeletionJob.ExecuteRecordedAsync(new DeletionRequest(a, DeletionMode.Permanent, [a, b]),
            CancellationToken.None, _ => ++writes == 1 ? Task.CompletedTask : throw new IOException("Storage unavailable."));
        check(unsaved.Items is [{ Status: DeletionStatus.PermanentlyDeleted }] && unsaved.Message.Contains("History could not be saved")
            && File.Exists(second), "persistent journal failure still returns known outcomes to the caller");

        var interrupted = BatchDeletion.Summarize(3,
            [new(a, DeletionStatus.PermanentlyDeleted, "Done"), new(b, DeletionStatus.Cancelled, "Partial")], DeletionMode.Permanent);
        check(interrupted.Counts == new BatchCounts(1, 0, 0, 1, 1) && interrupted.Counts.Total == 3,
            "cancellation totals explicitly account for the interrupted target");
    }
}
