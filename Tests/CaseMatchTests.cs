using System.IO;
using System.Text;
using System.Threading.Channels;
using LumaSearch;

internal static class CaseMatchTests
{
    internal static async Task RunAsync(string root, Action<bool, string> check)
    {
        string fixtures = Path.Combine(root, "case-matching");
        Directory.CreateDirectory(fixtures);
        File.WriteAllText(Path.Combine(fixtures, "Report.fixture"), "Report payload");
        Directory.CreateDirectory(Path.Combine(fixtures, "ReportFolder"));

        async Task<SearchResult[]> Scan(string name, string text = "", bool matchCase = false,
            NameMatchMode mode = NameMatchMode.Exact, bool folders = false)
        {
            var channel = Channel.CreateBounded<SearchResult>(2);
            var options = new SearchOptions(fixtures, name, text, !folders, folders, true, mode, MatchCase: matchCase);
            var worker = Task.Run(() => FileSearchService.ScanAsync(options, channel.Writer, new ScanStatistics(), CancellationToken.None));
            var results = new List<SearchResult>();
            await foreach (var result in channel.Reader.ReadAllAsync()) results.Add(result);
            await worker;
            return results.ToArray();
        }

        foreach (bool folders in new[] { false, true })
        foreach (var mode in Enum.GetValues<NameMatchMode>())
        {
            string name = mode switch { NameMatchMode.Contains => "Report", NameMatchMode.Wildcard => "Report*", _ => folders ? "ReportFolder" : "Report.fixture" };
            check((await Scan(name, matchCase: true, mode: mode, folders: folders)).Length == 1
                && (await Scan(name.ToLowerInvariant(), matchCase: true, mode: mode, folders: folders)).Length == 0,
                $"match case distinguishes {mode} {(folders ? "folder" : "file")} names");
            check((await Scan(name.ToLowerInvariant(), mode: mode, folders: folders)).Length == 1,
                $"default {mode} {(folders ? "folder" : "file")} matching still ignores case");
        }
        check(Enum.GetValues<NameMatchMode>().All(mode => FileSearchService.CreateNameMatcher("", mode, matchCase: true).IsMatch("Any.Name")),
            "blank names continue to match all names with match case enabled");

        File.WriteAllText(Path.Combine(fixtures, "Upper.fixture"), "Needle");
        File.WriteAllText(Path.Combine(fixtures, "Lower.fixture"), "needle");
        check((await Scan("*", "Needle", matchCase: true, mode: NameMatchMode.Wildcard)).Single().Name == "Upper.fixture"
            && (await Scan("*", "needle", matchCase: true, mode: NameMatchMode.Wildcard)).Single().Name == "Lower.fixture",
            "match case distinguishes uppercase and lowercase text inside files");
        check((await Scan("*", "Needle", mode: NameMatchMode.Wildcard)).Length == 2,
            "text searches still ignore case by default");
        check((await Scan("upper.fixture", "Needle", matchCase: true)).Length == 0
            && (await Scan("Upper.fixture", "needle", matchCase: true)).Length == 0,
            "combined searches require both the name and content to match case");

        foreach (var encoding in new[] { ("Utf8.fixture", Encoding.UTF8), ("Utf16.fixture", Encoding.Unicode), ("Utf32.fixture", Encoding.UTF32) })
            File.WriteAllText(Path.Combine(fixtures, encoding.Item1), "Café", encoding.Item2);
        check((await Scan("Utf*", "Café", matchCase: true, mode: NameMatchMode.Wildcard)).Length == 3
            && (await Scan("Utf*", "café", matchCase: true, mode: NameMatchMode.Wildcard)).Length == 0,
            "case-sensitive Unicode text works in UTF-8, UTF-16 and UTF-32 files");
        File.WriteAllText(Path.Combine(fixtures, "Résumé.fixture"), "Unicode name");
        check((await Scan("Résumé.fixture", matchCase: true)).Length == 1 && (await Scan("résumé.fixture", matchCase: true)).Length == 0,
            "match case preserves accented filename distinctions");

        File.WriteAllText(Path.Combine(fixtures, "Boundary.fixture"), new string('x', 8190) + "AbAbAC");
        check((await Scan("Boundary.fixture", "AbAC", matchCase: true)).Length == 1
            && (await Scan("Boundary.fixture", "abac", matchCase: true)).Length == 0
            && (await Scan("Boundary.fixture", "abac")).Length == 1,
            "case-aware text matching handles overlapping prefixes across reader buffers");
        File.WriteAllText(Path.Combine(fixtures, "Split.fixture"), "Nee\ndle");
        check((await Scan("Split.fixture", "Needle", matchCase: true)).Length == 0
            && (await Scan("Split.fixture", "needle")).Length == 0,
            "both case modes keep text matches within a single line");
    }
}
