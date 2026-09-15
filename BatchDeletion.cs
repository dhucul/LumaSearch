using System.IO;
using System.Diagnostics;

namespace LumaSearch;

public sealed record BatchPreflight(SearchResult[] Eligible, ItemDeletionOutcome[] Unavailable, int CoveredCount = 0);

public static class BatchDeletion
{
    public static BatchPreflight ValidateSelection(IEnumerable<SearchResult> selection, CancellationToken token)
    {
        var ready = new List<SearchResult>();
        var unavailable = new List<ItemDeletionOutcome>();
        var readyDirectories = new HashSet<string>(StringComparer.Ordinal);
        int covered = 0;
        foreach (var item in selection.DistinctBy(item => item.FullPath, StringComparer.Ordinal).OrderBy(item => item.FullPath.Length))
        {
            token.ThrowIfCancellationRequested();
            if (HasAncestor(item.FullPath, readyDirectories)) { covered++; continue; }
            try
            {
                var verified = FileDeletionService.ValidateTarget(item);
                if (verified is null) unavailable.Add(new(item, DeletionStatus.AlreadyMissing, "Already missing; no operation will be performed."));
                else
                {
                    ready.Add(verified);
                    if (verified.IsDirectory && !verified.IsLink) readyDirectories.Add(verified.FullPath);
                }
            }
            catch (Exception ex) when (FileSearchService.IsFileSystemException(ex) || ex is ArgumentException)
            { unavailable.Add(new(item, DeletionStatus.Failed, ex.Message)); }
        }
        return new(ready.ToArray(), unavailable.ToArray(), covered);
    }

    public static SearchResult[] Plan(IEnumerable<SearchResult> selection)
    {
        var unique = selection.DistinctBy(item => item.FullPath, StringComparer.Ordinal).ToArray();
        var directories = unique.Where(item => item.IsDirectory && !item.IsLink).Select(item => item.FullPath).ToHashSet(StringComparer.Ordinal);
        return unique.Where(item => !HasAncestor(item.FullPath, directories)).ToArray();
    }

    private static bool HasAncestor(string path, HashSet<string> directories)
    {
        string? parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path));
        while (!string.IsNullOrEmpty(parent))
        {
            if (directories.Contains(parent)) return true;
            parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(parent));
        }
        return false;
    }

    public static async Task<DeletionOutcome> ExecuteAsync(IEnumerable<SearchResult> selection, DeletionMode mode,
        CancellationToken token, Func<DeletionOutcome, Task>? progress = null)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        var plan = Plan(selection);
        if (plan.Length == 0) throw new ArgumentException("Select at least one item.");
        var outcomes = new List<ItemDeletionOutcome>();
        string? checkpointError = null;
        long checkpoint = Stopwatch.GetTimestamp();
        foreach (var item in plan)
        {
            if (token.IsCancellationRequested) break;
            DeletionOutcome result;
            try { result = await FileDeletionService.DeleteAsync(item, mode, token); }
            catch (OperationCanceledException) { result = new(DeletionStatus.Cancelled, "Cancelled; this item may have been partly processed."); }
            catch (Exception ex) { result = new(DeletionStatus.Failed, ex.Message); }
            outcomes.Add(new(item, result.Status, result.Message));
            if (progress is not null && (outcomes.Count == 1 || outcomes.Count == plan.Length || Stopwatch.GetElapsedTime(checkpoint).TotalMilliseconds >= 500))
            {
                try
                {
                    await progress(new(DeletionStatus.Unknown, $"Processed {outcomes.Count} of {plan.Length} selected targets; operation in progress.", outcomes.ToArray()));
                }
                catch (Exception ex)
                {
                    checkpointError = "Processing stopped because progress could not be saved: " + ex.Message;
                    break; // Completed outcomes remain available; never start another destructive step.
                }
                checkpoint = Stopwatch.GetTimestamp();
            }
            if (result.Status == DeletionStatus.Cancelled) break;
        }
        return Summarize(plan.Length, outcomes.ToArray(), mode, checkpointError);
    }

    public static DeletionOutcome Summarize(int total, ItemDeletionOutcome[] completed, DeletionMode mode,
        string? error = null, bool restoring = false)
    {
        if (total < completed.Length || total < 1) throw new ArgumentOutOfRangeException(nameof(total));
        int done = completed.Count(item => item.Status is DeletionStatus.Recycled or DeletionStatus.PermanentlyDeleted or DeletionStatus.Restored);
        int missing = completed.Count(item => item.Status == DeletionStatus.AlreadyMissing);
        int failures = completed.Count(item => item.Status is DeletionStatus.Failed or DeletionStatus.Unknown);
        int interrupted = completed.Count(item => item.Status == DeletionStatus.Cancelled);
        int pending = completed.Count(item => item.Status == DeletionStatus.Pending);
        int notAttempted = total - completed.Length + completed.Count(item => item.Status == DeletionStatus.NotStarted);
        bool cancelled = interrupted > 0 || notAttempted > 0;
        DeletionStatus status = error is not null || failures > 0 ? DeletionStatus.Failed : pending > 0 ? DeletionStatus.Pending
            : cancelled ? DeletionStatus.Cancelled
            : missing == total ? DeletionStatus.AlreadyMissing
            : restoring ? DeletionStatus.Restored
            : mode == DeletionMode.RecycleBin ? DeletionStatus.Recycled : DeletionStatus.PermanentlyDeleted;
        string verb = restoring ? "restored" : mode == DeletionMode.RecycleBin ? "recycled" : "permanently deleted";
        var counts = new BatchCounts(done, missing, failures, interrupted, notAttempted, pending);
        string detail = (error is not null ? " " + error : "") + (cancelled
            ? " Cancelled; interrupted folders may be partly processed. Refresh the search." : "")
            + (pending > 0 ? " Close open files and refresh the search to confirm removal; folders may be partly processed." : "");
        if (!restoring && mode == DeletionMode.RecycleBin && (failures > 0 || interrupted > 0))
            detail += " Items may be in protected recovery storage. Use Restore deleted items in LumaSearch.";
        return new(status, $"{done:N0} targets {verb}; {missing:N0} already missing; {failures:N0} failed/unconfirmed; "
            + $"{interrupted:N0} cancelled/possibly partial; {notAttempted:N0} not attempted; {pending:N0} awaiting removal confirmation." + detail, completed, counts);
    }

    public static SearchResult[] Reconcile(SearchResult[] results, IEnumerable<ItemDeletionOutcome> outcomes)
    {
        var removed = new HashSet<string>(StringComparer.Ordinal);
        var removedDirectories = new HashSet<string>(StringComparer.Ordinal);
        var uncertain = new HashSet<string>(StringComparer.Ordinal);
        var uncertainDirectories = new HashSet<string>(StringComparer.Ordinal);
        foreach (var outcome in outcomes)
        {
            bool success = outcome.Status is DeletionStatus.Recycled or DeletionStatus.PermanentlyDeleted or DeletionStatus.AlreadyMissing;
            (success ? removed : uncertain).Add(outcome.Item.FullPath);
            if (outcome.Item.IsDirectory && !outcome.Item.IsLink)
                (success ? removedDirectories : uncertainDirectories).Add(outcome.Item.FullPath);
        }
        return results.Where(item =>
        {
            if (removed.Contains(item.FullPath) || HasAncestor(item.FullPath, removedDirectories)) return false;
            if (uncertain.Contains(item.FullPath) || HasAncestor(item.FullPath, uncertainDirectories))
                return !FileDeletionService.IsDefinitelyMissing(item.FullPath);
            return true;
        }).ToArray();
    }
}
