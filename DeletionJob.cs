using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace LumaSearch;

public sealed record DeletionRequest(SearchResult Item, DeletionMode Mode, SearchResult[]? Items = null);

public sealed class DeletionJob : IDisposable
{
    private readonly Process _worker;
    public string JournalPath { get; }
    public static string JournalRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LumaSearch", "Operations");
    private DeletionJob(Process worker, string journalPath) { _worker = worker; JournalPath = journalPath; }

    public static Task<DeletionJob> StartAsync(SearchResult item, DeletionMode mode, CancellationToken token,
        string? journalRoot = null, bool useManagedTestHost = false) => StartAsync([item], mode, token, journalRoot, useManagedTestHost);

    public static async Task<DeletionJob> StartAsync(SearchResult[] selection, DeletionMode mode, CancellationToken token,
        string? journalRoot = null, bool useManagedTestHost = false)
    {
        var items = BatchDeletion.Plan(selection);
        if (items.Length == 0) throw new ArgumentException("Select at least one item.");
        string directory = Path.Combine(journalRoot ?? JournalRoot, Guid.NewGuid().ToString("N"));
        await Task.Run(() => Directory.CreateDirectory(directory), token);
        await File.WriteAllTextAsync(Path.Combine(directory, "request.json"), JsonSerializer.Serialize(new DeletionRequest(items[0], mode, items)), token);
        token.ThrowIfCancellationRequested();
        string executable = Path.ChangeExtension(typeof(App).Assembly.Location, ".exe");
        // Production helpers inherit the elevated application's token. Tests use their managed
        // host to exercise disposable fixtures without a UAC prompt for every worker process.
        var start = new ProcessStartInfo(useManagedTestHost ? "dotnet.exe" : executable)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true };
        if (useManagedTestHost) start.ArgumentList.Add(typeof(App).Assembly.Location);
        start.ArgumentList.Add("--delete-worker");
        start.ArgumentList.Add(directory);
        var process = Process.Start(start) ?? throw new IOException("Could not start the deletion worker.");
        return new DeletionJob(process, directory);
    }

    public async Task<DeletionOutcome> WaitAsync()
    {
        // Drain concurrently so a large result cannot fill the pipe and deadlock the worker.
        Task<string> response = _worker.StandardOutput.ReadToEndAsync();
        await _worker.WaitForExitAsync();
        string responseText = await response;
        if (!string.IsNullOrWhiteSpace(responseText))
        {
            try
            {
                var received = JsonSerializer.Deserialize<DeletionOutcome>(responseText);
                if (received is not null) return received;
            }
            catch (JsonException) { /* Fall back to the durable checkpoint. */ }
        }
        string result = Path.Combine(JournalPath, "result.json");
        if (!File.Exists(result)) return new(DeletionStatus.Unknown,
            "The deletion worker stopped without a confirmed result. Refresh the search before taking another action.");
        var outcome = JsonSerializer.Deserialize<DeletionOutcome>(await File.ReadAllTextAsync(result));
        if (outcome is null || outcome.Status == DeletionStatus.Unknown)
        {
            outcome = new(DeletionStatus.Unknown, "The worker exited without confirming the outcome. Refresh the search before another deletion.", outcome?.Items, outcome?.Counts);
            try { await WriteOutcomeAsync(JournalPath, outcome); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Still return known outcomes. */ }
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
            outcome = await ExecuteRecordedAsync(request, cancel.Token, value => WriteOutcomeAsync(directory, value));
        }
        catch (OperationCanceledException)
        { outcome = new(DeletionStatus.Cancelled, "Stopped at a safe boundary. Some items may already have been processed; refresh the search."); }
        catch (Exception ex) { outcome = new(DeletionStatus.Failed, ex.Message); }
        // The response channel preserves known results even if the journal volume is unavailable.
        try { await Console.Out.WriteLineAsync(JsonSerializer.Serialize(outcome)); }
        catch (IOException) { /* Parent closed; the durable result was already attempted. */ }
    }

    public static async Task<DeletionOutcome> ExecuteRecordedAsync(DeletionRequest request, CancellationToken token,
        Func<DeletionOutcome, Task> write)
    {
        DeletionOutcome lastKnown = new(DeletionStatus.Unknown, "Operation in progress; its outcome is not confirmed.", []);
        DeletionOutcome outcome;
        try
        {
            // No deletion begins unless the initial journal record was successfully saved.
            await write(lastKnown);
            outcome = await BatchDeletion.ExecuteAsync(request.Items ?? [request.Item], request.Mode, token, async partial =>
            {
                lastKnown = partial; // Update memory before attempting the fallible write.
                await write(partial);
            });
        }
        catch (OperationCanceledException) { outcome = new(DeletionStatus.Cancelled, "Stopped before the next operation.", lastKnown.Items); }
        catch (Exception ex) { outcome = new(DeletionStatus.Failed, ex.Message, lastKnown.Items); }
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try { await write(outcome); return outcome; }
            catch (Exception ex)
            {
                if (attempt == 2)
                    return outcome with { Status = DeletionStatus.Failed, Message = outcome.Message + " History could not be saved: " + ex.Message };
                await Task.Delay(50 * (attempt + 1));
            }
        }
        return outcome;
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
