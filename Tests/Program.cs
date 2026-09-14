using System.Text;
using System.IO;
using System.Threading.Channels;
using LumaSearch;

try
{
if (args.Length == 2 && args[0] == "--verify-release")
{
    ReleaseVerification.Verify(args[1]);
    return;
}
if (args.Length == 1 && args[0] == "--recycle-smoke")
{
    await RecycleSmokeTest.RunAsync();
    return;
}

string root = Path.Combine(Path.GetTempPath(), "LumaSearch-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("FAILED: " + name);
    Console.WriteLine("PASS: " + name);
    passed++;
}
async Task<(List<SearchResult> Results, ScanStatistics Stats)> Scan(string pattern = "*", string text = "",
    bool files = true, bool folders = true, bool hidden = false, NameMatchMode mode = NameMatchMode.Wildcard)
{
    var channel = Channel.CreateBounded<SearchResult>(2);
    var stats = new ScanStatistics();
    var task = Task.Run(() => FileSearchService.ScanAsync(new SearchOptions(root, pattern, text, files, folders, hidden, mode),
        channel.Writer, stats, CancellationToken.None));
    var results = new List<SearchResult>();
    await foreach (var result in channel.Reader.ReadAllAsync()) results.Add(result);
    await task;
    return (results, stats);
}
try
{
    Check(FileSearchService.CreateNameMatcher("a[1]?.*").IsMatch("A[1]x.TXT"), "wildcards and literal regex characters");
    Check(!FileSearchService.CreateNameMatcher("report?.txt").IsMatch("report12.txt"), "question mark matches one character");
    Check(!FileSearchService.CreateNameMatcher("*.txt").IsMatch("a.txt.bak"), "wildcards match the full filename");
    Check(FileSearchService.CreateNameMatcher("report", NameMatchMode.Contains).IsMatch("Annual REPORT.pdf"), "contains mode matches literal partial names ignoring case");
    Check(!FileSearchService.CreateNameMatcher("report", NameMatchMode.Exact).IsMatch("report.txt"), "exact mode requires the full name including extension");
    Check(FileSearchService.CreateNameMatcher("REPORT.txt", NameMatchMode.Exact).IsMatch("report.txt"), "exact mode ignores case");
    Check(!FileSearchService.CreateNameMatcher("*.txt", NameMatchMode.Contains).IsMatch("report.txt"), "contains mode does not interpret wildcard characters");
    Check(!FileSearchService.CreateNameMatcher("r?port.txt", NameMatchMode.Exact).IsMatch("report.txt"), "exact mode does not interpret wildcard characters");
    Check(Enum.GetValues<NameMatchMode>().All(mode => FileSearchService.CreateNameMatcher("", mode).IsMatch("any.txt")), "blank name accepts all filenames in every mode");
    Directory.CreateDirectory(Path.Combine(root, "nested", "deep"));
    File.WriteAllText(Path.Combine(root, "nested", "deep", "Report.txt"), "hello\nNeEdLe here");
    File.WriteAllText(Path.Combine(root, "other.log"), "needle");
    File.WriteAllText(Path.Combine(root, "split.txt"), "nee\ndle");
    File.WriteAllText(Path.Combine(root, "boundary.txt"), new string('x', 8190) + "needle");
    File.WriteAllText(Path.Combine(root, "unicode.txt"), "A needle in UTF16", Encoding.Unicode);
    File.WriteAllText(Path.Combine(root, "unicode32.txt"), "A needle in UTF32", Encoding.UTF32);
    File.WriteAllBytes(Path.Combine(root, "invalid.txt"), [0xff, 0xfe, 0x00, 0x00, 0xff, 0xff, 0xff, 0x7f]);
    string hiddenDir = Path.Combine(root, "secret");
    Directory.CreateDirectory(hiddenDir);
    File.SetAttributes(hiddenDir, FileAttributes.Directory | FileAttributes.Hidden);
    File.WriteAllText(Path.Combine(hiddenDir, "hidden-child.txt"), "needle");
    string hiddenFile = Path.Combine(root, "hidden.txt");
    File.WriteAllText(hiddenFile, "needle");
    File.SetAttributes(hiddenFile, FileAttributes.Hidden);

    var names = await Scan("*.txt", "NEEDLE");
    var exact = await Scan("REPORT.TXT", folders: false, mode: NameMatchMode.Exact);
    Check(exact.Results.Count == 1 && exact.Results[0].Name == "Report.txt", "exact mode reaches recursive scanner");
    var partial = await Scan("nicode", folders: false, mode: NameMatchMode.Contains);
    Check(partial.Results.Count == 2, "contains mode reaches recursive scanner");
    var folderName = await Scan("dee", files: false, mode: NameMatchMode.Contains);
    Check(folderName.Results.Count == 1 && folderName.Results[0].Name == "deep", "contains mode also matches folder names");
    Check(names.Results.Any(x => x.Name == "Report.txt"), "recursive case-insensitive content search");
    Check(names.Results.Any(x => x.Name == "boundary.txt"), "content match spans streaming buffer boundary");
    Check(names.Results.Any(x => x.Name == "unicode.txt") && names.Results.Any(x => x.Name == "unicode32.txt"), "UTF16 and UTF32 BOM detection");
    Check(names.Results.All(x => x.Name != "split.txt" && x.Name != "other.log"), "line boundaries and filename filtering");
    Check(names.Results.All(x => x.Name != "hidden.txt" && x.Name != "hidden-child.txt"), "hidden files and entire hidden subtrees excluded");
    var hiddenResults = await Scan(hidden: true);
    Check(hiddenResults.Results.Any(x => x.Name == "hidden.txt") && hiddenResults.Results.Any(x => x.Name == "hidden-child.txt"), "include hidden option");
    var folders = await Scan(text: "does not exist", files: false);
    Check(folders.Results.Count == 2 && folders.Results.All(x => x.IsDirectory), "folder-only search ignores content filter");
    var filesOnly = await Scan(folders: false);
    Check(filesOnly.Results.All(x => !x.IsDirectory) && filesOnly.Results.Any(x => x.Name == "Report.txt"), "file-only search still traverses folders");

    string lockedPath = Path.Combine(root, "locked.txt");
    File.WriteAllText(lockedPath, "needle");
    using (var locked = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    {
        var lockedResults = await Scan("*.txt", "needle");
        Check(lockedResults.Stats.Skipped >= 1 && lockedResults.Results.All(x => x.Name != "locked.txt"), "locked text files skipped without aborting");
        bool refused = false;
        try { FileDeletionService.Delete(new SearchResult("locked.txt", lockedPath, false)); }
        catch (IOException) { refused = true; }
        Check(refused && File.Exists(lockedPath), "locked deletion fails without hiding file");
    }

    var blockedChannel = Channel.CreateBounded<SearchResult>(1);
    using (var cancellation = new CancellationTokenSource())
    {
        var task = Task.Run(() => FileSearchService.ScanAsync(new SearchOptions(root, "*", "", true, true, true),
            blockedChannel.Writer, new ScanStatistics(), cancellation.Token));
        await blockedChannel.Reader.WaitToReadAsync();
        cancellation.Cancel();
        bool cancelled = false;
        try { await task.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled, "cancellation interrupts backpressure without deadlock");
    }

    string largePath = Path.Combine(root, "large-line.txt");
    using (var output = new StreamWriter(largePath))
    {
        string block = new('x', 8192);
        for (int i = 0; i < 1024; i++) output.Write(block);
        output.Write("needle");
    }
    Check((await Scan("large-line.txt", "needle")).Results.Count == 1, "multi-megabyte line scanned with bounded buffers");

    var deleted = SearchResult.Capture(Path.Combine(root, "nested"));
    string folderConfirmation = FileDeletionService.GetConfirmationMessage(deleted);
    Check(folderConfirmation.StartsWith("Folder: nested\n") && folderConfirmation.Contains("everything inside it")
        && folderConfirmation.Contains("parent folder and other items beside it will stay"), "folder confirmation identifies name and deletion scope");
    string fileConfirmation = FileDeletionService.GetConfirmationMessage(new SearchResult("locked.txt", lockedPath, false));
    Check(fileConfirmation.StartsWith("File: locked.txt\n") && fileConfirmation.Contains("Located in: " + root)
        && fileConfirmation.Contains("Only this file") && fileConfirmation.Contains("all other files will stay"), "file confirmation separates name, location and deletion scope");
    string recycleConfirmation = FileDeletionService.GetConfirmationMessage(deleted, DeletionMode.RecycleBin);
    Check(recycleConfirmation.Contains("sent to the Recycle Bin") && recycleConfirmation.Contains("restored")
        && !recycleConfirmation.Contains("cannot be undone"), "recycle confirmation identifies recoverable operation");
    var recycleGuard = new RecycleProgressSink();
    Check(recycleGuard.PreDeleteItem(0, IntPtr.Zero) < 0 && recycleGuard.RefusedPermanentDelete,
        "recycle operation refuses a permanent-delete callback");
    Check(new RecycleProgressSink().PreDeleteItem(0x80, IntPtr.Zero) == 0,
        "recycle operation accepts a recycling callback");
    string permanentPath = Path.Combine(root, "permanent-mode.txt");
    File.WriteAllText(permanentPath, "fixture");
    await FileDeletionService.DeleteAsync(SearchResult.Capture(permanentPath), DeletionMode.Permanent);
    Check(!File.Exists(permanentPath), "permanent mode routes to direct filesystem deletion");
    Check(FileDeletionService.IsSameOrDescendant(Path.Combine(root, "nested", "deep", "Report.txt"), deleted)
        && !FileDeletionService.IsSameOrDescendant(Path.Combine(root, "nested-other", "a"), deleted), "deletion reconciliation respects directory boundaries");
    FileDeletionService.Delete(deleted);
    Check(!Directory.Exists(deleted.FullPath), "recursive folder deletion");
    FileDeletionService.Delete(SearchResult.Capture(lockedPath));
    Check(FileDeletionService.IsDefinitelyMissing(lockedPath), "file deletion and missing-item detection");
    FileDeletionService.Delete(new SearchResult("locked.txt", lockedPath, false));
    Check(true, "already-missing file deletion is idempotent");
    bool rootRefused = false;
    try { FileDeletionService.Delete(new SearchResult("root", Path.GetPathRoot(root)!, true)); }
    catch (IOException) { rootRefused = true; }
    Check(rootRefused, "filesystem root deletion refused");
    bool missingRefused = false;
    try { await ExplorerService.ShowAsync(new SearchResult("missing.txt", Path.Combine(root, "missing.txt"), false)); }
    catch (FileNotFoundException) { missingRefused = true; }
    Check(missingRefused, "Explorer reports deleted or missing results");
    string explorerPath = Path.Combine(root, "comma, space & café.txt");
    File.WriteAllText(explorerPath, "navigation fixture");
    Check(ExplorerService.GetExistingPath(new SearchResult(Path.GetFileName(explorerPath), explorerPath, false)) == explorerPath,
        "Explorer preserves spaces, punctuation and Unicode paths");
    Check(ExplorerService.GetExistingPath(new SearchResult("fixture", root, true)) == root,
        "Explorer accepts folder results");
    bool changedTypeRefused = false;
    try { ExplorerService.GetExistingPath(new SearchResult("fixture", root, false)); }
    catch (IOException) { changedTypeRefused = true; }
    Check(changedTypeRefused, "Explorer detects a stale item type");
    await AuditRegressionTests.RunAsync(root, Check);
    Exception? uiFailure = null;
    string? previewPath = args.FirstOrDefault();
    var uiThread = new Thread(() =>
    {
        try { WpfSmokeTest.Run(root, previewPath); }
        catch (Exception ex) { uiFailure = ex; }
    });
    uiThread.SetApartmentState(ApartmentState.STA);
    uiThread.Start();
    if (!uiThread.Join(TimeSpan.FromSeconds(30))) throw new TimeoutException("WPF smoke test did not finish.");
    if (uiFailure is not null) throw new InvalidOperationException("WPF smoke test failed.", uiFailure);
    Check(true, "WPF initialization, asynchronous search, selection and layout rendering");
    Console.WriteLine($"{passed} integration checks passed.");
}
finally
{
    // The only recursive cleanup target is the unique fixture directory created above.
    string resolved = Path.GetFullPath(root);
    string temporaryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
    if (!resolved.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase) ||
        !Path.GetFileName(resolved).StartsWith("LumaSearch-tests-", StringComparison.Ordinal))
        throw new InvalidOperationException("Unexpected fixture cleanup path.");
    if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
}
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    Environment.ExitCode = 1;
}
