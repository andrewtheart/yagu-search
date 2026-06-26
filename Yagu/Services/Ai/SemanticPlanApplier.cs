using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
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
    bool SearchHiddenFiles { get; set; }
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
    public bool? SearchHiddenFiles { get; init; }

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

        string? modelDirectory = ResolveDirectory(plan.Directory);
        // Reject a hallucinated directory: a small model can echo a nonsense query into the plan's
        // "directory" field. Applying it would overwrite the search location with a non-existent path
        // and fail with "Directory does not exist". When a probe is supplied, drop a model directory
        // that doesn't exist and fall back to the user's current directory instead.
        if (modelDirectory is not null && context.DirectoryExists is { } directoryExists && !directoryExists(modelDirectory))
        {
            warnings.Add($"Ignored directory '{modelDirectory}' because it does not exist; used the current location instead.");
            modelDirectory = null;
        }
        string? directory = modelDirectory ?? NormalizeDirectory(context.DefaultDirectory);

        var include = NormalizeFilterTokens(plan.IncludeGlobs);
        var exclude = NormalizeFilterTokens(plan.ExcludeGlobs);
        AppendNameExcludes(exclude, plan.ExcludeFileNames);

        // Deterministically correct the include filter for programming-language names the model
        // can't represent as a glob (e.g. "c# files" -> the model emits "*.c" or nothing). Read from
        // the ORIGINAL query, where the '#'/'++' still exists, so this is independent of the guess.
        ApplyLanguageExtensionGlobs(include, context.OriginalQuery);

        // Force the include glob for a file extension the user named EXPLICITLY with a leading dot
        // (e.g. ".com files on C:"). Small models mishandle uncommon/ambiguous extensions — phi-4-mini
        // hallucinated ".com" (also a TLD) into a content search for "*.gitignore". When the query
        // states a literal ".ext", drive the filter from the query, not the model's guess.
        var explicitExtensions = ApplyExplicitExtensionGlobs(include, context.OriginalQuery);

        // "hidden file(s)" / "not hidden files" controls the Advanced Options "Search hidden files"
        // toggle, NOT an exclude glob. Detect the intent from the ORIGINAL query (independent of the
        // model's guess) and, when present, drop the dotfile/hidden-exclusion glob the model emits to
        // approximate it (e.g. ".\.[A-Za-z0-9].+" or ".*") so the toggle alone controls hidden files.
        bool? searchHidden = DetectHiddenFilePreference(context.OriginalQuery);
        if (searchHidden is not null)
            exclude.RemoveAll(IsLikelyHiddenExclusionToken);
        searchHidden ??= plan.SearchHidden;

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

        // When the user explicitly named a file extension ("X files on C:"), the request is a filename
        // listing. A weak model sometimes routes the (often hallucinated) extension into a content
        // pattern + content mode instead of an include glob — e.g. ".com files" -> pattern
        // "*.gitignore", mode content. The include glob is already forced above; drop a glob-shaped
        // pattern and force filename mode so the forced include glob drives the result. A real content
        // term ("X files containing Y") is not glob-shaped, so a genuine content search is preserved.
        if (explicitExtensions.Count > 0 && pattern is not null && LooksLikeGlobToken(pattern))
        {
            pattern = null;
            mode = Models.SearchMode.FileNames;
        }

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
            SearchHiddenFiles = searchHidden,
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
        if (r.SearchHiddenFiles is { } sh) parts.Add($"searchHidden={sh}");
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
        if (resolved.SearchHiddenFiles is { } sh) target.SearchHiddenFiles = sh;

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
    /// Builds a deterministic, human-readable summary of a resolved plan for display, instead of
    /// trusting the model's free-text <see cref="ResolvedSearchPlan.Explanation"/>. Small on-device
    /// models routinely garble the literal search term in prose (e.g. writing "yagursd" for "yagu"),
    /// so this summary is derived only from the resolved fields and can never drift from what is
    /// actually searched.
    /// </summary>
    public static string BuildExplanation(ResolvedSearchPlan resolved)
    {
        ArgumentNullException.ThrowIfNull(resolved);

        string directory = string.IsNullOrWhiteSpace(resolved.Directory)
            ? "all drives"
            : resolved.Directory!.Trim();

        string scope = resolved.SearchMode switch
        {
            SearchMode.FileNames => "file names",
            SearchMode.Content => "file contents",
            SearchMode.FileNameThenContent => "the contents of files whose names match",
            _ => "file names and contents",
        };

        var sb = new StringBuilder();
        sb.Append("Searching ").Append(directory).Append(" \u2014 ").Append(scope);

        if (!string.IsNullOrWhiteSpace(resolved.Pattern))
        {
            sb.Append(resolved.UseRegex == true
                ? $" matching the regular expression {resolved.Pattern}"
                : $" containing \u201c{resolved.Pattern}\u201d");
        }

        if (resolved.IncludeGlobs is { Count: > 0 } includes)
            sb.Append(", limited to ").Append(string.Join(", ", includes));

        if (resolved.ExcludeGlobs is { Count: > 0 } excludes)
            sb.Append(", excluding ").Append(string.Join(", ", excludes));

        if (resolved.SearchHiddenFiles is { } hidden)
            sb.Append(hidden ? ", including hidden files" : ", excluding hidden files");

        AppendSizeClause(sb, resolved.MinFileSizeBytes, resolved.MaxFileSizeBytes);
        AppendDateClause(sb, "modified", resolved.ModifiedAfterDate, resolved.ModifiedBeforeDate);
        AppendDateClause(sb, "created", resolved.CreatedAfterDate, resolved.CreatedBeforeDate);

        sb.Append('.');
        return sb.ToString();
    }

    private static void AppendSizeClause(StringBuilder sb, long? min, long? max)
    {
        bool hasMin = min is > 0;
        bool hasMax = max is > 0;
        if (hasMin && hasMax)
            sb.Append(CultureInfo.InvariantCulture, $", between {FormatSize(min!.Value)} and {FormatSize(max!.Value)}");
        else if (hasMin)
            sb.Append(CultureInfo.InvariantCulture, $", larger than {FormatSize(min!.Value)}");
        else if (hasMax)
            sb.Append(CultureInfo.InvariantCulture, $", smaller than {FormatSize(max!.Value)}");
    }

    private static void AppendDateClause(StringBuilder sb, string label, DateTimeOffset? after, DateTimeOffset? before)
    {
        // Show the calendar date as stored (no time-zone conversion) since these are date-only filters.
        static string D(DateTimeOffset d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (after.HasValue && before.HasValue)
            sb.Append(CultureInfo.InvariantCulture, $", {label} between {D(after.Value)} and {D(before.Value)}");
        else if (after.HasValue)
            sb.Append(CultureInfo.InvariantCulture, $", {label} after {D(after.Value)}");
        else if (before.HasValue)
            sb.Append(CultureInfo.InvariantCulture, $", {label} before {D(before.Value)}");
    }

    private static string FormatSize(long bytes)
    {
        const long kb = 1024, mb = kb * 1024, gb = mb * 1024;
        if (bytes >= gb && bytes % gb == 0) return $"{bytes / gb} GB";
        if (bytes >= mb && bytes % mb == 0) return $"{bytes / mb} MB";
        if (bytes >= kb && bytes % kb == 0) return $"{bytes / kb} KB";
        return $"{bytes} bytes";
    }

    /// <summary>
    /// Returns the bare (dot-less, lower-case) extensions among <paramref name="includeGlobs"/> that
    /// are known archive containers (present in <paramref name="knownArchiveExtensions"/>). Office and
    /// OpenDocument formats (.docx/.xlsx/.pptx/.odt/…) are ZIP files, so when a plan filters to them
    /// the caller must enable "Search archives" and select those extensions for their inner text to be
    /// searched. Returns an empty list when nothing matches.
    /// </summary>
    public static IReadOnlyList<string> GetArchiveExtensionsToEnable(
        IReadOnlyList<string>? includeGlobs,
        IReadOnlySet<string> knownArchiveExtensions)
    {
        if (includeGlobs is null || includeGlobs.Count == 0
            || knownArchiveExtensions is null || knownArchiveExtensions.Count == 0)
            return [];

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var glob in includeGlobs)
        {
            string ext = ExtractGlobExtension(glob);
            if (ext.Length > 0 && knownArchiveExtensions.Contains(ext) && seen.Add(ext))
                result.Add(ext);
        }
        return result;
    }

    /// <summary>
    /// Returns the bare (dot-less, lower-case) extensions among <paramref name="includeGlobs"/> that
    /// are known binary types (present in <paramref name="knownBinaryExtensions"/>, e.g. .com/.exe/.cpl).
    /// Yagu skips known-binary extensions by name, so when a plan explicitly filters to one the caller
    /// must enable binary search and stop skipping it, or the very files the user asked for are
    /// excluded. Returns an empty list when nothing matches.
    /// </summary>
    public static IReadOnlyList<string> GetBinaryExtensionsToEnable(
        IReadOnlyList<string>? includeGlobs,
        IReadOnlySet<string> knownBinaryExtensions)
    {
        if (includeGlobs is null || includeGlobs.Count == 0
            || knownBinaryExtensions is null || knownBinaryExtensions.Count == 0)
            return [];

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var glob in includeGlobs)
        {
            string ext = ExtractGlobExtension(glob);
            if (ext.Length > 0 && knownBinaryExtensions.Contains(ext) && seen.Add(ext))
                result.Add(ext);
        }
        return result;
    }

    /// <summary>Extracts the bare, lower-case file extension from a glob/extension token such as
    /// <c>*.docx</c>, <c>**/*.ZIP</c>, <c>.docx</c>, or <c>docx</c>. Returns an empty string when none
    /// can be determined.</summary>
    internal static string ExtractGlobExtension(string? glob)
    {
        if (string.IsNullOrWhiteSpace(glob)) return string.Empty;
        string s = glob.Trim();
        int slash = s.LastIndexOfAny(['/', '\\']);
        if (slash >= 0) s = s[(slash + 1)..];
        int dot = s.LastIndexOf('.');
        if (dot >= 0) s = s[(dot + 1)..];
        return s.Trim().Trim('*', '?', ' ').ToLowerInvariant();
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
            SearchHiddenFiles = resolved.SearchHiddenFiles,
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
            token = DepluralizeGlobExtension(token, KnownFileExtensions.Default);
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

    /// <summary>
    /// Repairs file-extension globs that a small model pluralized (e.g. <c>*.exes</c> -&gt;
    /// <c>*.exe</c>, <c>*.pdfs</c> -&gt; <c>*.pdf</c>, <c>*.pngs</c> -&gt; <c>*.png</c>) while leaving
    /// real extensions that end in 's' untouched (<c>*.cs</c>, <c>*.css</c>, <c>*.js</c>, <c>*.ts</c>,
    /// <c>*.xls</c>, <c>*.class</c>, <c>*.props</c>). The trailing-'s' form is only de-pluralized when
    /// its singular IS a known extension and the plural is NOT, so legitimate extensions (which are
    /// themselves "known") are never altered. To stay safe it (1) only touches tokens whose final
    /// path segment contains a '.', leaving folder excludes like <c>docs</c> or <c>builds</c> intact,
    /// and (2) never de-pluralizes down to a single-character extension (so <c>*.hs</c>/<c>*.as</c>
    /// are preserved). Original casing of the kept characters is retained.
    /// </summary>
    internal static string DepluralizeGlobExtension(string glob, IReadOnlySet<string> known)
    {
        if (string.IsNullOrEmpty(glob) || known is null || known.Count == 0) return glob;
        int slash = glob.LastIndexOfAny(['/', '\\']);
        int dot = glob.LastIndexOf('.');
        if (dot <= slash) return glob; // no extension dot in the final path segment

        string ext = glob[(dot + 1)..];
        // Only plain alphanumeric extensions ending in 's' (with a >=2-char singular) are candidates.
        if (ext.Length < 3 || (ext[^1] != 's' && ext[^1] != 'S')) return glob;
        foreach (char c in ext)
            if (!char.IsLetterOrDigit(c)) return glob;

        if (known.Contains(ext)) return glob;          // the plural is itself a real extension -> keep it
        string singular = ext[..^1];
        if (!known.Contains(singular)) return glob;    // singular isn't real either -> not a pluralization

        return string.Concat(glob.AsSpan(0, dot + 1), singular);
    }

    /// <summary>
    /// Forces the correct extension glob for any programming-language file-type the user named in
    /// <paramref name="originalQuery"/> (e.g. "c# files" -&gt; <c>*.cs</c>). Small on-device models
    /// can't turn a symbol-bearing language name into a glob — phi-4-mini emits <c>*.c</c> for "c#",
    /// or no glob at all — so this reads the language from the raw query (where the '#'/'++' survives)
    /// and overrides the guess. If the model already produced the canonical extension it is left
    /// alone (so a deliberate "C and C# files" request keeps both <c>*.c</c> and <c>*.cs</c>);
    /// otherwise the symbol-stripped extension the model substituted is removed and the canonical one
    /// added. Mutates <paramref name="include"/> in place.
    /// </summary>
    internal static void ApplyLanguageExtensionGlobs(List<string> include, string? originalQuery)
    {
        foreach (var (canonical, stripped) in LanguageExtensionGlobs.FromQuery(originalQuery))
        {
            // Model already emitted the right extension -> trust it and whatever siblings it kept.
            if (include.Any(g => GlobHasBareExtension(g, canonical))) continue;

            // Model substituted the wrong, symbol-stripped extension (or emitted none) -> replace it.
            if (stripped is not null)
                include.RemoveAll(g => GlobHasBareExtension(g, stripped));
            include.Add("*." + canonical);
        }
    }

    private static readonly Regex ExplicitExtensionRegex = new(
        @"(?<![\w.])\.([A-Za-z0-9]{1,8})\s+(?:files?|extensions?)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Forces <c>*.ext</c> include globs for file extensions the user named EXPLICITLY with a leading
    /// dot in the original query (e.g. <c>".com files on C:"</c> -&gt; <c>*.com</c>). Small models
    /// mishandle uncommon or ambiguous extensions — phi-4-mini turned ".com" (also a TLD) into a
    /// content search for "*.gitignore" — so a literal <c>.ext</c> is taken from the query, not the
    /// model's guess. Only a dotted token is matched (a bare "text files" stays a category phrase the
    /// model maps to *.txt). Returns the bare extensions it forced (empty when none were named).
    /// </summary>
    internal static IReadOnlyList<string> ApplyExplicitExtensionGlobs(List<string> include, string? originalQuery)
    {
        if (string.IsNullOrWhiteSpace(originalQuery))
            return [];

        List<string>? forced = null;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in ExplicitExtensionRegex.Matches(originalQuery))
        {
            string ext = m.Groups[1].Value.ToLowerInvariant();
            if (!seen.Add(ext))
                continue;
            (forced ??= []).Add(ext);
            if (!include.Any(g => GlobHasBareExtension(g, ext)))
                include.Add("*." + ext);
        }
        return (IReadOnlyList<string>?)forced ?? [];
    }

    /// <summary>True when <paramref name="pattern"/> looks like a glob/extension token rather than a
    /// real content search term — i.e. it contains a wildcard or is a bare <c>.ext</c>. Internal so the
    /// empty/bare-<c>.ext</c> branches can be unit-tested directly (it is only reached from
    /// <see cref="Resolve"/> with a non-empty, non-bare pattern).</summary>
    internal static bool LooksLikeGlobToken(string pattern)
    {
        string s = pattern.Trim();
        if (s.Length == 0) return false;
        if (s.IndexOfAny(['*', '?']) >= 0) return true;
        return s[0] == '.' && s.Length > 1 && s.AsSpan(1).IndexOfAnyExcept(BareExtensionChars) < 0;
    }

    private static readonly System.Buffers.SearchValues<char> BareExtensionChars =
        System.Buffers.SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789");

    /// <summary>True when <paramref name="glob"/>'s final extension equals <paramref name="bareExt"/>
    /// (dot-less, case-insensitive), e.g. <c>"*.cs"</c> matches <c>"cs"</c>.</summary>
    private static bool GlobHasBareExtension(string glob, string bareExt)
    {
        int dot = glob.LastIndexOf('.');
        return dot >= 0 && string.Equals(glob[(dot + 1)..].Trim(), bareExt, StringComparison.OrdinalIgnoreCase);
    }

    // "hidden" used as a file-attribute filter: "hidden file(s)/folder(s)/item(s)/…", or a bare
    // "hidden" governed by an include/exclude verb ("show hidden", "exclude hidden"). Anything else
    // (e.g. searching for the literal word "hidden" in file contents) is left to the model.
    private static readonly Regex HiddenFileMentionRegex = new(
        @"\bhidden\s+(?:files?|folders?|directories|director(?:y|ies)|items?|entries|entry|elements?|ones?|stuff|dot[\s-]?files?)\b"
        + @"|\b(?:include|including|show|showing|display|displaying|reveal|revealing|with|exclude|excluding|skip|skipping|ignore|ignoring|omit|omitting|without|no|hide|hiding)\s+hidden\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Negation/exclusion cues that, in the clause preceding a hidden-file mention, mean the user wants
    // hidden items EXCLUDED rather than included.
    private static readonly Regex HiddenExclusionCueRegex = new(
        @"\b(?:not|no|non|without|exclude|excluding|skip|skipping|ignore|ignoring|omit|omitting|except|excepting|hide|hiding|aren'?t|isn'?t|don'?t|doesn'?t|remove|removing|drop|dropping|avoid|avoiding|minus|sans)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly string[] HiddenClauseSeparators = [",", ";", " but ", " and ", " or ", " then "];

    /// <summary>
    /// Detects whether the ORIGINAL query asks to include or exclude hidden files/folders — which Yagu
    /// controls via the "Search hidden files" toggle, not an exclude glob. Returns <c>true</c> to
    /// include hidden items ("hidden files", "show hidden"), <c>false</c> to exclude them ("not hidden",
    /// "no hidden files", "exclude hidden", "without hidden files"), or <c>null</c> when the query does
    /// not mention hidden files.
    /// </summary>
    internal static bool? DetectHiddenFilePreference(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return null;

        var match = HiddenFileMentionRegex.Match(query);
        if (!match.Success)
            return null;

        // Scope the negation scan to the clause that contains the "hidden" word so a negation in an
        // earlier, unrelated clause ("exclude tmp files, show hidden files") doesn't flip the result.
        int hiddenIndex = query.IndexOf("hidden", match.Index, StringComparison.OrdinalIgnoreCase);
        if (hiddenIndex < 0)
            hiddenIndex = match.Index;
        string before = HiddenClauseBefore(query, hiddenIndex);
        return !HiddenExclusionCueRegex.IsMatch(before);
    }

    /// <summary>The text within the same clause leading up to <paramref name="index"/> — i.e. after the
    /// last hard separator (comma/semicolon or a connecting word). Scopes the hidden-files negation scan.</summary>
    private static string HiddenClauseBefore(string query, int index)
    {
        int start = 0;
        foreach (var sep in HiddenClauseSeparators)
        {
            int at = query.LastIndexOf(sep, index, StringComparison.OrdinalIgnoreCase);
            if (at >= 0 && at + sep.Length <= index && at + sep.Length > start)
                start = at + sep.Length;
        }
        return query[start..index];
    }

    /// <summary>True when an exclude token is the model's attempt to filter out hidden/dotfile entries
    /// (a "starts with a dot" matcher such as <c>.*</c>, <c>.?*</c>, <c>.[a-z]+</c>, or
    /// <c>.\.[A-Za-z0-9].+</c>) rather than a real extension/name exclude (which starts with <c>*</c> or
    /// a name character). Dropped when the query's hidden-file intent is handled by the toggle instead.</summary>
    private static bool IsLikelyHiddenExclusionToken(string token)
    {
        string s = token.Trim();
        if (s.StartsWith("**/", StringComparison.Ordinal) || s.StartsWith("**\\", StringComparison.Ordinal))
            s = s[3..].TrimStart();
        return s.Length > 0 && s[0] == '.';
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
    public bool? SearchHiddenFiles { get; init; }

    /// <summary>CLI <c>--sort</c> key (matches, date, size, name, directory), or null when unset.</summary>
    public string? SortBy { get; init; }

    /// <summary>True = descending, false = ascending. Null when no sort field is set.</summary>
    public bool? SortDescending { get; init; }

    /// <summary>CLI <c>--group</c> key (directory, extension, size, modified, created, date), or null when unset.</summary>
    public string? GroupBy { get; init; }

    /// <summary>True reverses the natural group order. Null when no group field is set.</summary>
    public bool? GroupDescending { get; init; }
}

/// <summary>
/// Maps programming-language names a user might name in a search ("c# files", "c++ source") to the
/// canonical file-extension glob. Exists because small on-device models can't reliably turn a
/// language name into an extension — especially the symbol-bearing ones ("c#", "c++", "f#") whose
/// glyph cannot appear in a glob — so phi-4-mini emits the wrong extension ("*.c" for "c#") or none.
/// Reading the language from the raw query lets <see cref="SemanticPlanApplier"/> correct the include
/// filter deterministically, independent of the model's guess.
/// </summary>
internal static class LanguageExtensionGlobs
{
    // Each entry: a matcher for the language name + canonical bare extension + the symbol-stripped
    // extension the model commonly substitutes (null when there is no predictable wrong guess). Only
    // languages whose name carries a glyph a glob can't hold are listed — plain-word languages
    // (python, java, typescript) the model maps correctly on its own, so forcing them risks
    // clobbering a legitimate content search.
    private static readonly (Regex Matcher, string Canonical, string? Stripped)[] Languages =
    [
        (Alias(@"c\s*#|c\s*sharp|csharp"), "cs", "c"),
        (Alias(@"c\s*\+\+|c\s*plus\s*plus|cpp"), "cpp", "c"),
        (Alias(@"f\s*#|f\s*sharp|fsharp"), "fs", "f"),
        (Alias(@"objective[\s-]*c|objc"), "m", null),
    ];

    // The language name must be FOLLOWED by a file-type noun to count as a file filter (so "files
    // about c#" or a bare mention isn't treated as one). The leading lookbehind keeps the name from
    // matching inside a larger token (e.g. "abc# ..."). '#'/'+' are not word chars, so a normal
    // trailing \b can't be used after the symbol — the explicit noun list is the right anchor.
    private static Regex Alias(string core) => new(
        @"(?<![\w#+])(?:" + core + @")\s+(?:source\s+|src\s+)?(?:files?|scripts?|sources?|code|programs?|projects?)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The (canonicalExt, strippedExt) pairs implied by "&lt;language&gt; files" mentions in
    /// <paramref name="query"/>, in first-seen order and de-duplicated by canonical extension. Quoted
    /// spans are ignored so a content term like containing "c#" is never read as a file-type filter.</summary>
    public static IReadOnlyList<(string Canonical, string? Stripped)> FromQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        string scan = Regex.Replace(query, "\"[^\"]*\"|'[^']*'", " ");
        var hits = new List<(string, string?)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (matcher, canonical, stripped) in Languages)
            if (matcher.IsMatch(scan) && seen.Add(canonical))
                hits.Add((canonical, stripped));
        return hits;
    }
}

/// <summary>
/// Supplies the set of "real" bare (dot-less, lower-case) file extensions used to detect and repair
/// extensions that small models sometimes pluralize ("exes" -&gt; "exe"). Combines a curated baseline
/// (Yagu's own skip/binary/archive defaults plus common source/text/document extensions) with the
/// file extensions registered under the local machine's <c>HKEY_CLASSES_ROOT</c>, so machine-specific
/// types are recognized too. The registry lookup is best-effort and cached once: any failure simply
/// falls back to the curated baseline.
/// </summary>
internal static class KnownFileExtensions
{
    // Common source/markup/text/config/document extensions that may not appear in Yagu's
    // skip/binary/archive defaults but are real and must never be treated as a pluralized mistake.
    private const string CuratedExtensions =
        "cs;js;mjs;cjs;jsx;ts;tsx;json;jsonc;xml;yaml;yml;toml;ini;cfg;conf;config;props;targets;" +
        "html;htm;css;scss;sass;less;md;markdown;rst;txt;text;log;csv;tsv;sql;sh;bash;zsh;ps1;psm1;psd1;" +
        "bat;cmd;py;pyw;pyi;rb;go;rs;java;kt;kts;scala;swift;c;h;hpp;hh;cc;cpp;cxx;hxx;m;mm;php;pl;pm;lua;" +
        "r;dart;vb;fs;fsx;fsi;clj;cljs;ex;exs;erl;hrl;jl;groovy;gradle;tf;tfvars;cmake;asm;s;ipynb;" +
        "resx;xaml;razor;cshtml;vue;svelte;sln;csproj;vbproj;fsproj;vcxproj;proj;lock;env";

    private static readonly Lazy<IReadOnlySet<string>> LazySet = new(Build);

    /// <summary>Combined curated + machine-registered known extensions (bare, lower-case).</summary>
    public static IReadOnlySet<string> Default => LazySet.Value;

    private static HashSet<string> Build()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddSemicolonList(set, CuratedExtensions);
        AddSemicolonList(set, AppSettings.DefaultSkipExtensions);
        AddSemicolonList(set, AppSettings.DefaultBinaryExtensions);
        AddSemicolonList(set, AppSettings.DefaultArchiveExtensions);
        TryAddRegisteredExtensions(set);
        return set;
    }

    private static void AddSemicolonList(HashSet<string> set, string list)
    {
        foreach (var raw in list.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string ext = raw.TrimStart('.').Trim();
            if (ext.Length > 0) set.Add(ext);
        }
    }

    private static void TryAddRegisteredExtensions(HashSet<string> set)
    {
        try
        {
            using RegistryKey hkcr = Registry.ClassesRoot;
            foreach (string keyName in hkcr.GetSubKeyNames())
            {
                // Extension keys are the only ones that begin with a dot (e.g. ".png"); ProgIDs do not.
                if (keyName.Length > 1 && keyName[0] == '.')
                    set.Add(keyName[1..]);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException
            or UnauthorizedAccessException or System.IO.IOException or ObjectDisposedException)
        {
            // Best-effort: keep the curated baseline when the registry can't be read.
        }
    }
}
