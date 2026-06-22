using System.Text;
using Yagu.Models;
using Yagu.Services;

namespace Yagu.Tests;

// ════════════════════════════════════════════════════════════════════════════
//  Support types for the data-driven "semantic" search query test suite.
//
//  Each SearchScenario fully specifies a synthetic corpus (directories + files),
//  the search options to apply, and the expected outcome. Scenarios are built
//  with the fluent ScenarioBuilder DSL and executed by SearchScenarioRunner
//  against the real SearchService pipeline.
//
//  "Semantic" here means behavior-focused query tests over meaningful corpora —
//  not the local-AI translation feature. The full options matrix (search modes,
//  regex, case, whole-word, include/exclude glob+regex, size, dates, depth,
//  max-results, skip-extensions, binary, hidden) is exercised across the catalog.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>A single synthetic file to materialize on disk for a scenario.</summary>
public sealed class CorpusFile
{
    public required string RelativePath { get; init; }
    public string? Text { get; init; }
    public byte[]? Bytes { get; init; }
    public DateTimeOffset? Created { get; init; }
    public DateTimeOffset? Modified { get; init; }

    /// <summary>If &gt; 0, pad the file (on a trailing line of spaces) up to at least this many bytes.</summary>
    public long MinBytes { get; init; }

    public bool Hidden { get; init; }
}

/// <summary>Expected outcome assertions for a scenario. Only the set values are asserted.</summary>
public sealed class ExpectedOutcome
{
    /// <summary>Exact set of matched files (relative '/'-normalized paths), order-independent.</summary>
    public IReadOnlyList<string>? Files { get; init; }

    /// <summary>When true, expect zero matches at all.</summary>
    public bool NoMatches { get; init; }

    /// <summary>Exact total number of match rows across every file.</summary>
    public int? TotalMatches { get; init; }

    /// <summary>Per-file expected match-row count (relative '/'-normalized path -&gt; count).</summary>
    public IReadOnlyDictionary<string, int>? MatchesPerFile { get; init; }

    /// <summary>Each listed substring must equal the matched text of some result.</summary>
    public IReadOnlyList<string>? MatchTexts { get; init; }

    /// <summary>Each listed file must be present among matched files (subset check).</summary>
    public IReadOnlyList<string>? ContainsFiles { get; init; }

    /// <summary>None of the listed files may appear among matched files.</summary>
    public IReadOnlyList<string>? ExcludesFiles { get; init; }
}

/// <summary>A fully specified, runnable search scenario.</summary>
public sealed class SearchScenario
{
    public required string Name { get; init; }
    public required IReadOnlyList<CorpusFile> Files { get; init; }
    public required Func<string, SearchOptions> OptionsFactory { get; init; }
    public required ExpectedOutcome Expected { get; init; }
}

/// <summary>Fluent builder for <see cref="SearchScenario"/>.</summary>
public sealed class ScenarioBuilder
{
    private readonly string _name;
    private readonly List<CorpusFile> _files = new();

    private string _query = string.Empty;
    private bool _useRegex;
    private bool _exactMatch; // default false => substring/multi-term
    private bool _caseSensitive;
    private SearchMode _mode = SearchMode.Content;

    private List<string> _include = new();
    private List<string> _exclude = new();
    private FilterPatternMode _includeMode = FilterPatternMode.GlobPath;
    private FilterPatternMode _excludeMode = FilterPatternMode.GlobPath;

    private long _minSize;
    private long _maxSize;
    private DateTimeOffset? _createdAfter;
    private DateTimeOffset? _createdBefore;
    private DateTimeOffset? _modifiedAfter;
    private DateTimeOffset? _modifiedBefore;

    private int _maxResults; // 0 = unlimited (avoids default 50k clamp surprises)
    private int _maxMatchesPerFile;
    private int _maxDepth;
    private bool _skipBinary = true;
    private bool _searchHidden = true;
    private int _context;
    private HashSet<string> _skipExtensions = new(StringComparer.OrdinalIgnoreCase);
    private bool _searchInsideArchives;
    private HashSet<string> _archiveExtensions = new(StringComparer.OrdinalIgnoreCase);
    private bool _obeyGitignore;

    private ExpectedOutcome _expected = new();

    public ScenarioBuilder(string name) => _name = name;

