using System.Globalization;
using System.Text.RegularExpressions;
using Yagu.Models;

namespace Yagu.Services.Ai;

/// <summary>
/// Writable surface a <see cref="ResolvedSearchPlan"/> can be applied to. Implemented by the
/// UI view-model so <see cref="SemanticPlanApplier.ApplyToTarget"/> can stay pure and unit-testable
/// against a fake target without constructing any WinUI types.
/// </summary>
public interface ISemanticPlanTarget
{
    string Directory { get; set; }
    string Query { get; set; }
    int SearchModeIndex { get; set; }
    bool CaseSensitive { get; set; }
    bool UseRegex { get; set; }
    bool ExactMatch { get; set; }
    string IncludeGlobs { get; set; }
    string ExcludeGlobs { get; set; }
    int IncludeFilterModeIndex { get; set; }
    int ExcludeFilterModeIndex { get; set; }
    long MinFileSizeBytes { get; set; }
    long MaxFileSizeBytes { get; set; }
    DateTimeOffset? CreatedAfterDate { get; set; }
    DateTimeOffset? CreatedBeforeDate { get; set; }
    DateTimeOffset? ModifiedAfterDate { get; set; }
    DateTimeOffset? ModifiedBeforeDate { get; set; }
    double MaxSearchDepth { get; set; }
    bool ObeyGitignore { get; set; }
    bool SearchInsideArchives { get; set; }
    int SortModeIndex { get; set; }
    int SortDirectionIndex { get; set; }
    int GroupModeIndex { get; set; }
    int GroupSortDirectionIndex { get; set; }
}

/// <summary>
/// A <see cref="SemanticSearchPlan"/> after normalization: drive shorthands rooted, relative
/// dates resolved to concrete instants, name-excludes expanded to globs, values clamped, and the
/// search mode parsed to an enum. Only non-null fields should override a baseline when applied.
/// </summary>
public sealed class ResolvedSearchPlan
{
    public string? Directory { get; init; }
    public string? Pattern { get; init; }
    public SearchMode? SearchMode { get; init; }
    public bool? CaseSensitive { get; init; }
    public bool? UseRegex { get; init; }
    public bool? ExactMatch { get; init; }
    public IReadOnlyList<string>? IncludeGlobs { get; init; }
    public IReadOnlyList<string>? ExcludeGlobs { get; init; }
    public long? MinFileSizeBytes { get; init; }
    public long? MaxFileSizeBytes { get; init; }
    public DateTimeOffset? CreatedAfterDate { get; init; }
    public DateTimeOffset? CreatedBeforeDate { get; init; }
    public DateTimeOffset? ModifiedAfterDate { get; init; }
    public DateTimeOffset? ModifiedBeforeDate { get; init; }
    public int? MaxSearchDepth { get; init; }
    public bool? ObeyGitignore { get; init; }
    public bool? SearchInsideArchives { get; init; }

    /// <summary>Sort column: 1=matches, 2=date(modified), 3=size, 4=name, 5=directory. Null = leave unchanged.</summary>
    public int? SortModeIndex { get; init; }

    /// <summary>Sort direction: 0=descending, 1=ascending. Null = leave unchanged.</summary>
    public int? SortDirectionIndex { get; init; }

    /// <summary>Grouping for the results. Null = leave unchanged.</summary>
    public GroupMode? GroupMode { get; init; }

    /// <summary>Group order: 0=A-Z/recent-first/small-first, 1=reversed. Null = leave unchanged.</summary>
    public int? GroupSortDirectionIndex { get; init; }

    public string? Explanation { get; init; }

    /// <summary>Non-fatal notes (e.g. a date that could not be parsed and was ignored).</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// Maps a model-produced <see cref="SemanticSearchPlan"/> onto Yagu's concrete search inputs.
/// Pure and side-effect free except for the thin <see cref="ApplyToTarget"/>/<see cref="ApplyToSearchOptions"/>
/// adapters, so the normalization logic is fully unit-testable.
/// </summary>
public static class SemanticPlanApplier
{
    private const string LogSource = "Semantic.PlanApplier";

