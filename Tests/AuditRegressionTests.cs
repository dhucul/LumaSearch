using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Threading.Channels;
using LumaSearch;

internal static class AuditRegressionTests
{
    internal static async Task RunAsync(string root, Action<bool, string> check)
    {
        check(ExecutableManifest.GetExecutionLevel(Path.ChangeExtension(typeof(App).Assembly.Location, ".exe")) == "requireAdministrator",
            "built executable embeds the administrator requirement");
        string path = Path.Combine(root, "identity.txt");
        File.WriteAllText(path, "original");
        var original = SearchResult.Capture(path);
        File.Move(path, path + ".original");
        File.WriteAllText(path, "replacement");
        bool rejected = false;
        try { FileDeletionService.Delete(original); } catch (IOException) { rejected = true; }
        check(rejected && File.ReadAllText(path) == "replacement", "same-type replacement rejected before permanent deletion");
        rejected = false;
        try { await FileDeletionService.DeleteAsync(original, DeletionMode.RecycleBin); } catch (IOException) { rejected = true; }
        check(rejected && File.Exists(path), "same-type replacement rejected before recycling");

        string folder = Path.Combine(root, "identity-folder");
        Directory.CreateDirectory(folder);
        var originalFolder = SearchResult.Capture(folder);
        Directory.Move(folder, folder + "-original");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "keep.txt"), "replacement");
        rejected = false;
        try { FileDeletionService.Delete(originalFolder); } catch (IOException) { rejected = true; }
        check(rejected && File.Exists(Path.Combine(folder, "keep.txt")), "replaced folder is not recursively deleted");

        var missing = SearchResult.Capture(path);
        File.Delete(path);
        check(FileDeletionService.Delete(missing).Status == DeletionStatus.AlreadyMissing,
            "missing deletion has a distinct outcome");
        check((await FileDeletionService.DeleteAsync(missing, DeletionMode.RecycleBin)).Status == DeletionStatus.AlreadyMissing,
            "missing recycling never claims recoverability");

        var upper = new SearchResult("A", Path.Combine(root, "A"), true);
        check(!FileDeletionService.IsSameOrDescendant(Path.Combine(root, "a", "child"), upper),
            "case-distinct sibling subtree survives reconciliation");
        var lower = new SearchResult("a", Path.Combine(root, "a"), true);
        var collection = new ResultCollection();
        for (int i = 0; i < 20_000; i++) collection.Add(new SearchResult("item" + i, Path.Combine(root, "item" + i), false));
        int notifications = 0;
        collection.CollectionChanged += (_, e) => { notifications++; if (e.Action != NotifyCollectionChangedAction.Reset) throw new Exception("Expected a bulk reset."); };
        collection.ReplaceAll([lower]);
        check(collection.Count == 1 && notifications == 1, "large result reconciliation emits one collection reset");

        var channel = Channel.CreateBounded<SearchResult>(2);
        var stats = new ScanStatistics();
        Task scan = Task.Run(() => FileSearchService.ScanAsync(new SearchOptions(root, "*", "", true, true, true, MaxResults: 3), channel.Writer, stats, CancellationToken.None));
        int count = 0;
        await foreach (var result in channel.Reader.ReadAllAsync()) count++;
        await scan;
        check(count == 3 && stats.LimitReached, "search result cap marks the scan incomplete");

        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var cancel = new CancellationTokenSource();
        Task<int> blocked = ReadOnlyWork.RunAsync(() => { started.Set(); release.Wait(); return 1; }, cancel.Token);
        if (!started.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("Read-only worker did not start.");
        cancel.Cancel();
        bool cancelled = false;
        try { await blocked.WaitAsync(cancel.Token); } catch (OperationCanceledException) { cancelled = true; }
        finally { release.Set(); }
        await blocked;
        check(cancelled, "cancellation releases a caller while its OS work is blocked");
        check(MainWindow.ProgressPhase(OperationState.Cancelling, "Searching") == "Cancelling" &&
            MainWindow.ProgressPhase(OperationState.Closing, "Searching") == "Closing", "progress cannot overwrite cancellation or closing phase");

        var sink = new RecycleProgressSink();
        sink.PostDeleteItem(0x80, IntPtr.Zero, 0, new IntPtr(1));
        int refusal = sink.PostDeleteItem(0x80, IntPtr.Zero, 0, IntPtr.Zero);
        check(refusal < 0 && sink.UnconfirmedDeletion && sink.Failure < 0,
            "mixed recycling results fail when any item lacks a recycle destination");
        using var stopped = new CancellationTokenSource();
        stopped.Cancel();
        check(new RecycleProgressSink(token: stopped.Token).PreDeleteItem(0x80, IntPtr.Zero) < 0,
            "native recycling callback honors cancellation before a side effect");

        string linkTarget = Path.Combine(root, "junction-target");
        string link = Path.Combine(root, "junction-link");
        Directory.CreateDirectory(linkTarget);
        File.WriteAllText(Path.Combine(linkTarget, "keep.txt"), "target content");
        using (var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{linkTarget}\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!)
        {
            await mklink.WaitForExitAsync();
            if (mklink.ExitCode != 0) throw new IOException("Could not create the isolated junction fixture: " + await mklink.StandardError.ReadToEndAsync());
        }
        var linkResult = SearchResult.Capture(link);
        check(linkResult.IsLink && FileDeletionService.GetConfirmationMessage(linkResult).Contains("Its target"), "folder links carry distinct metadata and confirmation scope");
        FileDeletionService.Delete(linkResult);
        check(!Directory.Exists(link) && File.Exists(Path.Combine(linkTarget, "keep.txt")), "handle-based folder-link deletion preserves its target");

        string workerPath = Path.Combine(root, "worker-delete.txt");
        File.WriteAllText(workerPath, "fixture");
        using var job = await DeletionJob.StartAsync(SearchResult.Capture(workerPath), DeletionMode.Permanent,
            CancellationToken.None, Path.Combine(root, "journals"), useManagedTestHost: true);
        var workerOutcome = await job.WaitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        check(workerOutcome.Status == DeletionStatus.PermanentlyDeleted && !File.Exists(workerPath) && File.Exists(Path.Combine(job.JournalPath, "result.json")),
            "isolated deletion worker completes and journals its exact outcome");
        string cancelPath = Path.Combine(root, "worker-cancel.txt");
        File.WriteAllText(cancelPath, "fixture");
        using var cancelledJob = await DeletionJob.StartAsync(SearchResult.Capture(cancelPath), DeletionMode.Permanent,
            CancellationToken.None, Path.Combine(root, "journals"), useManagedTestHost: true);
        cancelledJob.RequestCancel();
        var stoppedOutcome = await cancelledJob.WaitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        check((stoppedOutcome.Status is DeletionStatus.Cancelled or DeletionStatus.NotStarted && File.Exists(cancelPath)) ||
            (stoppedOutcome.Status == DeletionStatus.PermanentlyDeleted && !File.Exists(cancelPath)),
            "worker cancellation preserves a truthful terminal outcome");
    }
}