    // ── Corpus ──────────────────────────────────────────────────────────────
    public ScenarioBuilder File(string relativePath, string text)
    {
        _files.Add(new CorpusFile { RelativePath = relativePath, Text = text });
        return this;
    }

    public ScenarioBuilder FileAt(string relativePath, string text, DateTimeOffset? created = null, DateTimeOffset? modified = null)
    {
        _files.Add(new CorpusFile { RelativePath = relativePath, Text = text, Created = created, Modified = modified });
        return this;
    }

    public ScenarioBuilder PaddedFile(string relativePath, string text, long minBytes)
    {
        _files.Add(new CorpusFile { RelativePath = relativePath, Text = text, MinBytes = minBytes });
        return this;
    }

    public ScenarioBuilder HiddenFile(string relativePath, string text)
    {
        _files.Add(new CorpusFile { RelativePath = relativePath, Text = text, Hidden = true });
        return this;
    }

    public ScenarioBuilder BinaryFile(string relativePath, params byte[] bytes)
    {
        _files.Add(new CorpusFile { RelativePath = relativePath, Bytes = bytes });
        return this;
    }

    // ── Query / options ─────────────────────────────────────────────────────
    public ScenarioBuilder Query(string q) { _query = q; return this; }
    public ScenarioBuilder Regex(string q) { _query = q; _useRegex = true; return this; }
    public ScenarioBuilder Substring() { _exactMatch = false; _useRegex = false; return this; }
    public ScenarioBuilder WholeWord() { _exactMatch = true; _useRegex = false; return this; }
    public ScenarioBuilder CaseSensitive(bool on = true) { _caseSensitive = on; return this; }
    public ScenarioBuilder Mode(SearchMode m) { _mode = m; return this; }

    public ScenarioBuilder Include(params string[] globs) { _include = globs.ToList(); _includeMode = FilterPatternMode.GlobPath; return this; }
    public ScenarioBuilder Exclude(params string[] globs) { _exclude = globs.ToList(); _excludeMode = FilterPatternMode.GlobPath; return this; }
    public ScenarioBuilder IncludeRegex(params string[] regexes) { _include = regexes.ToList(); _includeMode = FilterPatternMode.Regex; return this; }
    public ScenarioBuilder ExcludeRegex(params string[] regexes) { _exclude = regexes.ToList(); _excludeMode = FilterPatternMode.Regex; return this; }

    public ScenarioBuilder MinSize(long bytes) { _minSize = bytes; return this; }
    public ScenarioBuilder MaxSize(long bytes) { _maxSize = bytes; return this; }
    public ScenarioBuilder ModifiedAfter(DateTimeOffset d) { _modifiedAfter = d; return this; }
    public ScenarioBuilder ModifiedBefore(DateTimeOffset d) { _modifiedBefore = d; return this; }
    public ScenarioBuilder CreatedAfter(DateTimeOffset d) { _createdAfter = d; return this; }
    public ScenarioBuilder CreatedBefore(DateTimeOffset d) { _createdBefore = d; return this; }

    public ScenarioBuilder MaxResults(int n) { _maxResults = n; return this; }
    public ScenarioBuilder MaxMatchesPerFile(int n) { _maxMatchesPerFile = n; return this; }
    public ScenarioBuilder MaxDepth(int n) { _maxDepth = n; return this; }
    public ScenarioBuilder Context(int n) { _context = n; return this; }
    public ScenarioBuilder SearchBinary() { _skipBinary = false; return this; }
    public ScenarioBuilder NoHidden() { _searchHidden = false; return this; }
    public ScenarioBuilder SkipExtensions(params string[] exts) { _skipExtensions = new HashSet<string>(exts, StringComparer.OrdinalIgnoreCase); return this; }
    public ScenarioBuilder SearchArchives(params string[] archiveExts) { _searchInsideArchives = true; _archiveExtensions = new HashSet<string>(archiveExts, StringComparer.OrdinalIgnoreCase); return this; }
    public ScenarioBuilder ObeyGitignore() { _obeyGitignore = true; return this; }

