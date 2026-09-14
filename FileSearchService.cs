using System.IO;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace LumaSearch;

public static class FileSearchService
{
    public static Regex CreateNameMatcher(string name, NameMatchMode mode = NameMatchMode.Wildcard)
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
        return new Regex(@"\A" + expression + @"\z",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline |
            RegexOptions.NonBacktracking, TimeSpan.FromSeconds(2));
    }

    public static async Task ScanAsync(SearchOptions options, ChannelWriter<SearchResult> writer,
        ScanStatistics statistics, CancellationToken cancellationToken)
    {
        var enumerators = new Stack<IEnumerator<string>>();
        try
        {
            var matcher = CreateNameMatcher(options.NamePattern, options.MatchMode);
            if (options.MaxResults is < 1 or > 100_000) throw new ArgumentException("Result limit must be between 1 and 100,000.");
            cancellationToken.ThrowIfCancellationRequested();
            if ((File.GetAttributes(options.StartingDirectory) & FileAttributes.Directory) == 0)
                throw new ArgumentException("The starting path must be a directory.");
            int resultCount = 0;
            var textMatcher = string.IsNullOrEmpty(options.TargetText) ? null : new LineTextMatcher(options.TargetText);
            // Explicitly selected starting directories are searched even when hidden.
            enumerators.Push(OpenDirectory(options.StartingDirectory));
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
                            try { matches = await textMatcher.ContainsAsync(path, cancellationToken).ConfigureAwait(false); }
                            catch (Exception ex) when (IsFileSystemException(ex) || ex is DecoderFallbackException)
                            { statistics.Skip(); matches = false; }
                        }
                    }
                    if (matches)
                    {
                        SearchResult found;
                        try { found = SearchResult.Capture(path); }
                        catch (Exception ex) when (IsFileSystemException(ex))
                        { found = new SearchResult(Path.GetFileName(path), path, directory, link); }
                        await writer.WriteAsync(found, cancellationToken).ConfigureAwait(false);
                        if (++resultCount >= options.MaxResults) { statistics.ReachLimit(); return; }
                    }
                }

                // Depth-first iteration avoids call-stack overflow and never follows junctions/symlinks.
                if (directory && !link)
                {
                    try { enumerators.Push(OpenDirectory(path)); }
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

    private static IEnumerator<string> OpenDirectory(string path) =>
        Directory.EnumerateFileSystemEntries(path, "*", new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = false,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false
        }).GetEnumerator();

    public static bool IsFileSystemException(Exception exception) => exception is
        IOException or UnauthorizedAccessException or SecurityException or NotSupportedException;

    // KMP matching across bounded reader buffers scans each line sequentially without ever
    // allocating an entire file or an unbounded line. A newline resets the match state.
    private sealed class LineTextMatcher
    {
        private readonly char[] _pattern;
        private readonly int[] _prefix;

        public LineTextMatcher(string target)
        {
            if (target.Contains('\r') || target.Contains('\n'))
                throw new ArgumentException("Search text must fit on a single line.");
            _pattern = target.Select(char.ToUpperInvariant).ToArray();
            _prefix = new int[_pattern.Length];
            for (int i = 1, j = 0; i < _pattern.Length; i++)
            {
                while (j > 0 && _pattern[i] != _pattern[j]) j = _prefix[j - 1];
                if (_pattern[i] == _pattern[j]) j++;
                _prefix[i] = j;
            }
        }

        public async Task<bool> ContainsAsync(string path, CancellationToken cancellationToken)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 16 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous);
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true),
                detectEncodingFromByteOrderMarks: true, bufferSize: 16 * 1024);
            char[] buffer = new char[8192];
            int matched = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (count == 0) return false;
                for (int i = 0; i < count; i++)
                {
                    char value = buffer[i];
                    if (value is '\r' or '\n') { matched = 0; continue; }
                    value = char.ToUpperInvariant(value);
                    while (matched > 0 && value != _pattern[matched]) matched = _prefix[matched - 1];
                    if (value == _pattern[matched]) matched++;
                    if (matched == _pattern.Length) return true;
                }
            }
        }
    }
}
