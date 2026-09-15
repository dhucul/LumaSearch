using System.IO;
using System.Text.Json;
using LumaSearch;

internal static class ElevatedRecoveryTest
{
    internal static async Task RunAsync(string probeDirectory)
    {
        string directory = Path.GetFullPath(probeDirectory);
        if (!Path.GetFileName(directory).StartsWith("elevated-recovery-check-", StringComparison.Ordinal) || !Directory.Exists(directory))
            throw new ArgumentException("A prepared disposable probe directory is required.");
        string report = Path.Combine(directory, "result.json");
        RecoveryEntry? staged = null;
        var store = new RecoveryStorage();
        try
        {
            if (!RecoverySecurity.IsAdministrator) throw new UnauthorizedAccessException("This check requires administrator execution.");
            string file = Path.Combine(directory, "LumaSearch-recovery-security-fixture.txt");
            const string contents = "Disposable LumaSearch security fixture.";
            if (File.ReadAllText(file) != contents) throw new IOException("The disposable fixture was not prepared correctly.");
            var original = SearchResult.Capture(file);
            var outcome = await store.RecycleAsync(original, CancellationToken.None, entry =>
            {
                staged = entry;
                File.WriteAllText(Path.Combine(directory, "ready.json"), JsonSerializer.Serialize(new { entry.Record.StagedPath }));
                var deadline = DateTime.UtcNow.AddSeconds(45);
                while (!File.Exists(Path.Combine(directory, "continue.signal")))
                {
                    if (DateTime.UtcNow >= deadline) throw new TimeoutException("The non-elevated protection check did not respond.");
                    Thread.Sleep(100);
                }
            });
            if (outcome.Status != DeletionStatus.Recycled) throw new IOException(outcome.Message);
            if (staged is null) throw new IOException("Staging did not produce a recovery record.");
            if (store.Restore(staged.RecordPath, CancellationToken.None).Status != DeletionStatus.Restored ||
                File.ReadAllText(file) != contents || SearchResult.Capture(file).Identity != original.Identity)
                throw new IOException("The elevated round trip did not preserve the fixture.");
            File.WriteAllText(report, JsonSerializer.Serialize(new { Passed = true, Message = "Production administrator staging, native recycling and restoration passed." }));
        }
        catch (Exception ex)
        {
            if (staged is not null)
            {
                try { store.Restore(staged.RecordPath, CancellationToken.None); }
                catch (Exception restoreError) { File.WriteAllText(Path.Combine(directory, "restore-error.txt"), restoreError.ToString()); }
            }
            File.WriteAllText(report, JsonSerializer.Serialize(new { Passed = false, Message = ex.ToString() }));
            Environment.ExitCode = 1;
        }
    }
}