    /// <summary>
    /// Normalizes <paramref name="plan"/> using <paramref name="context"/> for relative-date and
    /// default-directory resolution. Never throws on bad model output — unparseable values are
    /// dropped and recorded in <see cref="ResolvedSearchPlan.Warnings"/>.
    /// </summary>
    public static ResolvedSearchPlan Resolve(SemanticSearchPlan plan, SemanticTranslationContext context)
    {
        ArgumentNullException.ThrowIfNull(plan);
        context ??= new SemanticTranslationContext();
        var warnings = new List<string>();

        string? directory = ResolveDirectory(plan.Directory) ?? NormalizeDirectory(context.DefaultDirectory);

        var include = NormalizeFilterTokens(plan.IncludeGlobs);
        var exclude = NormalizeFilterTokens(plan.ExcludeGlobs);
        AppendNameExcludes(exclude, plan.ExcludeFileNames);

        long? minSize = ClampSize(plan.MinFileSizeBytes);
        long? maxSize = ClampSize(plan.MaxFileSizeBytes);

        // phi-3.5-mini sometimes pads every date field with a single value, yielding a contradictory
        // "between X and X" window that matches nothing. Identical raw bounds are never a real user
        // intent, so drop the degenerate pair before resolving (resolution would otherwise hide it by
        // pushing the "before" bound to end-of-day, making 00:00..23:59 look like a valid 1-day range).
        string? createdAfterRaw = plan.CreatedAfter;
        string? createdBeforeRaw = plan.CreatedBefore;
        if (IsSameDateToken(createdAfterRaw, createdBeforeRaw))
        {
            createdAfterRaw = createdBeforeRaw = null;
            warnings.Add("Ignored a contradictory created-date range (identical bounds).");
        }
        string? modifiedAfterRaw = plan.ModifiedAfter;
        string? modifiedBeforeRaw = plan.ModifiedBefore;
        if (IsSameDateToken(modifiedAfterRaw, modifiedBeforeRaw))
        {
            modifiedAfterRaw = modifiedBeforeRaw = null;
            warnings.Add("Ignored a contradictory modified-date range (identical bounds).");
        }

        // phi-3.5-mini also pads by COPYING one user-intended date across the created*/modified*
        // families (e.g. "created before 2024" emits createdBefore AND modifiedBefore = "2024-01-01").
        // When the same token appears in both families, keep the one the explanation refers to and
        // drop the duplicate; the model's explanation stays accurate even when its fields are padded.
        bool prefersModified = MentionsModified(plan.Explanation) && !MentionsCreated(plan.Explanation);
        if (IsSameDateToken(createdBeforeRaw, modifiedBeforeRaw))
        {
            if (prefersModified) createdBeforeRaw = null; else modifiedBeforeRaw = null;
            warnings.Add("Ignored a duplicated date copied across created/modified bounds.");
        }
        if (IsSameDateToken(createdAfterRaw, modifiedAfterRaw))
        {
            if (prefersModified) createdAfterRaw = null; else modifiedAfterRaw = null;
            warnings.Add("Ignored a duplicated date copied across created/modified bounds.");
        }

        DateTimeOffset? createdAfter = ResolveDate(createdAfterRaw, context, "createdAfter", warnings, endOfDay: false);
        DateTimeOffset? createdBefore = ResolveDate(createdBeforeRaw, context, "createdBefore", warnings, endOfDay: true);
        DateTimeOffset? modifiedAfter = ResolveDate(modifiedAfterRaw, context, "modifiedAfter", warnings, endOfDay: false);
        DateTimeOffset? modifiedBefore = ResolveDate(modifiedBeforeRaw, context, "modifiedBefore", warnings, endOfDay: true);
        SearchMode? mode = ParseSearchMode(plan.SearchMode, warnings);

        var (sortModeIndex, sortDirectionIndex) = ParseSort(plan.SortBy, plan.SortDirection, warnings);
        var (groupMode, groupDirectionIndex) = ParseGroup(plan.GroupBy, plan.GroupDirection, warnings);

        string? pattern = string.IsNullOrWhiteSpace(plan.Pattern) ? null : plan.Pattern!.Trim();
        bool? useRegex = plan.UseRegex;

        // Weak models sometimes echo a file-type filter into "pattern" (e.g. pattern "*.png" when
        // includeGlobs already has "*.png"). That term isn't a real search query and would make the
        // engine match the literal text; drop it so the match-all synthesis below takes over and the
        // include/exclude/metadata filters drive the result.
        if (pattern is not null && include.Count > 0 && IsRedundantGlobPattern(pattern, include))
            pattern = null;

        // A request like "all png files modified last year" carries no text term — the model emits
        // an empty pattern and relies purely on globs/metadata filters. Yagu's engine yields nothing
        // for an empty query, so synthesize a match-all filename query (regex ".") that enumerates
        // every file passing the include/exclude/date/size filters.
        bool hasFileFilter = include.Count > 0 || exclude.Count > 0 ||
            minSize.HasValue || maxSize.HasValue ||
            createdAfter.HasValue || createdBefore.HasValue ||
            modifiedAfter.HasValue || modifiedBefore.HasValue;
        if (pattern is null && (hasFileFilter || mode == Models.SearchMode.FileNames))
        {
            pattern = ".";
            useRegex = true;
            mode ??= Models.SearchMode.FileNames;
        }

        var resolved = new ResolvedSearchPlan
        {
            Directory = directory,
            Pattern = pattern,
            SearchMode = mode,
            CaseSensitive = plan.CaseSensitive,
            UseRegex = useRegex,
            ExactMatch = plan.ExactMatch,
            IncludeGlobs = include.Count > 0 ? include : null,
            ExcludeGlobs = exclude.Count > 0 ? exclude : null,
            MinFileSizeBytes = minSize,
            MaxFileSizeBytes = maxSize,
            CreatedAfterDate = createdAfter,
            CreatedBeforeDate = createdBefore,
            ModifiedAfterDate = modifiedAfter,
            ModifiedBeforeDate = modifiedBefore,
            MaxSearchDepth = plan.MaxSearchDepth is { } d ? Math.Max(0, d) : null,
            ObeyGitignore = plan.ObeyGitignore,
            SearchInsideArchives = plan.SearchInsideArchives,
            SortModeIndex = sortModeIndex,
            SortDirectionIndex = sortDirectionIndex,
            GroupMode = groupMode,
            GroupSortDirectionIndex = groupDirectionIndex,
            Explanation = string.IsNullOrWhiteSpace(plan.Explanation) ? null : plan.Explanation!.Trim(),
            Warnings = warnings,
        };

        var log = LogService.Instance;
        if (warnings.Count > 0)
            log.Verbose(LogSource,
                $"Resolved plan with {warnings.Count} normalization warning(s): {string.Join(" | ", warnings)}");
        if (log.IsVerboseEnabled)
            log.Verbose(LogSource, $"Resolved plan: {DescribeResolved(resolved)}");

        return resolved;
    }

