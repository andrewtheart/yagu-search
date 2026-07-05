using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Yagu.Models;
using System.Security;

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
    bool SearchImageText { get; set; }
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

    /// <summary>Enable binary-file content search — the plan targets known-binary extensions
    /// (.exe/.com/.cpl…) which Yagu otherwise skips. Null = leave unchanged.</summary>
    public bool? SearchBinary { get; init; }

    public bool? SearchHiddenFiles { get; init; }

    /// <summary>Enable "Search image text (OCR)" — reading text inside image files. Null = leave unchanged.</summary>
    public bool? SearchImageText { get; init; }

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

    /// <summary>Bare, lower-case extensions Yagu treats as archive containers (from
    /// <see cref="AppSettings.DefaultArchiveExtensions"/>). Used to deterministically enable "Search
    /// archives" when a plan filters to Office/OpenDocument/zip-family files whose text lives inside
    /// the container.</summary>
    private static readonly Lazy<IReadOnlySet<string>> ArchiveExtensionUniverse =
        new(() => ParseSemicolonExtensions(AppSettings.DefaultArchiveExtensions));

    /// <summary>Bare, lower-case extensions Yagu skips as binary (from
    /// <see cref="AppSettings.DefaultBinaryExtensions"/>). Used to deterministically enable binary
    /// search when a plan filters to known-binary types (.exe/.com/.cpl…).</summary>
    private static readonly Lazy<IReadOnlySet<string>> BinaryExtensionUniverse =
        new(() => ParseSemicolonExtensions(AppSettings.DefaultBinaryExtensions));

    private static HashSet<string> ParseSemicolonExtensions(string semicolonList)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in semicolonList.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string ext = raw.TrimStart('.').Trim();
            if (ext.Length > 0) set.Add(ext);
        }
        return set;
    }

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
        // Deterministically resolve a CLEAR known-folder reference in the query ("files on my desktop",
        // "in my documents", "downloads folder") to its real OS path. A small model can't produce the
        // actual path — for "files on my desktop" it emitted the literal placeholder
        // "C:\User\<your-username>\Desktop" — so read the folder from the query and override the guess.
        if (TryResolveKnownFolder(context.OriginalQuery, out string knownFolder))
            modelDirectory = knownFolder;
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

        // "obey .gitignore" / "do not obey .gitignore" (and synonyms: respect/follow/use vs
        // ignore/disregard/bypass/disable) map to the "Obey .gitignore" toggle. Detect it
        // deterministically from the ORIGINAL query (the small model is unreliable at this) and fall
        // back to the model's obeyGitignore only when the query says nothing about it.
        bool? obeyGitignore = DetectGitignorePreference(context.OriginalQuery) ?? plan.ObeyGitignore;

        // Content-negation guard: a small model turns "log files with error but WITHOUT warning" (a
        // CONTENT exclusion the engine can't express) into a FILENAME exclude glob — often the SAME
        // extension it just included (exclude *.log while include *.log) or a match-everything token
        // (*, *.*). Applying that removes every candidate, so the search returns nothing. Drop excludes
        // that would nuke the include set; genuine narrowing filename excludes (*Legacy*, *.test.*) stay.
        if (RemoveIncludeNullifyingExcludes(include, exclude))
            warnings.Add("Ignored an exclusion that would have removed all matching files.");

        // Yagu cannot express CONTENT exclusion ("files that do NOT contain X"). When the query asks
        // for it ("... but not 'Legacy'", "... but without warning", "doesn't contain X"), warn so the
        // user knows only name/type filters were applied and the negated term was not honored.
        if (context.OriginalQuery is { } negQuery && ContentNegationCue.IsMatch(negQuery))
            warnings.Add("Content exclusion (e.g. \u201Cnot containing X\u201D) isn't supported; only name and type filters were applied.");

        long? minSize = ClampSize(plan.MinFileSizeBytes);
        long? maxSize = ClampSize(plan.MaxFileSizeBytes);

        // A single named DAY is a legitimate range, NOT padding: "modified 2024-06-21" / "modified the
        // day before yesterday" set the SAME token on BOTH bounds, which resolves to that day's
        // 00:00..23:59 window (the "before" bound is pushed to end-of-day). So identical bounds are NOT
        // dropped up front here (doing so used to nuke every single-day query). Genuine phi padding of
        // both bounds with the identical EXACT INSTANT yields an empty window, which is caught AFTER
        // resolution below (after >= before) — a real one-day range never triggers that.
        string? createdAfterRaw = plan.CreatedAfter;
        string? createdBeforeRaw = plan.CreatedBefore;
        string? modifiedAfterRaw = plan.ModifiedAfter;
        string? modifiedBeforeRaw = plan.ModifiedBefore;

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

        // Deterministic relative "recent window" override. A small model mangles single-sided phrases
        // like "in the past 6 months" — phi-4-mini emitted modifiedAfter="6 months" (unparseable) plus a
        // hallucinated modifiedBefore, which the empty-window guard below then dropped, leaving NO date
        // filter. When the ORIGINAL query states such a window, compute the after-bound in C# (relative
        // to context.Now) and drop the — usually hallucinated — before-bound for that family, so the
        // filter is applied reliably regardless of what the model produced.
        if (TryDetectRelativeDateWindow(context.OriginalQuery, context.Now, out var relativeAfter, out bool relativeTargetsCreated))
        {
            if (relativeTargetsCreated) { createdAfter = relativeAfter; createdBefore = null; }
            else { modifiedAfter = relativeAfter; modifiedBefore = null; }
        }

        // Empty/inverted window guard: a resolved after-bound at or past its before-bound matches
        // nothing (the model padded both bounds with the identical exact INSTANT, e.g. a full
        // timestamp). A one-day range never hits this — date-only / relative-day tokens resolve to
        // 00:00 < 23:59:59 — so single-day queries survive.
        if (createdAfter is { } caLo && createdBefore is { } caHi && caLo >= caHi)
        {
            createdAfter = createdBefore = null;
            warnings.Add("Ignored a contradictory created-date range (empty window).");
        }
        if (modifiedAfter is { } maLo && modifiedBefore is { } maHi && maLo >= maHi)
        {
            modifiedAfter = modifiedBefore = null;
            warnings.Add("Ignored a contradictory modified-date range (empty window).");
        }

        SearchMode? mode = ParseSearchMode(plan.SearchMode, warnings);

        var (sortModeIndex, sortDirectionIndex) = ParseSort(plan.SortBy, plan.SortDirection, warnings);
        var (groupMode, groupDirectionIndex) = ParseGroup(plan.GroupBy, plan.GroupDirection, warnings);

        string? pattern = string.IsNullOrWhiteSpace(plan.Pattern) ? null : plan.Pattern!.Trim();
        bool? useRegex = plan.UseRegex;

        // Guard against a runaway / pathological model pattern. Observed: phi-4 emitting a ~700-char
        // repeated "\b(?:\s+\w+\s+){1,}\b(?:…)" regex for "C# files with async methods…", which also
        // burned the whole output budget so the rest of the plan (the date filter) was truncated away.
        // Such a pattern matches nothing useful and, as a regex, risks catastrophic backtracking. Drop
        // it (and its regex flag) so the surviving filters — e.g. the *.cs include — drive a sane result
        // instead of garbage.
        if (pattern is not null && IsDegenerateSearchPattern(pattern))
        {
            warnings.Add("Ignored an unusable search pattern produced by the AI model.");
            pattern = null;
            useRegex = null;
        }

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

        // Finding TEXT inside image files requires OCR ("Search image text"). A request like
        // "png files with the word CUDA in it" carries a real content term whose include filter targets
        // image extensions, but the engine can only match that text via OCR. Enable it deterministically
        // when the search has a content term, is not a filename-only listing, and its include filter is
        // image-typed. The model can also request it explicitly via searchImageText.
        bool? searchImageText = plan.SearchImageText;
        if (searchImageText != true
            && pattern is not null
            && mode != Models.SearchMode.FileNames
            && include.Exists(IsImageExtensionToken))
        {
            searchImageText = true;
        }

        // Office Open XML / OpenDocument / zip-family files are archives — their text lives INSIDE the
        // container and is only reachable with "Search archives" on. When the plan filters to such
        // extensions, enable it deterministically (small models routinely forget searchInsideArchives).
        // This flows to BOTH the GUI and the CLI via the resolved plan; the GUI additionally adds the
        // specific extensions to its archive set and syncs the dropdown.
        bool? searchInsideArchives = plan.SearchInsideArchives;
        if (searchInsideArchives != true
            && GetArchiveExtensionsToEnable(include, ArchiveExtensionUniverse.Value).Count > 0)
            searchInsideArchives = true;

        // Yagu skips known-binary content by default, so a content search scoped to binary types
        // (.exe/.com/.cpl…) would read nothing. When the plan filters to such extensions, enable binary
        // search deterministically (again flowing to both consumers; the GUI additionally un-skips only
        // the targeted types in its dropdown). Null when no binary type is targeted.
        bool? searchBinary = GetBinaryExtensionsToEnable(include, BinaryExtensionUniverse.Value).Count > 0
            ? true
            : null;

        // A request like "all png files modified last year" carries no text term — the model emits
        // an empty pattern and relies purely on globs/metadata filters. Yagu's engine yields nothing
        // for an empty query, so synthesize a match-all filename query (regex ".") that enumerates
        // every file passing the include/exclude/date/size filters.
        bool hasFileFilter = include.Count > 0 || exclude.Count > 0 ||
            minSize.HasValue || maxSize.HasValue ||
            createdAfter.HasValue || createdBefore.HasValue ||
            modifiedAfter.HasValue || modifiedBefore.HasValue;
        // A "directive-only" query ("documents sorted by name", "files grouped by folder", "search
        // hidden files") carries no text term — the model emits an empty pattern and relies purely on
        // the sort/group/scope switches. Treat those the same as a file filter so we synthesize a
        // match-all (".") the directives act on, instead of literal-searching the whole sentence in the
        // empty-plan fallback below (the eval showed 35 queries turned into a literal search of their
        // own text this way).
        bool hasDirective = hasFileFilter
            || sortModeIndex is not null || groupMode is not null
            || searchHidden == true || searchInsideArchives == true
            || searchBinary == true || searchImageText == true;
        if (pattern is null && (hasDirective || mode == Models.SearchMode.FileNames))
        {
            pattern = ".";
            useRegex = true;
            mode ??= Models.SearchMode.FileNames;
        }

        // Empty/unusable plan (e.g. the model returned "{}"): with no pattern and nothing to enumerate,
        // the search would do nothing. Fall back to a literal substring search of the user's typed text
        // so it still searches something — the same intent as the "AI couldn't interpret" fallback, but
        // reachable here because a valid-but-empty plan counts as success and bypasses that path. Both
        // the GUI and the CLI populate context.OriginalQuery, so this works for both.
        if (pattern is null && !string.IsNullOrWhiteSpace(context.OriginalQuery))
        {
            pattern = context.OriginalQuery!.Trim();
            useRegex = false;
        }

        // Natural-language queries express SUBSTRING intent ("image files with 'a' in them",
        // "files containing report") — never whole-word boundaries. The UI's ExactMatch toggle
        // defaults to whole-word (true), so without forcing a default here a semantic content search
        // for "a" would compile to the whole-word regex \ba\b and match nothing inside words like
        // "BANANA". Default a model-translated search to substring matching; the model can still opt
        // into whole-word by emitting exactMatch=true. (Harmless when UseRegex is set — the regex
        // path ignores ExactMatch — so this is a safe blanket default.)
        bool? exactMatch = plan.ExactMatch ?? false;

        var resolved = new ResolvedSearchPlan
        {
            Directory = directory,
            Pattern = pattern,
            SearchMode = mode,
            CaseSensitive = plan.CaseSensitive,
            UseRegex = useRegex,
            ExactMatch = exactMatch,
            IncludeGlobs = include.Count > 0 ? include : null,
            ExcludeGlobs = exclude.Count > 0 ? exclude : null,
            MinFileSizeBytes = minSize,
            MaxFileSizeBytes = maxSize,
            CreatedAfterDate = createdAfter,
            CreatedBeforeDate = createdBefore,
            ModifiedAfterDate = modifiedAfter,
            ModifiedBeforeDate = modifiedBefore,
            MaxSearchDepth = plan.MaxSearchDepth is { } d ? Math.Max(0, d) : null,
            ObeyGitignore = obeyGitignore,
            SearchInsideArchives = searchInsideArchives,
            SearchBinary = searchBinary,
            SearchHiddenFiles = searchHidden,
            SearchImageText = searchImageText,
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
    internal static string DescribeResolved(ResolvedSearchPlan r)
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
        if (r.SearchBinary is { } sb) parts.Add($"searchBinary={sb}");
        if (r.SearchHiddenFiles is { } sh) parts.Add($"searchHidden={sh}");
        if (r.SearchImageText is { } oit) parts.Add($"searchImageText={oit}");
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
        if (resolved.SearchImageText is { } sit) target.SearchImageText = sit;

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
        => BuildExplanation(resolved, effectiveDirectory: null);

    /// <summary>
    /// Same as <see cref="BuildExplanation(ResolvedSearchPlan)"/> but, when the model did not resolve
    /// an explicit directory, describes the <paramref name="effectiveDirectory"/> the search will
    /// actually use (the current directory box). This keeps the summary truthful: the GUI deliberately
    /// does not seed the model with the box value, so <see cref="ResolvedSearchPlan.Directory"/> is
    /// null for an unscoped query even though the search still honors whatever is in the box. Only a
    /// genuinely empty effective directory is described as "all drives".
    /// </summary>
    public static string BuildExplanation(ResolvedSearchPlan resolved, string? effectiveDirectory)
    {
        ArgumentNullException.ThrowIfNull(resolved);

        string directory = !string.IsNullOrWhiteSpace(resolved.Directory)
            ? resolved.Directory!.Trim()
            : !string.IsNullOrWhiteSpace(effectiveDirectory)
                ? effectiveDirectory!.Trim()
                : "all drives";

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

        if (resolved.SearchInsideArchives == true)
            sb.Append(", searching inside archives");

        if (resolved.SearchBinary == true)
            sb.Append(", including binary files");

        if (resolved.SearchHiddenFiles is { } hidden)
            sb.Append(hidden ? ", including hidden files" : ", excluding hidden files");

        if (resolved.ObeyGitignore is { } obeyGit)
            sb.Append(obeyGit ? ", obeying .gitignore" : ", ignoring .gitignore");

        if (resolved.SearchImageText == true)
            sb.Append(", reading text inside images (OCR)");

        AppendSizeClause(sb, resolved.MinFileSizeBytes, resolved.MaxFileSizeBytes);
        AppendDateClause(sb, "modified", resolved.ModifiedAfterDate, resolved.ModifiedBeforeDate);
        AppendDateClause(sb, "created", resolved.CreatedAfterDate, resolved.CreatedBeforeDate);
        AppendSortClause(sb, resolved.SortModeIndex, resolved.SortDirectionIndex);
        AppendGroupClause(sb, resolved.GroupMode, resolved.GroupSortDirectionIndex);

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

    /// <summary>Describes the resolved sort (mode index 1=matches, 2=modified date, 3=size, 4=name,
    /// 5=folder; direction 1=ascending, else descending). No-op when no sort is set.</summary>
    private static void AppendSortClause(StringBuilder sb, int? modeIndex, int? directionIndex)
    {
        if (modeIndex is not { } m || m <= 0) return;

        string? field = m switch
        {
            1 => "match count",
            2 => "modified date",
            3 => "size",
            4 => "name",
            5 => "folder",
            _ => null,
        };
        if (field is null) return;

        bool asc = directionIndex == 1;
        string dir = m switch
        {
            1 => asc ? "fewest first" : "most first",
            2 => asc ? "oldest first" : "newest first",
            3 => asc ? "smallest first" : "largest first",
            _ => asc ? "A\u2013Z" : "Z\u2013A", // name / folder
        };
        sb.Append(", sorted by ").Append(field).Append(" (").Append(dir).Append(')');
    }

    /// <summary>Describes the resolved grouping (folder / file type / size / modified / created / date).
    /// No-op when grouping is None/unset. A reversed group order is noted.</summary>
    private static void AppendGroupClause(StringBuilder sb, GroupMode? mode, int? directionIndex)
    {
        if (mode is not { } g || g == GroupMode.None) return;

        string label = g switch
        {
            GroupMode.Folder => "folder",
            GroupMode.Extension => "file type",
            GroupMode.FileSize => "size",
            GroupMode.DateRangeModified => "modified date",
            GroupMode.DateRangeCreated => "created date",
            _ => "date",
        };
        sb.Append(", grouped by ").Append(label);
        if (directionIndex == 1)
            sb.Append(" (reversed)");
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
            SearchBinary = resolved.SearchBinary,
            SearchHiddenFiles = resolved.SearchHiddenFiles,
            SearchImageText = resolved.SearchImageText,
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
        // Repair the malformed leading ".*"/".*." extension forms small models emit (".*.py" or
        // ".*png" instead of "*.py"/"*.png") — the leading dot makes them match nothing. A bare ".*"
        // (a dotfile glob) has no extension after it, so the anchored pattern leaves it untouched.
        token = Regex.Replace(token, @"^\.\*\.?([A-Za-z0-9]+)$", "*.$1");
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
        foreach (var (canonical, substituted, alwaysStrip) in LanguageExtensionGlobs.FromQuery(originalQuery))
        {
            // 1. Remove forms that are NEVER real file extensions (*.c#, *.c++, *.f#, *.objective-c,
            //    *.javascript, ...), even when the model ALSO emitted the correct glob.
            foreach (var bogus in alwaysStrip)
                include.RemoveAll(g => GlobHasBareExtension(g, bogus));

            // 2. Model already emitted the canonical extension -> trust it and whatever real siblings
            //    it kept (so "C and C# files" keeps a deliberate *.c).
            if (include.Any(g => GlobHasBareExtension(g, canonical))) continue;

            // 3. Canonical missing -> the model substituted a real-but-wrong extension (or emitted
            //    none); drop the substitute and add the canonical one.
            if (substituted is not null)
                include.RemoveAll(g => GlobHasBareExtension(g, substituted));
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

    /// <summary>No natural-language-derived search term or regex is anywhere near this long.</summary>
    private const int MaxReasonableSearchPatternLength = 200;

    /// <summary>A plausible NL-translated regex rarely stacks this many non-capturing groups; a
    /// runaway repetition does.</summary>
    private const int DegenerateNonCapturingGroupCount = 5;

    /// <summary>A real regex almost never stacks this many bare <c>\w</c> quantifiers outside a
    /// character class; a letter-spelling runaway (password -&gt; <c>p\w*?a\w*?s\w*?…</c>) does.</summary>
    private const int DegenerateWordQuantifierCount = 4;

    /// <summary>A real regex rarely uses more than a couple of <c>\b</c> word boundaries; a runaway
    /// stacks many (observed: "the word secret" -&gt; <c>*\b\w*\b\w*\b\b\b\b\b…</c>). Six is well
    /// above any legitimate NL-translated regex (<c>\bTODO\b|\bFIXME\b</c> has 4) yet far below a runaway.</summary>
    private const int DegenerateWordBoundaryCount = 6;

    /// <summary>
    /// True when <paramref name="pattern"/> is a pathological / runaway search term rather than a real
    /// one — chiefly a model that ran away generating a huge repeated regex (observed: phi-4 emitting a
    /// ~700-char <c>\b(?:\s+\w+\s+){1,}\b(?:…)</c> for "async methods"). Such a pattern matches nothing
    /// useful and, as a regex, risks catastrophic backtracking, so it is rejected and the other filters
    /// drive the search. Legitimate terms ("async", <c>async\s+Task&lt;</c>, <c>\b(TODO|FIXME)\b</c>) are
    /// short and stack few groups, so they pass.
    /// </summary>
    internal static bool IsDegenerateSearchPattern(string? pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return false;
        if (pattern.Length > MaxReasonableSearchPatternLength) return true;
        int groups = 0;
        for (int i = pattern.IndexOf("(?:", StringComparison.Ordinal); i >= 0; i = pattern.IndexOf("(?:", i + 3, StringComparison.Ordinal))
            if (++groups >= DegenerateNonCapturingGroupCount) return true;

        // Letter-spelling runaway: some models spell a word out with a "\w*?" gap between every letter
        // (observed: password -> ".*?\b(?:p\w*?a\w*?s\w*?s\w*?w\w*?\w*?\b)"). A genuine regex almost never
        // stacks this many bare \w quantifiers (a real one keeps \w inside a character class like
        // [\w.-]+, where \w is not immediately followed by * or +).
        int wordQuantifiers = 0;
        for (int i = pattern.IndexOf(@"\w", StringComparison.Ordinal); i >= 0; i = pattern.IndexOf(@"\w", i + 2, StringComparison.Ordinal))
        {
            char next = i + 2 < pattern.Length ? pattern[i + 2] : '\0';
            if ((next == '*' || next == '+') && ++wordQuantifiers >= DegenerateWordQuantifierCount)
                return true;
        }

        // Word-boundary runaway: a model can spew a long run of \b tokens (observed: "the word secret"
        // -> "*\b\w*\b\w*\b\b\b\b\b…"). This slips past the \w-quantifier and (?: checks, so count \b
        // directly; a legitimate regex never needs this many word boundaries.
        int wordBoundaries = 0;
        for (int i = pattern.IndexOf(@"\b", StringComparison.Ordinal); i >= 0; i = pattern.IndexOf(@"\b", i + 2, StringComparison.Ordinal))
            if (++wordBoundaries >= DegenerateWordBoundaryCount) return true;

        return false;
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

    /// <summary>Detects a request for CONTENT exclusion the engine can't honor ("... but not X",
    /// "... but without Y", "doesn't contain Z"). Deliberately narrow ("but not/without/no", "don't/
    /// doesn't contain") so it doesn't fire on folder/hidden negations like "not in hidden folders".</summary>
    private static readonly Regex ContentNegationCue = new(
        @"\bbut\s+(?:not|without|no)\b|\b(?:don'?t|doesn'?t|does\s+not)\s+contain",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DesktopFolderRef = new(
        @"\bon\s+(?:my\s+|the\s+)?desktop\b|\b(?:my|the)\s+desktop\b|\bdesktop\s+folder\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DocumentsFolderRef = new(
        @"\bmy\s+documents\b|\bdocuments\s+folder\b|\bin\s+(?:my\s+)?documents\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DownloadsFolderRef = new(
        @"\bmy\s+downloads\b|\bdownloads\s+folder\b|\bin\s+(?:my\s+)?downloads\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Resolves a CLEAR known-folder reference in the query to its real OS path (Desktop,
    /// Documents, Downloads). Conservative on purpose: bare "documents" is NOT matched (it usually
    /// means document FILES, e.g. "documents mentioning budget") — only phrasings that clearly name the
    /// folder ("my documents", "documents folder", "in documents"). Returns false when no clear
    /// reference is present or the resolved folder does not exist.</summary>
    internal static bool TryResolveKnownFolder(string? originalQuery, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(originalQuery)) return false;

        string? candidate = null;
        if (DesktopFolderRef.IsMatch(originalQuery))
            candidate = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        else if (DocumentsFolderRef.IsMatch(originalQuery))
            candidate = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        else if (DownloadsFolderRef.IsMatch(originalQuery))
        {
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            candidate = string.IsNullOrEmpty(profile) ? null : Path.Combine(profile, "Downloads");
        }

        if (string.IsNullOrEmpty(candidate) || !Directory.Exists(candidate))
            return false;
        path = candidate;
        return true;
    }

    /// <summary>Removes exclude globs that would eliminate the entire include set — a match-everything
    /// token (<c>*</c>/<c>*.*</c>) or one that exactly matches an include glob (the small-model
    /// content-negation artifact, e.g. include <c>*.log</c> + exclude <c>*.log</c>). Returns true when
    /// any were removed. Genuine narrowing filename excludes (<c>*Legacy*</c>, <c>*.test.*</c>) are
    /// left intact.</summary>
    private static bool RemoveIncludeNullifyingExcludes(List<string> include, List<string> exclude)
    {
        if (exclude.Count == 0) return false;
        int before = exclude.Count;
        exclude.RemoveAll(x =>
        {
            string t = x.Trim();
            if (t is "*" or "*.*") return true;
            return include.Exists(g => string.Equals(g.Trim(), t, StringComparison.OrdinalIgnoreCase));
        });
        return exclude.Count < before;
    }

    /// <summary>
    /// True when <paramref name="token"/> is an image-file filter (glob or bare extension) whose
    /// extension is one Yagu can OCR (png/jpg/jpeg/bmp/gif/tif/tiff/webp). Used to enable
    /// "Search image text" when a content search targets image files.
    /// </summary>
    private static bool IsImageExtensionToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        string t = token.Trim();
        string ext = Path.GetExtension(t);
        if (string.IsNullOrEmpty(ext))
            ext = t; // bare token like "png"
        ext = ext.TrimStart('.', '*');
        return Yagu.Services.Ocr.ImageOcrSupport.DefaultImageExtensions.Contains(ext);
    }

    // "hidden" used as a file-attribute filter: "hidden file(s)/folder(s)/item(s)/…", or a bare
    // "hidden" governed by an include/exclude verb ("show hidden", "exclude hidden"). Anything else
    // (e.g. searching for the literal word "hidden" in file contents) is left to the model.
    private static readonly Regex HiddenFileMentionRegex = new(
        @"\bhidden\s+(?:files?|folders?|directories|director(?:y|ies)|items?|entries|entry|elements?|ones?|stuff|dot[\s-]?files?)\b"
        + @"|\b(?:include|including|show|showing|display|displaying|reveal|revealing|with|exclude|excluding|skip|skipping|ignore|ignoring|omit|omitting|without|no|not|hide|hiding)\s+hidden\b",
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

    // ".gitignore" behavior directive detection. Matches ".gitignore", "gitignore", "git ignore",
    // "git-ignore", and the "gitignored" participle; the leading verb (scanned in the clause BEFORE the
    // token, so the token's own "ignore" is never counted) decides obey vs don't-obey.
    private static readonly Regex GitignoreTokenRegex = new(
        @"\.?\bgit[\s-]?ignore(d)?\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex GitignoreObeyVerb = new(
        @"\b(?:obey|obeying|respect|respecting|honou?r|honou?ring|follow|following|use|using|apply|applying|enforce|enforcing|observe|observing|heed|heeding|enable|enabling|abide)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex GitignoreDisobeyVerb = new(
        @"\b(?:ignore|ignoring|disregard|disregarding|bypass|bypassing|disable|disabling|skip|skipping|omit|omitting|override|overriding|without|no)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex GitignoreExcludeCue = new(
        @"\b(?:exclude|excluding|without|skip|skipping|omit|omitting|hide|hiding|no|not|remove|removing|drop|dropping|except)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex GitignoreNegationCue = new(
        @"\b(?:not|never|don'?t|doesn'?t|didn'?t|won'?t|cannot|can'?t|shouldn'?t|wouldn'?t)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Detects whether the ORIGINAL query asks to obey or ignore <c>.gitignore</c> — Yagu's "Obey
    /// .gitignore" toggle. Returns <c>true</c> for "obey/respect/follow/use .gitignore", <c>false</c>
    /// for "do not obey/ignore/disregard/bypass .gitignore" or "include gitignored files", and
    /// <c>null</c> when the query has no .gitignore behavior directive (e.g. a search for the
    /// <c>.gitignore</c> file itself). A negation in the same clause flips the polarity, so both
    /// "do not obey .gitignore" (false) and "don't ignore .gitignore" (true) resolve correctly.
    /// </summary>
    internal static bool? DetectGitignorePreference(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return null;

        // Ignore quoted spans so a literal content search (containing "obey gitignore") isn't hijacked.
        string scan = Regex.Replace(query, "\"[^\"]*\"|'[^']*'", " ");

        var m = GitignoreTokenRegex.Match(scan);
        if (!m.Success)
            return null;

        bool ignoredFilesForm = m.Groups[1].Success; // "gitignored"
        // The governing clause up to (but not including) the .gitignore token.
        string before = HiddenClauseBefore(scan, m.Index);

        bool? obey;
        if (GitignoreObeyVerb.IsMatch(before))
            obey = !GitignoreNegationCue.IsMatch(before);      // "do not obey" -> false
        else if (GitignoreDisobeyVerb.IsMatch(before))
            obey = GitignoreNegationCue.IsMatch(before);       // "don't ignore" -> true
        else if (ignoredFilesForm)
            // "gitignored files": excluding/hiding them = obey; otherwise the user wants to see them.
            obey = GitignoreExcludeCue.IsMatch(before);
        else
            return null; // a bare ".gitignore" mention with no behavior directive = a filename search.

        return obey;
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
        new(@"^\s*(\d+)\s*(hour|day|week|month|year)s?\s*ago\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LastN =
        new(@"^\s*(?:in\s+the\s+)?(?:last|past)\s+(\d+)?\s*(hour|day|week|month|year)s?\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // A bare "N units" AFTER-bound with no "ago"/"last"/"past" — small models emit e.g.
    // modifiedAfter="6 months" for "in the past 6 months"; treat it as "N units ago".
    private static readonly Regex BareUnits =
        new(@"^\s*(\d+)\s*(hour|day|week|month|year)s?\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Deterministic relative "recent window" detection read from the ORIGINAL query (model-independent,
    // like the language/extension/hidden extractors). Small models mangle "in the past 6 months" into an
    // unparseable bare unit plus a hallucinated fixed before-bound, so we compute the window ourselves.
    private static readonly Regex RelativePastWindow =
        new(@"\b(?:in|within|over|during|for)?\s*(?:the\s+)?past\s+(\d+)?\s*(hour|day|week|month|year)s?\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex RelativeLastWindow =
        new(@"\b(?:in|within|over|during|for)?\s*(?:the\s+)?last\s+(\d+)\s+(hour|day|week|month|year)s?\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex RelativeAgoWindow =
        new(@"\b(\d+)\s+(hour|day|week|month|year)s?\s+ago\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CreatedCue =
        new(@"\bcreat|\bmade\b|\badded\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex NegatedWindowPrefix =
        new(@"\b(?:not|never|without|no\s+longer|haven'?t|hasn'?t|hadn'?t|isn'?t|aren'?t|wasn'?t|weren'?t|don'?t|doesn'?t|didn'?t)\b[\w\s'-]{0,30}$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

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
            // Chronic (nChronic) does NOT parse "the day before yesterday" / "the day after tomorrow",
            // so resolve these common single-day phrases explicitly (= today -/+ 2 days).
            case "the day before yesterday":
            case "day before yesterday": return DayBound(now.AddDays(-2), endOfDay);
            case "the day after tomorrow":
            case "day after tomorrow": return DayBound(now.AddDays(2), endOfDay);
            case "tomorrow": return DayBound(now.AddDays(1), endOfDay);
        }

        var ago = RelativeAgo.Match(v);
        if (ago.Success && int.TryParse(ago.Groups[1].Value, out int agoN))
            return ShiftUnits(now, ago.Groups[2].Value, -agoN);

        var last = LastN.Match(v);
        if (last.Success)
        {
            int n = last.Groups[1].Success && int.TryParse(last.Groups[1].Value, out int parsed) ? parsed : 1;
            return ShiftUnits(now, last.Groups[2].Value, -n);
        }

        // A bare "N units" (no "ago"/"last"/"past") in an AFTER bound almost always means "N units ago"
        // (e.g. a weak model emits modifiedAfter="6 months" for "in the past 6 months"). Resolve it as a
        // past instant. Only for after-bounds (endOfDay is false); a bare unit in a "before" bound is
        // ambiguous, so leave that to Chronic / the warning path.
        if (!endOfDay)
        {
            var bare = BareUnits.Match(v);
            if (bare.Success && int.TryParse(bare.Groups[1].Value, out int bareN))
                return ShiftUnits(now, bare.Groups[2].Value, -bareN);
        }

        // Date-only (yyyy-MM-dd and friends) -> day bound; otherwise full timestamp.
        if (DateOnlyParses(v))
        {
            if (DateTimeOffset.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dateOnly))
                return DayBound(dateOnly, endOfDay);
        }
        if (DateTimeOffset.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt))
            return dt;

        // Natural-language phrases the fast paths above don't cover ("this week", "last month",
        // "past 24 hours", "last December", "since last Monday", "next friday", ...) are handled by the
        // vendored Chronic parser (nChronic). It resolves relative to a supplied clock, so results stay
        // deterministic and testable. It runs LAST so the explicit fast paths keep their exact behavior
        // and Chronic only rescues phrases we would otherwise have dropped.
        if (TryResolveWithChronic(v, now, endOfDay, out var chronicResult))
            return chronicResult;

        warnings.Add($"Could not interpret {field} value '{raw}'; ignored.");
        return null;
    }

    /// <summary>Shifts an instant by a whole number of hour/day/week/month/year units. Callers only
    /// ever pass hour|day|week|month|year, so the default arm ("year") keeps every branch reachable.</summary>
    private static DateTimeOffset ShiftUnits(DateTimeOffset from, string unit, int amount) => unit.ToLowerInvariant() switch
    {
        "hour" => from.AddHours(amount),
        "day" => from.AddDays(amount),
        "week" => from.AddDays(amount * 7),
        "month" => from.AddMonths(amount),
        _ => from.AddYears(amount),
    };

    /// <summary>Detects a single-sided "recent window" date phrase ("in the past 6 months", "modified
    /// within the last 30 days", "created 2 weeks ago") in the ORIGINAL query and returns the resulting
    /// after-bound. Model-independent because a small model mangles these into an unparseable bare unit
    /// plus a hallucinated fixed before-bound. Phrases inside quotes, and negated windows ("NOT modified
    /// in the last 6 months" = an older-than/before bound), are ignored.</summary>
    internal static bool TryDetectRelativeDateWindow(string? query, DateTimeOffset now, out DateTimeOffset afterBound, out bool targetsCreated)
    {
        afterBound = default;
        targetsCreated = false;
        if (string.IsNullOrWhiteSpace(query)) return false;

        string scan = Regex.Replace(query, "\"[^\"]*\"|'[^']*'", " ");

        int n;
        string unit;
        Match m;
        if ((m = RelativePastWindow.Match(scan)).Success)
        {
            n = m.Groups[1].Success && int.TryParse(m.Groups[1].Value, out int pn) ? pn : 1;
            unit = m.Groups[2].Value;
        }
        else if ((m = RelativeLastWindow.Match(scan)).Success)
        {
            n = int.TryParse(m.Groups[1].Value, out int ln) ? ln : 0;
            unit = m.Groups[2].Value;
        }
        else if ((m = RelativeAgoWindow.Match(scan)).Success)
        {
            n = int.TryParse(m.Groups[1].Value, out int an) ? an : 0;
            unit = m.Groups[2].Value;
        }
        else
        {
            return false;
        }

        if (n <= 0) return false;

        // Defer a negated window ("not modified in the last 6 months") to the model — it means a
        // before/older-than bound we don't infer here, so firing the after-bound would be wrong.
        if (NegatedWindowPrefix.IsMatch(scan[..m.Index])) return false;

        afterBound = ShiftUnits(now, unit, -n);
        targetsCreated = CreatedCue.IsMatch(scan);
        return true;
    }

    private static bool DateOnlyParses(string v) =>
        Regex.IsMatch(v, @"^\d{4}[-/]\d{1,2}[-/]\d{1,2}$");

    private static DateTimeOffset DayBound(DateTimeOffset value, bool endOfDay)
    {
        var d = new DateTimeOffset(value.Year, value.Month, value.Day, 0, 0, 0, value.Offset);
        return endOfDay ? d.AddDays(1).AddTicks(-1) : d;
    }

    // Cached: the Chronic parser builds its handler registry once in the ctor; Parse(phrase, options)
    // takes the per-call clock. Constructed with default options (Middle endian / US date order),
    // matching the ISO/explicit paths that run before Chronic.
    private static readonly global::Chronic.Parser ChronicParser = new();

    /// <summary>Last-resort natural-language date resolution via the vendored Chronic parser. Returns
    /// the START of the matched span for "after" bounds and the END for "before" bounds, so a named
    /// period ("this week", "last month") is inclusive. Chronic can throw on unparseable input, which
    /// is treated as "not interpretable" (returns false).</summary>
    private static bool TryResolveWithChronic(string phrase, DateTimeOffset now, bool endOfDay, out DateTimeOffset result)
    {
        result = default;
        try
        {
            var options = new global::Chronic.Options { Clock = () => now.DateTime };
            var span = ChronicParser.Parse(phrase, options);
            DateTime? picked = endOfDay ? (span?.End ?? span?.Start) : (span?.Start ?? span?.End);
            if (picked is not { } dtp) return false;
            result = new DateTimeOffset(DateTime.SpecifyKind(dtp, DateTimeKind.Unspecified), now.Offset);
            return true;
        }
        catch
        {
            return false;
        }
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

    /// <summary>Enable binary-file content search (CLI maps this to <c>SkipBinary=false</c>). Null when unset.</summary>
    public bool? SearchBinary { get; init; }

    public bool? SearchHiddenFiles { get; init; }

    /// <summary>Enable "Search image text (OCR)". Null when unset.</summary>
    public bool? SearchImageText { get; init; }

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
    // Canonical extension + optional "substituted" ext (a REAL extension the model uses instead of
    // canonical — stripped only when canonical is absent, so "C and C# files" keeps a deliberate *.c)
    // + "always-strip" forms that are never real file extensions (the glyph/word forms a model emits:
    // *.c#, *.c++, *.f#, *.objective-c, *.javascript, ...), removed unconditionally.
    private static readonly (Regex Matcher, string Canonical, string? Substituted, string[] AlwaysStrip)[] Languages =
    [
        (Alias(@"c\s*#|c\s*sharp|csharp"), "cs", "c", ["c#"]),
        (Alias(@"c\s*\+\+|c\s*plus\s*plus|cpp"), "cpp", "c", ["c++"]),
        (Alias(@"f\s*#|f\s*sharp|fsharp"), "fs", "f", ["f#"]),
        (Alias(@"objective[\s-]*c|objc"), "m", null, ["objective-c", "objc"]),
        // Plain-word languages the model still mis-globs (observed: *.javascript, *.typescript,
        // ruby -> *.rs). The Alias regex requires "<lang> files/scripts/...", so a content search like
        // "files containing javascript" is never rewritten into a filter.
        (Alias(@"javascript"), "js", null, ["javascript"]),
        (Alias(@"typescript"), "ts", null, ["typescript"]),
        (Alias(@"ruby"), "rb", "rs", []),
    ];

    // The language name must be FOLLOWED by a file-type noun to count as a file filter (so "files
    // about c#" or a bare mention isn't treated as one). The leading lookbehind keeps the name from
    // matching inside a larger token (e.g. "abc# ..."). '#'/'+' are not word chars, so a normal
    // trailing \b can't be used after the symbol — the explicit noun list is the right anchor. The
    // optional "or/and/, <word>" bridge lets a COORDINATED list share one trailing noun, so both
    // languages in "typescript or javascript files" / "c and c# files" are detected.
    private static Regex Alias(string core) => new(
        @"(?<![\w#+])(?:" + core + @")\s+(?:(?:or|and|,|/)\s+\w+\s+)*(?:source\s+|src\s+)?(?:files?|scripts?|sources?|code|programs?|projects?)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The (canonicalExt, strippedExt) pairs implied by "&lt;language&gt; files" mentions in
    /// <paramref name="query"/>, in first-seen order and de-duplicated by canonical extension. Quoted
    /// spans are ignored so a content term like containing "c#" is never read as a file-type filter.</summary>
    public static IReadOnlyList<(string Canonical, string? Substituted, string[] AlwaysStrip)> FromQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        string scan = Regex.Replace(query, "\"[^\"]*\"|'[^']*'", " ");
        var hits = new List<(string, string?, string[])>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (matcher, canonical, substituted, alwaysStrip) in Languages)
            if (matcher.IsMatch(scan) && seen.Add(canonical))
                hits.Add((canonical, substituted, alwaysStrip));
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
        catch (Exception ex) when (ex is SecurityException
            or UnauthorizedAccessException or IOException or ObjectDisposedException)
        {
            // Best-effort: keep the curated baseline when the registry can't be read.
        }
    }
}
