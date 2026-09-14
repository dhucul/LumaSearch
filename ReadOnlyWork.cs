namespace LumaSearch;

public static class ReadOnlyWork
{
    // At most two OS calls may remain blocked after cancellation. Waiting jobs do not consume threads.
    private static readonly SemaphoreSlim Slots = new(2);
    public static Task<T> RunAsync<T>(Func<T> work, CancellationToken token) => RunAsync(() => Task.FromResult(work()), token);
    public static async Task<T> RunAsync<T>(Func<Task<T>> work, CancellationToken token)
    {
        await Slots.WaitAsync(token).ConfigureAwait(false);
        try { return await Task.Run(work, CancellationToken.None).ConfigureAwait(false); }
        finally { Slots.Release(); }
    }
    public static void Observe(Task task, CancellationTokenSource? owner = null)
    {
        _ = task.ContinueWith(completed => { _ = completed.Exception; owner?.Dispose(); },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
