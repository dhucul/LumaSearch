using System.IO;
using System.Text.Json;
using LumaSearch;

internal static class RecycleSmokeTest
{
    // Opt-in OS integration test. Only these newly generated disposable items enter the bin.
    internal static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "LumaSearch-recycle-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new RecoveryStorage(Path.Combine(root, "protected-store"));
            string file = Path.Combine(root, "LumaSearch disposable file test.txt");
            await File.WriteAllTextAsync(file, "Disposable LumaSearch recycle integration test.");
            var fileSnapshot = SearchResult.Capture(file);
            fileSnapshot = fileSnapshot with { FullPath = char.ToLowerInvariant(fileSnapshot.FullPath[0]) + fileSnapshot.FullPath[1..] };
            var fileOutcome = await store.RecycleAsync(fileSnapshot, CancellationToken.None);
            if (fileOutcome.Status != DeletionStatus.Recycled) throw new InvalidOperationException("Recycling was not confirmed.");
            if (File.Exists(file)) throw new InvalidOperationException("Recycled file remains at its original location.");
            Console.WriteLine("PASS: Windows confirmed the test file was moved to the Recycle Bin.");
            string folder = Path.Combine(root, "LumaSearch disposable folder test");
            Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(Path.Combine(folder, "sample.txt"), "Disposable fixture.");
            var folderOutcome = await store.RecycleAsync(SearchResult.Capture(folder), CancellationToken.None);
            if (folderOutcome.Status != DeletionStatus.Recycled) throw new InvalidOperationException("Folder recycling was not confirmed.");
            if (Directory.Exists(folder)) throw new InvalidOperationException("Recycled folder remains at its original location.");
            Console.WriteLine("PASS: Windows confirmed the test folder and its contents were moved to the Recycle Bin.");
            string first = Path.Combine(root, "LumaSearch disposable batch file 1.txt");
            string second = Path.Combine(root, "LumaSearch disposable batch file 2.txt");
            await File.WriteAllTextAsync(first, "Disposable batch fixture.");
            await File.WriteAllTextAsync(second, "Disposable batch fixture.");
            var batchItems = new[] { SearchResult.Capture(first), SearchResult.Capture(second) };
            var batchOutcomes = new List<ItemDeletionOutcome>();
            foreach (var item in batchItems)
            {
                var outcome = await store.RecycleAsync(item, CancellationToken.None);
                batchOutcomes.Add(new(item, outcome.Status, outcome.Message));
            }
            var batch = BatchDeletion.Summarize(2, batchOutcomes.ToArray(), DeletionMode.RecycleBin);
            if (batch.Status != DeletionStatus.Recycled || batch.Items?.Length != 2 || File.Exists(first) || File.Exists(second))
                throw new InvalidOperationException("Multi-item recycling was not confirmed.");
            Console.WriteLine("PASS: Windows confirmed both selected test files reached the Recycle Bin.");
            foreach (var entry in store.List())
            {
                if (store.Restore(entry.RecordPath, CancellationToken.None).Status != DeletionStatus.Restored)
                    throw new InvalidOperationException("Restoration from the Recycle Bin failed.");
            }
            if (!File.Exists(file) || !Directory.Exists(folder) || !File.Exists(first) || !File.Exists(second))
                throw new InvalidOperationException("Recycled fixtures were not restored to their original paths.");
            Console.WriteLine("PASS: Recycled files and folders restored to their original locations.");
            string race = Path.Combine(root, "replacement-race.txt");
            await File.WriteAllTextAsync(race, "Original race fixture");
            var original = SearchResult.Capture(race);
            var raceOutcome = await store.RecycleAsync(original, CancellationToken.None, _ => File.WriteAllText(race, "Replacement must survive"));
            if (raceOutcome.Status != DeletionStatus.Recycled || File.ReadAllText(race) != "Replacement must survive")
                throw new InvalidOperationException("Recycling affected a replacement at the original path.");
            var recovery = store.List().Single(entry => entry.Record.Original.Identity == original.Identity);
            if (SearchResult.Capture(recovery.Record.RecycledPath!).Identity != original.Identity)
                throw new InvalidOperationException("The Recycle Bin does not contain the verified original object.");
            File.Delete(race);
            store.Restore(recovery.RecordPath, CancellationToken.None);
            Console.WriteLine("PASS: Replacement at the original path survives recycling of the verified staged object.");