    // ── Expectations ────────────────────────────────────────────────────────
    public ScenarioBuilder ExpectFiles(params string[] files) { _expected = Merge(_expected, files: Norm(files)); return this; }
    public ScenarioBuilder ExpectNoMatches() { _expected = Merge(_expected, noMatches: true); return this; }
    public ScenarioBuilder ExpectTotal(int total) { _expected = Merge(_expected, total: total); return this; }
    public ScenarioBuilder ExpectMatchesInFile(string file, int count)
    {
        var dict = new Dictionary<string, int>(_expected.MatchesPerFile ?? new Dictionary<string, int>());
        dict[NormOne(file)] = count;
        _expected = Merge(_expected, perFile: dict);
        return this;
    }
    public ScenarioBuilder ExpectMatchText(params string[] texts) { _expected = Merge(_expected, matchTexts: texts.ToList()); return this; }
    public ScenarioBuilder ExpectContains(params string[] files) { _expected = Merge(_expected, contains: Norm(files)); return this; }
    public ScenarioBuilder ExpectExcludes(params string[] files) { _expected = Merge(_expected, excludes: Norm(files)); return this; }

    public SearchScenario Build()
    {
        var name = _name;
        var query = _query;
        var useRegex = _useRegex;
        var exactMatch = _exactMatch;
        var caseSensitive = _caseSensitive;
        var mode = _mode;
        var include = _include.ToArray();
        var exclude = _exclude.ToArray();
        var includeMode = _includeMode;
        var excludeMode = _excludeMode;
        var minSize = _minSize;
        var maxSize = _maxSize;
        var createdAfter = _createdAfter;
        var createdBefore = _createdBefore;
        var modifiedAfter = _modifiedAfter;
        var modifiedBefore = _modifiedBefore;
        var maxResults = _maxResults;
        var maxMatchesPerFile = _maxMatchesPerFile;
        var maxDepth = _maxDepth;
        var skipBinary = _skipBinary;
        var searchHidden = _searchHidden;
        var context = _context;
        var skipExtensions = _skipExtensions;
        var searchInsideArchives = _searchInsideArchives;
        var archiveExtensions = _archiveExtensions;
        var obeyGitignore = _obeyGitignore;

        return new SearchScenario
        {
            Name = name,
            Files = _files.ToArray(),
            Expected = _expected,
            OptionsFactory = root => new SearchOptions
            {
                Directory = root,
                Query = query,
                UseRegex = useRegex,
                ExactMatch = exactMatch,
                CaseSensitive = caseSensitive,
                SearchMode = mode,
                IncludeGlobs = include,
                ExcludeGlobs = exclude,
                IncludeFilterMode = includeMode,
                ExcludeFilterMode = excludeMode,
                MinFileSizeBytes = minSize,
                MaxFileSizeBytes = maxSize,
                CreatedAfterDate = createdAfter,
                CreatedBeforeDate = createdBefore,
                ModifiedAfterDate = modifiedAfter,
                ModifiedBeforeDate = modifiedBefore,
                MaxResults = maxResults,
                MaxMatchesPerFile = maxMatchesPerFile,
                MaxSearchDepth = maxDepth,
                SkipBinary = skipBinary,
                SearchHiddenFiles = searchHidden,
                ContextLines = context,
                SkipExtensions = skipExtensions,
                SearchInsideArchives = searchInsideArchives,
                ArchiveExtensions = archiveExtensions,
                ObeyGitignore = obeyGitignore,
            },
        };
    }

    private static List<string> Norm(IEnumerable<string> files) => files.Select(NormOne).ToList();
    private static string NormOne(string f) => f.Replace('\\', '/');

    private static ExpectedOutcome Merge(
        ExpectedOutcome current,
        List<string>? files = null,
        bool? noMatches = null,
        int? total = null,
        IReadOnlyDictionary<string, int>? perFile = null,
        List<string>? matchTexts = null,
        List<string>? contains = null,
        List<string>? excludes = null)
        => new()
        {
            Files = files ?? current.Files,
            NoMatches = noMatches ?? current.NoMatches,
            TotalMatches = total ?? current.TotalMatches,
            MatchesPerFile = perFile ?? current.MatchesPerFile,
            MatchTexts = matchTexts ?? current.MatchTexts,
            ContainsFiles = contains ?? current.ContainsFiles,
            ExcludesFiles = excludes ?? current.ExcludesFiles,
        };
}

