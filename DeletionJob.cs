using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace LumaSearch;

public sealed record DeletionRequest(SearchResult Item, DeletionMode Mode, SearchResult[]? Items = null, string[]? RecoveryRecords = null,
    bool EmptyRecovery = false);

public sealed class DeletionJob : IDisposable
{
    private readonly Process _worker;
    private readonly Task<string> _response;
    private Task _delivery = Task.CompletedTask;
    public string JournalPath { get; }
    public static string JournalRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LumaSearch", "Operations");
    private DeletionJob(Process worker, string journalPath)
    {
        _worker = worker; JournalPath = journalPath;
        // Start draining immediately, including when the caller closes before WaitAsync.
        _response = worker.StandardOutput.ReadToEndAsync();
        ReadOnlyWork.Observe(_response);
    }

    public static Task<DeletionJob> StartAsync(SearchResult item, DeletionMode mode, CancellationToken token,
        string? journalRoot = null, bool useManagedTestHost = false) => StartAsync([item], mode, token, journalRoot, useManagedTestHost);

    public static async Task<DeletionJob> StartAsync(SearchResult[] selection, DeletionMode mode, CancellationToken token,
        string? journalRoot = null, bool useManagedTestHost = false, string[]? recoveryRecords = null, bool emptyRecovery = false)
    {
        var items = recoveryRecords is null ? BatchDeletion.Plan(selection) : selection.ToArray();
        if (items.Length == 0) throw new ArgumentException("Select at least one item.");
        if (recoveryRecords is not null && recoveryRecords.Length != items.Length) throw new ArgumentException("Recovery selections must match their records.");
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (emptyRecovery && (recoveryRecords is null || mode != DeletionMode.Permanent))
            throw new ArgumentException("Emptying saved items requires recovery records and permanent deletion mode.");
        string request = JsonSerializer.Serialize(new DeletionRequest(items[0], mode, items, recoveryRecords, emptyRecovery));
        string directory = Path.Combine(journalRoot ?? JournalRoot, Guid.NewGuid().ToString("N"));
        string preparing = directory + ".preparing";
        // Preparation is never executable; only a committed request delivered through the pipe can start work.
        await Task.Run(() => Directory.CreateDirectory(preparing), token);
        await WriteOutcomeAsync(preparing, new(DeletionStatus.NotStarted, "Deletion has not started.",
            items.Select(item => new ItemDeletionOutcome(item, DeletionStatus.NotStarted, "Not started.")).ToArray()));
        token.ThrowIfCancellationRequested();
        await Task.Run(() => Directory.Move(preparing, directory), token);
        token.ThrowIfCancellationRequested();
        string executable = Path.ChangeExtension(typeof(App).Assembly.Location, ".exe");
        // Inherited anonymous pipes carry the approved request. Files in the journal
        // directory are history only and are never treated as executable instructions.
        var start = new ProcessStartInfo(useManagedTestHost ? "dotnet.exe" : executable)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true };
        if (useManagedTestHost) start.ArgumentList.Add(typeof(App).Assembly.Location);
        start.ArgumentList.Add("--delete-worker");
        start.ArgumentList.Add(directory);
        var process = Process.Start(start) ?? throw new IOException("Could not start the deletion worker.");
        var job = new DeletionJob(process, directory);
        // Once a helper exists, always return its job: cancellation must be reported
        // from its actual outcome, never as an assumed pre-launch cancellation.
        job._delivery = job.DeliverRequestAsync(request, token);
        ReadOnlyWork.Observe(job._delivery);
        return job;
    }

    private async Task DeliverRequestAsync(string request, CancellationToken token)
    {
        using var registration = token.Register(RequestCancel);
        try
        {
            token.ThrowIfCancellationRequested();
            await _worker.StandardInput.WriteLineAsync(request);
            await _worker.StandardInput.WriteLineAsync("START");
            await _worker.StandardInput.FlushAsync();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
        { RequestCancel(); }
    }

    public async Task<DeletionOutcome> WaitAsync()
    {
        // Drain concurrently so a large result cannot fill the pipe and deadlock the worker.
        await _worker.WaitForExitAsync();
        await _delivery;
        string responseText = await _response;
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
        DeletionOutcome? outcome;
        try { outcome = JsonSerializer.Deserialize<DeletionOutcome>(await File.ReadAllTextAsync(result)); }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        { return new(DeletionStatus.Unknown, "The worker outcome could not be read. Refresh the search before another deletion. " + ex.Message); }
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
        // Close the pipe itself; StreamWriter.Close is not safe while an async write
        // is delivering a large request. EOF is also the worker's cancellation signal.
        try { _worker.StandardInput.BaseStream.Close(); }
        catch (InvalidOperationException) { } catch (IOException) { }
    }

    public static async Task RunWorkerAsync(string directory)
    {
        using var cancel = new CancellationTokenSource();
        DeletionOutcome outcome;
        bool accepted = false;
        try
        {
            string? input = await Console.In.ReadLineAsync();
            var request = input is null ? null : JsonSerializer.Deserialize<DeletionRequest>(input);
            request = request
                ?? throw new IOException("Invalid deletion request.");
            if (await Console.In.ReadLineAsync() != "START") throw new IOException("Deletion request was not committed.");
            accepted = true;
            _ = Task.Run(async () =>
            {
                try { await Console.In.ReadLineAsync(); cancel.Cancel(); }
                catch (IOException) { cancel.Cancel(); }
                catch (ObjectDisposedException) { }
            });
            outcome = await ExecuteRecordedAsync(request, cancel.Token, value => WriteOutcomeAsync(directory, value));
        }
        catch (OperationCanceledException)
        { outcome = new(DeletionStatus.Cancelled, "Stopped at a safe boundary. Some items may already have been processed; refresh the search."); }
        catch (Exception ex) { outcome = new(accepted ? DeletionStatus.Failed : DeletionStatus.NotStarted,
            accepted ? ex.Message : "Deletion did not start. " + ex.Message); }
        if (!accepted)
        {
            try { await WriteOutcomeAsync(directory, outcome); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
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
            async Task Checkpoint(DeletionOutcome partial)
            {
                lastKnown = partial; // Update memory before attempting the fallible write.
                await write(partial);
            }
            outcome = request.EmptyRecovery
                ? await new RecoveryStorage().EmptyBatchAsync(request, token, Checkpoint)
                : request.RecoveryRecords is null
                ? await BatchDeletion.ExecuteAsync(request.Items ?? [request.Item], request.Mode, token, Checkpoint)
                : await new RecoveryStorage().RestoreBatchAsync(request, token, Checkpoint);
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

    public static Task<string?> GetPreviousOutcomeAsync() => GetPreviousOutcomeAsync(JournalRoot);

    public static async Task<string?> GetPreviousOutcomeAsync(string journalRoot)
    {
        if (!Directory.Exists(journalRoot)) return null;
        var directories = Directory.EnumerateDirectories(journalRoot).OrderByDescending(Directory.GetCreationTimeUtc).ToArray();
        var unresolved = new List<string>();
        string? latest = null;
        foreach (string directory in directories)
        {
            DeletionOutcome outcome;
            try
            {
                string result = Path.Combine(directory, "result.json");
                outcome = File.Exists(result)
                    ? JsonSerializer.Deserialize<DeletionOutcome>(await File.ReadAllTextAsync(result))
                        ?? new(DeletionStatus.Unknown, "The saved outcome is empty.")
                    : directory.EndsWith(".preparing", StringComparison.Ordinal)
                        ? new(DeletionStatus.NotStarted, "Preparation did not finish; no deletion started.")
                        : new(DeletionStatus.Unknown, "No confirmed outcome was saved.");
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            { outcome = new(DeletionStatus.Unknown, "The saved outcome could not be read: " + ex.Message); }
            if (outcome.Items is { Length: > 0 } pendingItems && pendingItems.Any(item => item.Status == DeletionStatus.Pending))
            {
                var checkedItems = pendingItems.Select(RecheckPending).ToArray();
                outcome = BatchDeletion.Summarize(outcome.Counts?.Total ?? checkedItems.Length, checkedItems, DeletionMode.Permanent);
                if (checkedItems.Any(item => item.Status == DeletionStatus.Pending && item.RecoveryCleanup is not null))
                    outcome = outcome with { Message = outcome.Message + " Saved-item cleanup is unfinished. Use Empty saved items… to finish it." };
            }
            latest ??= outcome.Message;
            if (outcome.Status is DeletionStatus.Unknown or DeletionStatus.Pending ||
                outcome.Items?.Any(item => item.Status is DeletionStatus.Unknown or DeletionStatus.Pending) == true)
            {
                string target = outcome.Items?.FirstOrDefault()?.Item.FullPath ?? "Operation " + Path.GetFileName(directory);
                unresolved.Add(target + ": " + outcome.Message);
            }
        }
        if (unresolved.Count > 0)
            return $"{unresolved.Count:N0} previous deletion operations need confirmation. Refresh their locations before deleting again.\n"
                + string.Join("\n", unresolved.Take(10))
                + (unresolved.Count > 10 ? $"\n{unresolved.Count - 10:N0} more operations remain recorded in history." : "");
        return latest is null ? null : "Previous deletion: " + latest;
    }

    private static ItemDeletionOutcome RecheckPending(ItemDeletionOutcome item)
    {
        if (item.Status != DeletionStatus.Pending || !FileDeletionService.IsDefinitelyMissing(item.Item.FullPath)) return item;
        if (item.RecoveryCleanup is { } cleanup &&
            (!FileDeletionService.IsDefinitelyMissing(cleanup.RecordPath) ||
             (cleanup.MetadataPath is not null && !FileDeletionService.IsDefinitelyMissing(cleanup.MetadataPath))))
            return item;
        return item with { Status = DeletionStatus.AlreadyMissing, Message = item.RecoveryCleanup is null
            ? "The previously pending target is no longer at its recorded location."
            : "The saved item, recovery record, and recorded Recycle Bin metadata have been removed." };
    }
    public void Dispose()
    {
        RequestCancel();
        // Keep the process streams alive until any in-flight request delivery has stopped.
        ReadOnlyWork.Observe(Task.WhenAll(_delivery, _response).ContinueWith(_ => _worker.Dispose(), TaskScheduler.Default));
    }
}
