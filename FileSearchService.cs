using System.IO;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;

namespace LumaSearch;

public static class FileSearchService
{
    public static Regex CreateNameMatcher(string name, NameMatchMode mode = NameMatchMode.Wildcard, bool matchCase = false)
    {
        if (name.Length > 1024)
            throw new ArgumentException("The name pattern must be 1,024 characters or fewer.");
        string escaped = Regex.Escape(name);
        string expression = mode switch
        {
            NameMatchMode.Contains => ".*" + escaped + ".*",
            NameMatchMode.Exact => escaped,
            NameMatchMode.Wildcard => escaped.Replace(@"\*", ".*").Replace(@"\?", "."),
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
        // A blank field intentionally lists every name in all three modes.
        if (name.Length == 0) expression = ".*";
        var flags = RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.NonBacktracking;
        if (!matchCase) flags |= RegexOptions.IgnoreCase;
        return new Regex(@"\A" + expression + @"\z", flags, TimeSpan.FromSeconds(2));
    }

    public static Task ScanAsync(SearchOptions options, ChannelWriter<SearchResult> writer,
        ScanStatistics statistics, CancellationToken cancellationToken) => ScanAsync(options, writer, statistics, cancellationToken, null);

    internal static async Task ScanAsync(SearchOptions options, ChannelWriter<SearchResult> writer,
        ScanStatistics statistics, CancellationToken cancellationToken, Action<SearchResult>? contentOpened)
    {
        var enumerators = new Stack<DirectoryCursor>();
        try
        {
            var matcher = CreateNameMatcher(options.NamePattern, options.MatchMode, options.MatchCase);
            if (options.MaxResults is < 1 or > 100_000) throw new ArgumentException("Result limit must be between 1 and 100,000.");
            cancellationToken.ThrowIfCancellationRequested();
            var root = FileIdentityService.CaptureForSearch(options.StartingDirectory);
            if (!root.IsDirectory || root.IsLink)
                throw new ArgumentException("Choose a physical starting directory; linked directories are not followed.");
            // Keep every path component stable for the lifetime of the traversal.
            using var pinnedRoot = PinnedPath.Open(root, deleteAccess: false, requireIdentity: false);
            int resultCount = 0;
            var textMatcher = string.IsNullOrEmpty(options.TargetText) ? null : new LineTextMatcher(options.TargetText, options.MatchCase);
            // Explicitly selected starting directories are searched even when hidden.
            enumerators.Push(DirectoryCursor.Open(root.FullPath, root));
            while (enumerators.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string path;
                try
                {
                    if (!enumerators.Peek().MoveNext())
                    {
                        enumerators.Pop().Dispose();
                        continue;
                    }
                    path = enumerators.Peek().Current;
                }
                catch (Exception ex) when (IsFileSystemException(ex))
                {
                    statistics.Skip();
                    enumerators.Pop().Dispose();
                    continue;
                }

                FileAttributes attributes;
                try { attributes = File.GetAttributes(path); }
                catch (Exception ex) when (IsFileSystemException(ex)) { statistics.Skip(); continue; }
                statistics.Visit();
                if (!options.IncludeHidden && (attributes & FileAttributes.Hidden) != 0) continue;

                bool directory = (attributes & FileAttributes.Directory) != 0;
                bool link = (attributes & FileAttributes.ReparsePoint) != 0;
                bool requested = directory ? options.SearchFolders : options.SearchFiles;
                if (requested && matcher.IsMatch(Path.GetFileName(path)))
                {
                    bool matches = true;
                    if (!directory && textMatcher is not null)
                    {
                        // Do not open linked files, cloud placeholders or device-like reparse targets.
                        if (link) { statistics.Skip(); matches = false; }
                        else
                        {
                            try
                            {
                                var contentMatch = await textMatcher.MatchAsync(path, cancellationToken, contentOpened).ConfigureAwait(false);
                                if (contentMatch is not null)
                                {
                                    await writer.WriteAsync(contentMatch, cancellationToken).ConfigureAwait(false);
                                    if (++resultCount >= options.MaxResults) { statistics.ReachLimit(); return; }
                                }
                                // The content branch has already emitted the identity belonging to its read handle.
                                matches = false;
                            }
                            catch (Exception ex) when (IsFileSystemException(ex) || ex is DecoderFallbackException)
                            { statistics.Skip(); matches = false; }
                        }
                    }
                    if (matches)
                    {
                        SearchResult found;
                        try { found = FileIdentityService.CaptureForSearch(path); }
                        catch (Exception ex) when (IsFileSystemException(ex))
                        { found = new SearchResult(Path.GetFileName(path), path, directory, link, LastWriteTimeUtc: TryGetModifiedTime(path)); }
                        if (found.IsDirectory != directory || found.IsLink != link) { statistics.Skip(); continue; }
                        await writer.WriteAsync(found, cancellationToken).ConfigureAwait(false);
                        if (++resultCount >= options.MaxResults) { statistics.ReachLimit(); return; }
                    }
                }

                // Depth-first iteration avoids call-stack overflow and never follows junctions/symlinks.
                if (directory && !link)
                {
                    try { enumerators.Push(DirectoryCursor.Open(path)); }
                    catch (Exception ex) when (IsFileSystemException(ex)) { statistics.Skip(); }
                }
            }
        }
        finally
        {
            while (enumerators.Count > 0) enumerators.Pop().Dispose();
            writer.TryComplete();
        }
    }

    // The no-follow handle is retained until enumeration ends. Denying write and delete
    // sharing prevents both replacement and conversion of this directory to a junction.
    private sealed class DirectoryCursor : IDisposable
    {
        private readonly SafeFileHandle _handle;
        private readonly IEnumerator<string> _entries;
        private DirectoryCursor(SafeFileHandle handle, IEnumerator<string> entries)
        { _handle = handle; _entries = entries; }
        internal string Current => _entries.Current;
        internal bool MoveNext() => _entries.MoveNext();
        internal static DirectoryCursor Open(string path, SearchResult? expected = null)
        {
            var handle = FileIdentityService.Open(path, pin: true, denyWrite: true);
            try
            {
                var current = FileIdentityService.Read(handle, path, requireIdentity: false);
                if (!current.IsDirectory || current.IsLink) throw new IOException("The directory changed or became a link during the search.");
                if (expected?.Identity is not null) FileIdentityService.EnsureSame(expected, current);
                var entries = Directory.EnumerateFileSystemEntries(path, "*", new EnumerationOptions
                { RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0, ReturnSpecialDirectories = false }).GetEnumerator();
                return new(handle, entries);
            }
            catch { handle.Dispose(); throw; }
        }
        public void Dispose() { _entries.Dispose(); _handle.Dispose(); }
    }

    private static DateTime? TryGetModifiedTime(string path)
    {
        try
        {
            DateTime value = File.GetLastWriteTimeUtc(path);
            return value == DateTime.FromFileTimeUtc(0) ? null : value;
        }
        catch (Exception ex) when (IsFileSystemException(ex)) { return null; }
    }

    public static bool IsFileSystemException(Exception exception) => exception is
        IOException or UnauthorizedAccessException or SecurityException or NotSupportedException;

    // KMP matching across bounded reader buffers scans each line sequentially without ever
    // allocating an entire file or an unbounded line. A newline resets the match state.
    private sealed class LineTextMatcher
    {
        private readonly char[] _pattern;
        private readonly int[] _prefix;
        private readonly bool _matchCase;

        public LineTextMatcher(string target, bool matchCase)
        {
            if (target.Contains('\r') || target.Contains('\n'))
                throw new ArgumentException("Search text must fit on a single line.");
            _matchCase = matchCase;
            _pattern = target.Select(Normalize).ToArray();
            _prefix = new int[_pattern.Length];
            for (int i = 1, j = 0; i < _pattern.Length; i++)
            {
                while (j > 0 && _pattern[i] != _pattern[j]) j = _prefix[j - 1];
                if (_pattern[i] == _pattern[j]) j++;
                _prefix[i] = j;
            }
        }

        private char Normalize(char value) => _matchCase ? value : char.ToUpperInvariant(value);

        public async Task<SearchResult?> MatchAsync(string path, CancellationToken cancellationToken, Action<SearchResult>? contentOpened)
        {
            using var handle = FileIdentityService.Open(path, pin: true, readData: true, denyWrite: true);
            var result = FileIdentityService.Read(handle, path, requireIdentity: false);
            if (result.IsDirectory || result.IsLink) throw new IOException("The file changed or became a link during the search.");
            contentOpened?.Invoke(result);
            using var stream = new FileStream(handle, FileAccess.Read, 16 * 1024, isAsync: true);
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true),
                detectEncodingFromByteOrderMarks: true, bufferSize: 16 * 1024);
            char[] buffer = new char[8192];
            int matched = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (count == 0) return null;
                for (int i = 0; i < count; i++)
                {
                    char value = buffer[i];
                    if (value is '\r' or '\n') { matched = 0; continue; }
                    value = Normalize(value);
                    while (matched > 0 && value != _pattern[matched]) matched = _prefix[matched - 1];
                    if (value == _pattern[matched]) matched++;
                    if (matched == _pattern.Length) return result;
                }
            }
        }
    }
}