    /// <summary>Compact, allocation-light summary of a resolved plan for Verbose diagnostics. Only
    /// the fields the model actually set are emitted, so the log shows exactly what will override the
    /// search inputs.</summary>
    private static string DescribeResolved(ResolvedSearchPlan r)
    {
        var parts = new List<string>();
        if (r.Directory is { } dir) parts.Add($"dir={dir}");
        if (r.Pattern is { } pat) parts.Add($"pattern='{pat}'");
        if (r.SearchMode is { } m) parts.Add($"mode={m}");
        if (r.CaseSensitive is { } cs) parts.Add($"caseSensitive={cs}");
        if (r.UseRegex is { } rx) parts.Add($"useRegex={rx}");
        if (r.ExactMatch is { } em) parts.Add($"exactMatch={em}");
        if (r.IncludeGlobs is { Count: > 0 } inc) parts.Add($"include=[{string.Join(",", inc)}]");
        if (r.ExcludeGlobs is { Count: > 0 } exc) parts.Add($"exclude=[{string.Join(",", exc)}]");
        if (r.MinFileSizeBytes is { } mn) parts.Add($"minSize={mn}");
        if (r.MaxFileSizeBytes is { } mx) parts.Add($"maxSize={mx}");
        if (r.CreatedAfterDate is { } ca) parts.Add($"createdAfter={ca:O}");
        if (r.CreatedBeforeDate is { } cb) parts.Add($"createdBefore={cb:O}");
        if (r.ModifiedAfterDate is { } ma) parts.Add($"modifiedAfter={ma:O}");
        if (r.ModifiedBeforeDate is { } mb) parts.Add($"modifiedBefore={mb:O}");
        if (r.MaxSearchDepth is { } md) parts.Add($"maxDepth={md}");
        if (r.ObeyGitignore is { } gi) parts.Add($"obeyGitignore={gi}");
        if (r.SearchInsideArchives is { } ar) parts.Add($"archives={ar}");
        if (r.SortModeIndex is { } sm) parts.Add($"sortMode={sm}");
        if (r.SortDirectionIndex is { } sd) parts.Add($"sortDir={sd}");
        if (r.GroupMode is { } gm) parts.Add($"group={gm}");
        if (r.GroupSortDirectionIndex is { } gd) parts.Add($"groupDir={gd}");
        return parts.Count == 0 ? "(no overrides)" : string.Join(", ", parts);
    }