            async Task<RecoveryEntry> RecycleFixture(string name, bool isFolder = false)
            {
                string path = Path.Combine(root, name);
                if (isFolder) { Directory.CreateDirectory(path); await File.WriteAllTextAsync(Path.Combine(path, "child.txt"), "Disposable child"); }
                else await File.WriteAllTextAsync(path, "Disposable emptying fixture");
                var snapshot = SearchResult.Capture(path);
                var outcome = await store.RecycleAsync(snapshot, CancellationToken.None);
                if (outcome.Status != DeletionStatus.Recycled) throw new InvalidOperationException("Could not recycle emptying fixture.");
                return store.List().Single(item => item.Record.Original.Identity == snapshot.Identity);
            }
            string Metadata(RecoveryEntry entry) => Path.Combine(Path.GetDirectoryName(entry.Record.RecycledPath!)!,
                "$I" + Path.GetFileName(entry.Record.RecycledPath!)[2..]);
            var unrelated = await RecycleFixture("unselected-recycled-item.txt");
            var emptyFile = await RecycleFixture("empty-recycled-item.txt");
            var emptyFolder = await RecycleFixture("empty-recycled-folder", isFolder: true);
            await File.WriteAllTextAsync(emptyFile.OriginalPath, "Replacement must survive emptying");
            foreach (var entry in new[] { emptyFile, emptyFolder })
            {
                if (!File.Exists(Metadata(entry))) throw new InvalidOperationException("Windows did not create paired Recycle Bin metadata.");
                // Exercise discovery after an interrupted native destination checkpoint.
                if (entry == emptyFolder) RecoveryStorage.WriteRecord(entry with { Record = entry.Record with { RecycledPath = null } });
                var outcome = store.Empty(entry.RecordPath, entry.Record.Original, CancellationToken.None);
                if (outcome.Status != DeletionStatus.PermanentlyDeleted || !FileDeletionService.IsDefinitelyMissing(entry.Record.RecycledPath!)
                    || File.Exists(Metadata(entry)) || File.Exists(entry.RecordPath))
                    throw new InvalidOperationException("Emptying did not remove the recycled payload, metadata, and recovery record.");
            }
            if (File.ReadAllText(emptyFile.OriginalPath) != "Replacement must survive emptying"
                || !File.Exists(unrelated.Record.RecycledPath!) || !File.Exists(Metadata(unrelated)))
                throw new InvalidOperationException("Emptying affected a replacement or an unselected Recycle Bin item.");
            Console.WriteLine("PASS: Emptying removes only approved recycled files/folders and paired metadata, preserving replacements and unselected bin items.");

            var interrupted = await RecycleFixture("interrupted-emptying.txt");
            RecoveryStorage.WriteRecord(interrupted with { Record = interrupted.Record with { EmptyingMetadata = SearchResult.Capture(Metadata(interrupted)) } });
            FileDeletionService.Delete(interrupted.Record.Original with { FullPath = interrupted.Record.RecycledPath! });
            var retried = store.Empty(interrupted.RecordPath, interrupted.Record.Original, CancellationToken.None);
            if (retried.Status != DeletionStatus.AlreadyMissing || File.Exists(Metadata(interrupted)) || File.Exists(interrupted.RecordPath))
                throw new InvalidOperationException("Interrupted emptying could not finish its verified metadata cleanup.");
            Console.WriteLine("PASS: Retrying interrupted emptying removes its verified metadata after the payload is gone.");

            var pendingEntry = await RecycleFixture("pending-emptying.txt");
            ItemDeletionOutcome pendingOutcome;
            using (var reader = new FileStream(pendingEntry.Record.RecycledPath!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                pendingOutcome = store.Empty(pendingEntry.RecordPath, pendingEntry.Record.Original, CancellationToken.None);
            if (pendingOutcome.Status != DeletionStatus.Pending || pendingOutcome.RecoveryCleanup?.MetadataPath != Metadata(pendingEntry))
                throw new InvalidOperationException("Pending emptying lost its paired metadata cleanup context.");
            string journal = Path.Combine(root, "pending-journal");
            string operation = Path.Combine(journal, "operation");
            Directory.CreateDirectory(operation);
            await File.WriteAllTextAsync(Path.Combine(operation, "result.json"),
                JsonSerializer.Serialize(BatchDeletion.Summarize(1, [pendingOutcome], DeletionMode.Permanent)));
            string startup = (await DeletionJob.GetPreviousOutcomeAsync(journal))!;
            if (!FileDeletionService.IsDefinitelyMissing(pendingEntry.Record.RecycledPath!) || !File.Exists(Metadata(pendingEntry))
                || !File.Exists(pendingEntry.RecordPath) || !startup.Contains("need confirmation") || !startup.Contains("Use Empty saved items"))
                throw new InvalidOperationException("Startup incorrectly resolved pending emptying before its metadata and record were removed.");
            store.Empty(pendingEntry.RecordPath, pendingEntry.Record.Original, CancellationToken.None);
            if (File.Exists(Metadata(pendingEntry)) || File.Exists(pendingEntry.RecordPath)
                || (await DeletionJob.GetPreviousOutcomeAsync(journal))!.Contains("need confirmation"))
                throw new InvalidOperationException("Completed cleanup did not resolve the pending emptying operation.");
            Console.WriteLine("PASS: Pending native emptying remains unresolved until both Recycle Bin metadata and the recovery record are removed.");
            store.Empty(unrelated.RecordPath, unrelated.Record.Original, CancellationToken.None);
        }
        finally
        {
            string resolved = Path.GetFullPath(root);
            string temporaryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolved).StartsWith("LumaSearch-recycle-test-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected fixture cleanup path.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
    }
}
