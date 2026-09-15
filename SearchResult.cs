namespace LumaSearch;

public sealed record FileIdentity(ulong Volume, ulong IdLow, ulong IdHigh, long CreationTime);

public sealed record SearchResult(string Name, string FullPath, bool IsDirectory,
    bool IsLink = false, FileIdentity? Identity = null, DateTime? LastWriteTimeUtc = null)
{
    public DateTime? DateModified => LastWriteTimeUtc?.ToLocalTime();
    public string Type => IsLink ? (IsDirectory ? "Folder link" : "File link") : IsDirectory ? "Folder" : "File";
    public static SearchResult Capture(string path) => FileIdentityService.Capture(path);
}

public enum NameMatchMode { Contains, Exact, Wildcard }

public sealed record SearchOptions(string StartingDirectory, string NamePattern,
    string TargetText, bool SearchFiles, bool SearchFolders, bool IncludeHidden,
    NameMatchMode MatchMode = NameMatchMode.Wildcard, int MaxResults = 20_000);

public sealed class ScanStatistics
{
    private long _visited;
    private long _skipped;
    private int _limitReached;
    public bool LimitReached => Volatile.Read(ref _limitReached) != 0;
    internal void ReachLimit() => Interlocked.Exchange(ref _limitReached, 1);
    public long Visited => Interlocked.Read(ref _visited);
    public long Skipped => Interlocked.Read(ref _skipped);
    internal void Visit() => Interlocked.Increment(ref _visited);
    internal void Skip() => Interlocked.Increment(ref _skipped);
}