/// <summary>Materializes a scenario's corpus and runs the real search pipeline, asserting expectations.</summary>
public static class SearchScenarioRunner
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public static ScenarioBuilder Scn(string name) => new(name);

    public static async Task RunAsync(SearchScenario scenario)
    {
        string root = Path.Combine(Path.GetTempPath(), "yagu-sem-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Materialize(scenario, root);

            var opts = scenario.OptionsFactory(root);
            var results = await CollectAsync(opts);

            AssertExpectations(scenario, root, results);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    private static void Materialize(SearchScenario scenario, string root)
    {
        foreach (var file in scenario.Files)
        {
            string full = Path.Combine(root, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);

            if (file.Bytes is not null)
            {
                System.IO.File.WriteAllBytes(full, file.Bytes);
            }
            else
            {
                string text = file.Text ?? string.Empty;
                if (file.MinBytes > 0)
                {
                    int currentBytes = Utf8NoBom.GetByteCount(text);
                    if (currentBytes < file.MinBytes)
                    {
                        int pad = (int)(file.MinBytes - currentBytes) - 1; // -1 for the '\n'
                        if (pad < 0) pad = 0;
                        text = text + "\n" + new string(' ', pad);
                    }
                }
                System.IO.File.WriteAllText(full, text, Utf8NoBom);
            }

            if (file.Created is { } c) System.IO.File.SetCreationTimeUtc(full, c.UtcDateTime);
            if (file.Modified is { } m) System.IO.File.SetLastWriteTimeUtc(full, m.UtcDateTime);
            if (file.Hidden) System.IO.File.SetAttributes(full, System.IO.File.GetAttributes(full) | FileAttributes.Hidden);
        }
    }

    private static async Task<List<SearchResult>> CollectAsync(SearchOptions opts)
    {
        var results = new List<SearchResult>();
        var service = new SearchService();
        await foreach (var evt in service.SearchAsync(opts, default))
        {
            switch (evt)
            {
                case SearchEvent.Match m: results.Add(m.Result); break;
                case SearchEvent.MatchBatch b: results.AddRange(b.Results); break;
            }
        }
        return results;
    }

    private static void AssertExpectations(SearchScenario scenario, string root, List<SearchResult> results)
    {
        var matchedFiles = results
            .Select(r => Relative(root, r.FilePath))
            .Distinct()
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        var exp = scenario.Expected;
        string ctx = $"[{scenario.Name}] matched: {string.Join(", ", matchedFiles)}";

        if (exp.NoMatches)
        {
            Assert.True(results.Count == 0, $"{ctx} — expected no matches but got {results.Count}.");
        }

        if (exp.Files is not null)
        {
            var expected = exp.Files.OrderBy(s => s, StringComparer.Ordinal).ToList();
            Assert.True(expected.SequenceEqual(matchedFiles),
                $"{ctx} — expected files: {string.Join(", ", expected)}.");
        }

        if (exp.TotalMatches is { } total)
        {
            Assert.True(results.Count == total,
                $"{ctx} — expected total matches {total} but got {results.Count}.");
        }

        if (exp.MatchesPerFile is not null)
        {
            foreach (var (file, count) in exp.MatchesPerFile)
            {
                int actual = results.Count(r => Relative(root, r.FilePath) == file);
                Assert.True(actual == count,
                    $"{ctx} — expected {count} match(es) in '{file}' but got {actual}.");
            }
        }

        if (exp.ContainsFiles is not null)
        {
            foreach (var file in exp.ContainsFiles)
                Assert.True(matchedFiles.Contains(file), $"{ctx} — expected to contain '{file}'.");
        }

        if (exp.ExcludesFiles is not null)
        {
            foreach (var file in exp.ExcludesFiles)
                Assert.False(matchedFiles.Contains(file), $"{ctx} — expected NOT to contain '{file}'.");
        }

        if (exp.MatchTexts is not null)
        {
            foreach (var text in exp.MatchTexts)
            {
                bool found = results.Any(r => MatchedText(r) == text);
                Assert.True(found, $"{ctx} — expected a result whose matched text is '{text}'.");
            }
        }
    }

    private static string Relative(string root, string fullPath)
        => Path.GetRelativePath(root, fullPath).Replace('\\', '/');

    private static string MatchedText(SearchResult r)
    {
        if (r.LineNumber == 0) return string.Empty; // filename match — no line text
        if (r.MatchStartColumn < 0 || r.MatchLength <= 0) return string.Empty;
        if (r.MatchStartColumn + r.MatchLength > r.MatchLine.Length) return string.Empty;
        return r.MatchLine.Substring(r.MatchStartColumn, r.MatchLength);
    }
}
