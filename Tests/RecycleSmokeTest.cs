using System.IO;
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