    /// <summary>Convenience overload that resolves then applies to a writable UI target.</summary>
    public static ResolvedSearchPlan ApplyToTarget(SemanticSearchPlan plan, SemanticTranslationContext context, ISemanticPlanTarget target)
    {
        var resolved = Resolve(plan, context);
        ApplyToTarget(resolved, target);
        return resolved;
    }

    /// <summary>Writes the resolved plan onto <paramref name="target"/>. Only fields the model set
    /// are overwritten; everything else keeps the target's current value.</summary>
    public static void ApplyToTarget(ResolvedSearchPlan resolved, ISemanticPlanTarget target)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        ArgumentNullException.ThrowIfNull(target);

        if (resolved.Directory is { } dir) target.Directory = dir;
        if (resolved.Pattern is { } pattern) target.Query = pattern;
        if (resolved.SearchMode is { } mode) target.SearchModeIndex = (int)mode;
        if (resolved.CaseSensitive is { } cs) target.CaseSensitive = cs;
        if (resolved.UseRegex is { } rx) target.UseRegex = rx;
        if (resolved.ExactMatch is { } em) target.ExactMatch = em;

        if (resolved.IncludeGlobs is { } inc)
        {
            target.IncludeGlobs = string.Join(", ", inc);
            target.IncludeFilterModeIndex = (int)FilterPatternMode.GlobPath;
        }
        if (resolved.ExcludeGlobs is { } exc)
        {
            target.ExcludeGlobs = string.Join(", ", exc);
            target.ExcludeFilterModeIndex = (int)FilterPatternMode.GlobPath;
        }

        if (resolved.MinFileSizeBytes is { } min) target.MinFileSizeBytes = min;
        if (resolved.MaxFileSizeBytes is { } max) target.MaxFileSizeBytes = max;
        if (resolved.CreatedAfterDate is { } ca) target.CreatedAfterDate = ca;
        if (resolved.CreatedBeforeDate is { } cb) target.CreatedBeforeDate = cb;
        if (resolved.ModifiedAfterDate is { } ma) target.ModifiedAfterDate = ma;
        if (resolved.ModifiedBeforeDate is { } mb) target.ModifiedBeforeDate = mb;

        // UI MaxSearchDepth uses NaN for "unlimited"; resolved 0 also means unlimited.
        if (resolved.MaxSearchDepth is { } depth) target.MaxSearchDepth = depth <= 0 ? double.NaN : depth;
        if (resolved.ObeyGitignore is { } gi) target.ObeyGitignore = gi;
        if (resolved.SearchInsideArchives is { } arc) target.SearchInsideArchives = arc;

        // Sort: set the column first, then the direction, so the view-model's change handlers
        // settle on the final (mode, direction) pair regardless of the target's prior state.
        if (resolved.SortModeIndex is { } sortMode)
        {
            target.SortModeIndex = sortMode;
            target.SortDirectionIndex = resolved.SortDirectionIndex ?? 0;
        }

        if (resolved.GroupMode is { } group)
        {
            target.GroupModeIndex = (int)group;
            if (resolved.GroupSortDirectionIndex is { } groupDir)
                target.GroupSortDirectionIndex = groupDir;
        }
    }

