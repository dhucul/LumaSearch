using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Channels;
using LumaSearch;

internal static class OperationSafetyTests
{
    internal static async Task RunAsync(string root, Action<bool, string> check)
    {
        string fixtures = Path.Combine(root, "operation-safety");
        Directory.CreateDirectory(fixtures);
        string file = Path.Combine(fixtures, "content.txt");
        File.WriteAllText(file, "matching needle");
        var original = SearchResult.Capture(file);
        bool replacementRefused = false;
        var contentResults = new RecordingWriter();
        await FileSearchService.ScanAsync(new SearchOptions(fixtures, "content.txt", "needle", true, false, true),
            contentResults, new ScanStatistics(), CancellationToken.None, opened =>
            {
                try { File.Move(file, file + ".moved"); }
                catch (IOException) { replacementRefused = true; }
                check(opened.Identity == original.Identity, "content is matched through the captured object's handle");
            });
        check(replacementRefused && contentResults.Items is [{ Identity: var foundIdentity }] && foundIdentity == original.Identity,
            "replacement cannot change the file while content and identity are being read");

        string scan = Path.Combine(fixtures, "scan");
        string victim = Path.Combine(scan, "folder");
        string moved = Path.Combine(fixtures, "moved-folder");
        string outside = Path.Combine(fixtures, "outside");
        Directory.CreateDirectory(victim); Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "must-not-be-found.txt"), "outside");
        bool swapped = false;
        var directoryResults = new RecordingWriter(item =>
        {
            if (item.FullPath != victim || swapped) return;
            Directory.Move(victim, moved);
            using var command = Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c mklink /J \"{victim}\" \"{outside}\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
            command.WaitForExit();
            if (command.ExitCode != 0) throw new IOException(command.StandardError.ReadToEnd());
            swapped = true;
        });
        var statistics = new ScanStatistics();
        await FileSearchService.ScanAsync(new SearchOptions(scan, "", "", true, true, true), directoryResults, statistics, CancellationToken.None);
        check(swapped && directoryResults.Items.All(item => item.Name != "must-not-be-found.txt") && statistics.Skipped > 0,
            "a junction substituted after result emission is never traversed");
        FileDeletionService.Delete(SearchResult.Capture(victim));
        check(File.Exists(Path.Combine(outside, "must-not-be-found.txt")), "removing the substituted junction preserves its target");

        string pendingPath = Path.Combine(fixtures, "pending.txt");
        File.WriteAllText(pendingPath, "pending fixture");
        var pendingItem = SearchResult.Capture(pendingPath);
        using (var reader = new FileStream(pendingPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            var outcome = FileDeletionService.Delete(pendingItem);
            check(outcome.Status == DeletionStatus.Pending && Directory.EnumerateFiles(fixtures).Contains(pendingPath),
                "a deletion held open by another reader is reported as pending");
            check(BatchDeletion.Reconcile([pendingItem], [new(pendingItem, outcome.Status, outcome.Message)]).Length == 1,
                "pending deletion remains in results until removal is confirmed");
        }
        check(FileDeletionService.IsDefinitelyMissing(pendingPath), "pending file disappears when its last reader closes");
        var mixed = BatchDeletion.Summarize(2, [new(original, DeletionStatus.Failed, "Locked")], DeletionMode.Permanent);
        check(mixed.Status == DeletionStatus.Failed && mixed.Counts is { Failed: 1, NotAttempted: 1 } && mixed.Message.Contains("Cancelled"),
            "failure remains visible when cancellation leaves targets unattempted");

        string journals = Path.Combine(fixtures, "recovery");
        string old = Path.Combine(journals, "old-unresolved");
        string newer = Path.Combine(journals, "new-completed");
        Directory.CreateDirectory(old); Directory.CreateDirectory(newer);
        await File.WriteAllTextAsync(Path.Combine(old, "result.json"), JsonSerializer.Serialize(new DeletionOutcome(DeletionStatus.Unknown, "Older worker interrupted.")));
        await File.WriteAllTextAsync(Path.Combine(newer, "result.json"), JsonSerializer.Serialize(new DeletionOutcome(DeletionStatus.PermanentlyDeleted, "Newer worker completed.")));
        Directory.SetCreationTimeUtc(old, DateTime.UtcNow.AddHours(-1));
        string recovered = (await DeletionJob.GetPreviousOutcomeAsync(journals))!;
        check(recovered.Contains("Older worker interrupted") && recovered.Contains("1 previous"),
            "newer success does not hide an older unresolved deletion");
        string preparingRoot = Path.Combine(fixtures, "preparation-recovery");
        Directory.CreateDirectory(Path.Combine(preparingRoot, "incomplete.preparing"));
        string preparation = (await DeletionJob.GetPreviousOutcomeAsync(preparingRoot))!;
        check(preparation.Contains("no deletion started") && !preparation.Contains("need confirmation"),
            "interrupted preparation has an explicit no-deletion outcome");
        await File.WriteAllTextAsync(Path.Combine(old, "result.json"), "{broken");
        check((await DeletionJob.GetPreviousOutcomeAsync(journals))!.Contains("could not be read"),
            "unreadable history remains visible as unresolved");

        string untrusted = Path.Combine(fixtures, "untrusted-request");
        Directory.CreateDirectory(untrusted);
        await File.WriteAllTextAsync(Path.Combine(untrusted, "request.json"), JsonSerializer.Serialize(new DeletionRequest(original, DeletionMode.Permanent)));
        var launch = new ProcessStartInfo("dotnet.exe")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true };
        launch.ArgumentList.Add(typeof(App).Assembly.Location);
        launch.ArgumentList.Add("--delete-worker"); launch.ArgumentList.Add(untrusted);
        using (var worker = Process.Start(launch)!)
        {
            var response = worker.StandardOutput.ReadToEndAsync();
            worker.StandardInput.Close();
            await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            var outcome = JsonSerializer.Deserialize<DeletionOutcome>(await response)!;
            check(outcome.Status == DeletionStatus.NotStarted && File.Exists(file),
                "worker ignores mutable request.json without an approved pipe request");
        }
    }

    private sealed class RecordingWriter(Action<SearchResult>? received = null) : ChannelWriter<SearchResult>
    {
        internal List<SearchResult> Items { get; } = [];
        public override bool TryWrite(SearchResult item) { Items.Add(item); received?.Invoke(item); return true; }
        public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default) => new(true);
        public override bool TryComplete(Exception? error = null) => true;
    }
}
