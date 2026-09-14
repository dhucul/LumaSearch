using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace LumaSearch;

public sealed record DeletionRequest(SearchResult Item, DeletionMode Mode);

public sealed class DeletionJob : IDisposable
{
    private readonly Process _worker;
    public string JournalPath { get; }
    public static string JournalRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LumaSearch", "Operations");
    private DeletionJob(Process worker, string journalPath) { _worker = worker; JournalPath = journalPath; }

    public static async Task<DeletionJob> StartAsync(SearchResult item, DeletionMode mode, CancellationToken token,
        string? journalRoot = null)
    {
        string directory = Path.Combine(journalRoot ?? JournalRoot, Guid.NewGuid().ToString("N"));
        await Task.Run(() => Directory.CreateDirectory(directory), token);
        await File.WriteAllTextAsync(Path.Combine(directory, "request.json"), JsonSerializer.Serialize(new DeletionRequest(item, mode)), token);
        token.ThrowIfCancellationRequested();
        string executable = Path.ChangeExtension(typeof(App).Assembly.Location, ".exe");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true };
        start.ArgumentList.Add("--delete-worker");
        start.ArgumentList.Add(directory);
        var process = Process.Start(start) ?? throw new IOException("Could not start the deletion worker.");
        return new DeletionJob(process, directory);
    }

    public async Task<DeletionOutcome> WaitAsync()
    {
        await _worker.WaitForExitAsync();
        string result = Path.Combine(JournalPath, "result.json");
        if (!File.Exists(result)) return new(DeletionStatus.Unknown,
            "The deletion worker stopped without a confirmed result. Refresh the search before taking another action.");
        var outcome = JsonSerializer.Deserialize<DeletionOutcome>(await File.ReadAllTextAsync(result));
        if (outcome is null || outcome.Status == DeletionStatus.Unknown)
        {
            outcome = new(DeletionStatus.Unknown, "The worker exited without confirming the outcome. Refresh the search before another deletion.");
            await WriteOutcomeAsync(JournalPath, outcome);
        }
        return outcome;
    }

    public void RequestCancel()
    {
        // EOF requests a safe stop. The worker owns its current OS call and writes the final outcome.
        try { _worker.StandardInput.Close(); } catch (InvalidOperationException) { } catch (IOException) { }
    }

    public static async Task RunWorkerAsync(string directory)
    {
        using var cancel = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try { await Console.In.ReadLineAsync(); cancel.Cancel(); }
            catch (IOException) { cancel.Cancel(); }
            catch (ObjectDisposedException) { }
        });
        DeletionOutcome outcome;
        try
        {
            var request = JsonSerializer.Deserialize<DeletionRequest>(await File.ReadAllTextAsync(Path.Combine(directory, "request.json")))
                ?? throw new IOException("Invalid deletion request.");
            await WriteOutcomeAsync(directory, new(DeletionStatus.Unknown, "Operation in progress; its outcome is not confirmed."));
            outcome = await FileDeletionService.DeleteAsync(request.Item, request.Mode, cancel.Token);
        }
        catch (OperationCanceledException)
        { outcome = new(DeletionStatus.Cancelled, "Stopped at a safe boundary. Some items may already have been processed; refresh the search."); }
        catch (Exception ex) { outcome = new(DeletionStatus.Failed, ex.Message); }
        await WriteOutcomeAsync(directory, outcome);
    }

    private static async Task WriteOutcomeAsync(string directory, DeletionOutcome outcome)
    {
        string temporary = Path.Combine(directory, "result.pending");
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(outcome));
        File.Move(temporary, Path.Combine(directory, "result.json"), true);
    }

    public static async Task<string?> GetPreviousOutcomeAsync()
    {
        if (!Directory.Exists(JournalRoot)) return null;
        var newest = Directory.EnumerateDirectories(JournalRoot).OrderByDescending(Directory.GetCreationTimeUtc).FirstOrDefault();
        if (newest is null) return null;
        string result = Path.Combine(newest, "result.json");
        if (!File.Exists(result)) return "A previous deletion has no confirmed outcome. Refresh its location before deleting anything else.";
        var outcome = JsonSerializer.Deserialize<DeletionOutcome>(await File.ReadAllTextAsync(result));
        return outcome is null ? null : "Previous deletion: " + outcome.Message;
    }
    public void Dispose() => _worker.Dispose();
}
