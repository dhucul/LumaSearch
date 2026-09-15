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
            string file = Path.Combine(root, "LumaSearch disposable file test.txt");
            await File.WriteAllTextAsync(file, "Disposable LumaSearch recycle integration test.");
            var fileSnapshot = SearchResult.Capture(file);
            fileSnapshot = fileSnapshot with { FullPath = char.ToLowerInvariant(fileSnapshot.FullPath[0]) + fileSnapshot.FullPath[1..] };
            var fileOutcome = await FileDeletionService.DeleteAsync(fileSnapshot, DeletionMode.RecycleBin);
            if (fileOutcome.Status != DeletionStatus.Recycled) throw new InvalidOperationException("Recycling was not confirmed.");
            if (File.Exists(file)) throw new InvalidOperationException("Recycled file remains at its original location.");
            Console.WriteLine("PASS: Windows confirmed the test file was moved to the Recycle Bin.");
            string folder = Path.Combine(root, "LumaSearch disposable folder test");
            Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(Path.Combine(folder, "sample.txt"), "Disposable fixture.");
            var folderOutcome = await FileDeletionService.DeleteAsync(SearchResult.Capture(folder), DeletionMode.RecycleBin);
            if (folderOutcome.Status != DeletionStatus.Recycled) throw new InvalidOperationException("Folder recycling was not confirmed.");
            if (Directory.Exists(folder)) throw new InvalidOperationException("Recycled folder remains at its original location.");
            Console.WriteLine("PASS: Windows confirmed the test folder and its contents were moved to the Recycle Bin.");
            string first = Path.Combine(root, "LumaSearch disposable batch file 1.txt");
            string second = Path.Combine(root, "LumaSearch disposable batch file 2.txt");
            await File.WriteAllTextAsync(first, "Disposable batch fixture.");
            await File.WriteAllTextAsync(second, "Disposable batch fixture.");
            var batch = await BatchDeletion.ExecuteAsync([SearchResult.Capture(first), SearchResult.Capture(second)],
                DeletionMode.RecycleBin, CancellationToken.None);
            if (batch.Status != DeletionStatus.Recycled || batch.Items?.Length != 2 || File.Exists(first) || File.Exists(second))
                throw new InvalidOperationException("Multi-item recycling was not confirmed.");
            Console.WriteLine("PASS: Windows confirmed both selected test files reached the Recycle Bin.");
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