    /// <summary>
    /// Produces a CLI-style overlay from a resolved plan: only the values the model set are returned
    /// (as nullables) so the caller can fold them into its own <c>SearchOptions</c> construction
    /// without this method needing to know the surrounding defaults.
    /// </summary>
    public static SemanticSearchOverlay ToOverlay(ResolvedSearchPlan resolved)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        return new SemanticSearchOverlay
        {
            Directory = resolved.Directory,
            Query = resolved.Pattern,
            SearchMode = resolved.SearchMode,
            CaseSensitive = resolved.CaseSensitive,
            UseRegex = resolved.UseRegex,
            ExactMatch = resolved.ExactMatch,
            IncludeGlobs = resolved.IncludeGlobs,
            ExcludeGlobs = resolved.ExcludeGlobs,
            MinFileSizeBytes = resolved.MinFileSizeBytes,
            MaxFileSizeBytes = resolved.MaxFileSizeBytes,
            CreatedAfterDate = resolved.CreatedAfterDate,
            CreatedBeforeDate = resolved.CreatedBeforeDate,
            ModifiedAfterDate = resolved.ModifiedAfterDate,
            ModifiedBeforeDate = resolved.ModifiedBeforeDate,
            MaxSearchDepth = resolved.MaxSearchDepth,
            ObeyGitignore = resolved.ObeyGitignore,
            SearchInsideArchives = resolved.SearchInsideArchives,
            SortBy = SortModeIndexToCliKey(resolved.SortModeIndex),
            SortDescending = resolved.SortModeIndex is null ? null : resolved.SortDirectionIndex != 1,
            GroupBy = GroupModeToCliKey(resolved.GroupMode),
            GroupDescending = resolved.GroupMode is null ? null : resolved.GroupSortDirectionIndex == 1,
        };
    }

    /// <summary>Maps a UI sort-column index to the CLI <c>--sort</c> key, or null when unset.</summary>
    private static string? SortModeIndexToCliKey(int? sortModeIndex) => sortModeIndex switch
    {
        1 => "matches",
        2 => "date",
        3 => "size",
        4 => "name",
        5 => "directory",
        _ => null,
    };

    /// <summary>Maps a <see cref="Models.GroupMode"/> to the CLI <c>--group</c> key, or null when unset/None.</summary>
    private static string? GroupModeToCliKey(GroupMode? mode) => mode switch
    {
        Models.GroupMode.Folder => "directory",
        Models.GroupMode.Extension => "extension",
        Models.GroupMode.FileSize => "size",
        Models.GroupMode.DateRangeModified => "modified",
        Models.GroupMode.DateRangeCreated => "created",
        Models.GroupMode.DateRangeModifiedCreated => "date",
        _ => null,
    };

    // ---- helpers -----------------------------------------------------------

    private static readonly Regex DriveShorthand =
        new(@"^\s*(?:the\s+)?([A-Za-z])(?:\s*:|\s*:\\|\s+drive)?\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Resolves a model-supplied location like "C drive", "C:", or "C" to a rooted path.
    /// Already-rooted or UNC paths pass through trimmed.</summary>
    internal static string? ResolveDirectory(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string value = raw.Trim().Trim('"');

        var m = DriveShorthand.Match(value);
        if (m.Success)
            return char.ToUpperInvariant(m.Groups[1].Value[0]) + ":\\";

        return NormalizeDirectory(value);
    }

    private static string? NormalizeDirectory(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string v = value.Trim().Trim('"');
        // "C:" -> "C:\" so it roots correctly.
        if (v.Length == 2 && char.IsLetter(v[0]) && v[1] == ':')
            return char.ToUpperInvariant(v[0]) + ":\\";
        return v;
    }

    private static SearchMode? ParseSearchMode(string? raw, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string v = raw.Trim().ToLowerInvariant().Replace("_", "-").Replace(" ", "-");
        return v switch
        {
            "both" or "content-and-filenames" or "all" => SearchMode.Both,
            "content" or "contents" or "text" or "inside" => SearchMode.Content,
            "filenames" or "filename" or "names" or "name" or "files" => SearchMode.FileNames,
            "filename-then-content" or "filenamethencontent" or "name-then-content" => SearchMode.FileNameThenContent,
            _ => Unknown(),
        };

        SearchMode? Unknown()
        {
            warnings.Add($"Unrecognized searchMode '{raw}'; left unchanged.");
            return null;
        }
    }

    /// <summary>
    /// Parses the model-supplied sort field/direction into the UI sort indices. Returns
    /// <c>(null, null)</c> when no field is given. When a field is given without a direction,
    /// defaults to descending (index 0). Unrecognized fields are dropped with a warning.
    /// </summary>
    internal static (int? ModeIndex, int? DirectionIndex) ParseSort(string? sortBy, string? sortDirection, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(sortBy)) return (null, null);

        string v = Normalize(sortBy);
        int? mode = v switch
        {
            "relevance" or "matches" or "match" or "matchcount" or "match-count" or "count" or "hits" => 1,
            "date" or "modified" or "datemodified" or "date-modified" or "modifieddate" or "modified-date"
                or "lastmodified" or "last-modified" or "time" or "timestamp" => 2,
            "size" or "filesize" or "file-size" or "bytes" or "length" => 3,
            "name" or "filename" or "file-name" or "title" => 4,
            "directory" or "dir" or "folder" or "path" or "filepath" or "file-path" or "location" => 5,
            _ => null,
        };

        if (mode is null)
        {
            warnings.Add($"Unrecognized sortBy '{sortBy}'; left unchanged.");
            return (null, null);
        }

        int direction = ParseSortDirection(sortDirection, warnings);
        return (mode, direction);
    }

    /// <summary>Parses a sort direction token. 1=ascending, 0=descending. Defaults to descending (0).</summary>
    private static int ParseSortDirection(string? sortDirection, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(sortDirection)) return 0;

        string v = Normalize(sortDirection);
        return v switch
        {
            "asc" or "ascending" or "ascend" or "a-z" or "a-to-z" or "az" or "increasing" or "smallest"
                or "smallest-first" or "oldest" or "oldest-first" or "low" or "low-high" or "up" => 1,
            "desc" or "descending" or "descend" or "z-a" or "z-to-a" or "za" or "decreasing" or "largest"
                or "largest-first" or "newest" or "newest-first" or "high" or "high-low" or "down"
                or "reverse" or "reversed" => 0,
            _ => Unknown(),
        };

        int Unknown()
        {
            warnings.Add($"Unrecognized sortDirection '{sortDirection}'; defaulted to descending.");
            return 0;
        }
    }

    /// <summary>
    /// Parses the model-supplied group field/direction into a <see cref="Models.GroupMode"/> and the
    /// group-order index (0=natural A-Z/recent-first/small-first, 1=reversed). Returns <c>(null, null)</c>
    /// when no field is given. Unrecognized fields are dropped with a warning.
    /// </summary>
    internal static (GroupMode? Mode, int? DirectionIndex) ParseGroup(string? groupBy, string? groupDirection, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(groupBy)) return (null, null);

        string v = Normalize(groupBy);
        GroupMode? mode = v switch
        {
            "none" or "off" or "no" or "nogrouping" or "no-grouping" or "ungrouped" => Models.GroupMode.None,
            "directory" or "dir" or "folder" or "path" or "filepath" or "file-path" or "location" => Models.GroupMode.Folder,
            "extension" or "ext" or "type" or "filetype" or "file-type" or "kind" => Models.GroupMode.Extension,
            "size" or "filesize" or "file-size" => Models.GroupMode.FileSize,
            "modified" or "datemodified" or "date-modified" or "modifieddate" or "modified-date"
                or "lastmodified" or "last-modified" => Models.GroupMode.DateRangeModified,
            "created" or "datecreated" or "date-created" or "createddate" or "created-date" or "creation" => Models.GroupMode.DateRangeCreated,
            "date" or "modified-created" or "modified+created" or "modifiedcreated" or "date-range" or "daterange" => Models.GroupMode.DateRangeModifiedCreated,
            _ => null,
        };

        if (mode is null)
        {
            warnings.Add($"Unrecognized groupBy '{groupBy}'; left unchanged.");
            return (null, null);
        }

        if (mode == Models.GroupMode.None)
            return (Models.GroupMode.None, 0);

        int direction = ParseGroupDirection(groupDirection, warnings);
        return (mode, direction);
    }

    /// <summary>Parses a group-order token. 0=natural (A-Z/recent-first/small-first), 1=reversed.</summary>
    private static int ParseGroupDirection(string? groupDirection, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(groupDirection)) return 0;

        string v = Normalize(groupDirection);
        return v switch
        {
            "asc" or "ascending" or "a-z" or "a-to-z" or "az" or "recent" or "recent-first" or "newest" or "newest-first"
                or "small" or "small-first" or "smallest" or "smallest-first" or "up" => 0,
            "desc" or "descending" or "z-a" or "z-to-a" or "za" or "older" or "older-first" or "oldest" or "oldest-first"
                or "large" or "large-first" or "largest" or "largest-first" or "reverse" or "reversed" or "down" => 1,
            _ => Unknown(),
        };

        int Unknown()
        {
            warnings.Add($"Unrecognized groupDirection '{groupDirection}'; defaulted to the natural order.");
            return 0;
        }
    }

    private static string Normalize(string raw) =>
        raw.Trim().ToLowerInvariant().Replace("_", "-").Replace(" ", "-");

    /// <summary>Normalizes a model-supplied size bound. Treats <c>0</c> and negative values as
    /// "unset" (null): a 0-byte bound is never a meaningful filter and is almost always the model
    /// padding the JSON with placeholder zeros rather than an intentional constraint.</summary>
    private static long? ClampSize(long? value)
    {
        if (value is null) return null;
        return value.Value <= 0 ? null : value.Value;
    }

    /// <summary>True when two date bounds carry the same non-empty raw token — the signature of a
    /// model padding every date field with one value rather than expressing a real range.</summary>
    private static bool IsSameDateToken(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when the model's explanation refers to creation time.</summary>
    internal static bool MentionsCreated(string? explanation) =>
        !string.IsNullOrEmpty(explanation) && explanation.Contains("creat", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the model's explanation refers to modification/change time.</summary>
    internal static bool MentionsModified(string? explanation)
    {
        if (string.IsNullOrEmpty(explanation)) return false;
        return explanation.Contains("modif", StringComparison.OrdinalIgnoreCase)
            || explanation.Contains("chang", StringComparison.OrdinalIgnoreCase)
            || explanation.Contains("updat", StringComparison.OrdinalIgnoreCase)
            || explanation.Contains("edit", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when <paramref name="pattern"/> is just a file-type glob (e.g. "*.png", "*.cs")
    /// that duplicates an entry already in <paramref name="include"/> — i.e. the model put a filter
    /// in the search term by mistake rather than supplying a real query.</summary>
    private static bool IsRedundantGlobPattern(string pattern, IReadOnlyCollection<string> include)
    {
        string p = pattern.Trim();
        // Only treat simple extension globs as redundant; never a multi-word or content-style term.
        if (!Regex.IsMatch(p, @"^\*?\.[A-Za-z0-9]+$")) return false;
        return include.Any(g => string.Equals(g.Trim(), p, StringComparison.OrdinalIgnoreCase));
    }

    private static List<string> NormalizeFilterTokens(IEnumerable<string>? tokens)
    {
        var result = new List<string>();
        if (tokens is null) return result;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tokens)
        {
            if (string.IsNullOrWhiteSpace(t)) continue;
            string token = SanitizeGlobToken(t);
            if (token.Length == 0) continue;
            if (seen.Add(token)) result.Add(token);
        }
        return result;
    }

    /// <summary>Cleans a single include/exclude glob token. Trims quotes/space and removes stray
    /// whitespace that small models sometimes inject around the extension dot or wildcard
    /// (e.g. "*. json" -> "*.json", "* .log" -> "*.log"). Internal spaces NOT adjacent to a '.' or
    /// '*' are preserved so legitimate name fragments like "my file.txt" survive.</summary>
    private static string SanitizeGlobToken(string raw)
    {
        string token = raw.Trim().Trim('"').Trim();
        if (token.Length == 0) return token;
        // Collapse whitespace that hugs a dot or wildcard, the only places a glob never has spaces.
        token = Regex.Replace(token, @"\s*([.*?])\s*", "$1");
        return token;
    }

    /// <summary>Expands bare file names ("abc") into exclude globs that catch both the
    /// extensionless file and any extension ("abc", "abc.*"). Tokens that already look like
    /// globs/paths are passed through unchanged.</summary>
    private static void AppendNameExcludes(List<string> exclude, IEnumerable<string>? names)
    {
        if (names is null) return;
        var seen = new HashSet<string>(exclude, StringComparer.OrdinalIgnoreCase);
        foreach (var n in names)
        {
            if (string.IsNullOrWhiteSpace(n)) continue;
            string name = n.Trim().Trim('"');
            if (name.Length == 0) continue;

            if (name.IndexOfAny(['*', '?', '/', '\\', '.']) >= 0)
            {
                if (seen.Add(name)) exclude.Add(name);
                continue;
            }

            foreach (var glob in new[] { name, name + ".*" })
                if (seen.Add(glob)) exclude.Add(glob);
        }
    }

    private static readonly Regex RelativeAgo =
        new(@"^\s*(\d+)\s*(day|week|month|year)s?\s*ago\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LastN =
        new(@"^\s*(?:in\s+the\s+)?(?:last|past)\s+(\d+)?\s*(day|week|month|year)s?\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Parses an ISO date or a small set of relative phrases into a concrete instant. For
    /// "before" bounds, dates without a time component are pushed to end-of-day so the named day is
    /// inclusive.</summary>
    internal static DateTimeOffset? ResolveDate(string? raw, SemanticTranslationContext context, string field, List<string> warnings, bool endOfDay)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string v = raw.Trim();
        DateTimeOffset now = context.Now;

        switch (v.ToLowerInvariant())
        {
            case "today": return DayBound(now, endOfDay);
            case "yesterday": return DayBound(now.AddDays(-1), endOfDay);
        }

        var ago = RelativeAgo.Match(v);
        if (ago.Success && int.TryParse(ago.Groups[1].Value, out int agoN))
            return Shift(now, ago.Groups[2].Value, -agoN);

        var last = LastN.Match(v);
        if (last.Success)
        {
            int n = last.Groups[1].Success && int.TryParse(last.Groups[1].Value, out int parsed) ? parsed : 1;
            return Shift(now, last.Groups[2].Value, -n);
        }

        // Date-only (yyyy-MM-dd and friends) -> day bound; otherwise full timestamp.
        if (DateOnlyParses(v))
        {
            if (DateTimeOffset.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dateOnly))
                return DayBound(dateOnly, endOfDay);
        }
        if (DateTimeOffset.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt))
            return dt;

        warnings.Add($"Could not interpret {field} value '{raw}'; ignored.");
        return null;

        static DateTimeOffset Shift(DateTimeOffset from, string unit, int amount) => unit.ToLowerInvariant() switch
        {
            "day" => from.AddDays(amount),
            "week" => from.AddDays(amount * 7),
            "month" => from.AddMonths(amount),
            // The calling regexes only ever capture day|week|month|year, so the remaining unit here
            // is always "year"; folding it into the default arm keeps every branch reachable.
            _ => from.AddYears(amount),
        };
    }

    private static bool DateOnlyParses(string v) =>
        Regex.IsMatch(v, @"^\d{4}[-/]\d{1,2}[-/]\d{1,2}$");

    private static DateTimeOffset DayBound(DateTimeOffset value, bool endOfDay)
    {
        var d = new DateTimeOffset(value.Year, value.Month, value.Day, 0, 0, 0, value.Offset);
        return endOfDay ? d.AddDays(1).AddTicks(-1) : d;
    }
}

/// <summary>
/// Nullable overlay of the search inputs a semantic plan can set, used by the CLI to fold a
/// resolved plan into its own <c>SearchOptions</c> construction. Null = "not specified by the plan".
/// </summary>
public sealed class SemanticSearchOverlay
{
    public string? Directory { get; init; }
    public string? Query { get; init; }
    public SearchMode? SearchMode { get; init; }
    public bool? CaseSensitive { get; init; }
    public bool? UseRegex { get; init; }
    public bool? ExactMatch { get; init; }
    public IReadOnlyList<string>? IncludeGlobs { get; init; }
    public IReadOnlyList<string>? ExcludeGlobs { get; init; }
    public long? MinFileSizeBytes { get; init; }
    public long? MaxFileSizeBytes { get; init; }
    public DateTimeOffset? CreatedAfterDate { get; init; }
    public DateTimeOffset? CreatedBeforeDate { get; init; }
    public DateTimeOffset? ModifiedAfterDate { get; init; }
    public DateTimeOffset? ModifiedBeforeDate { get; init; }
    public int? MaxSearchDepth { get; init; }
    public bool? ObeyGitignore { get; init; }
    public bool? SearchInsideArchives { get; init; }

    /// <summary>CLI <c>--sort</c> key (matches, date, size, name, directory), or null when unset.</summary>
    public string? SortBy { get; init; }

    /// <summary>True = descending, false = ascending. Null when no sort field is set.</summary>
    public bool? SortDescending { get; init; }

    /// <summary>CLI <c>--group</c> key (directory, extension, size, modified, created, date), or null when unset.</summary>
    public string? GroupBy { get; init; }

    /// <summary>True reverses the natural group order. Null when no group field is set.</summary>
    public bool? GroupDescending { get; init; }
}
