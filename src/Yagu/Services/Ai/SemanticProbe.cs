using Yagu.Models;

namespace Yagu.Services.Ai;

/// <summary>How demanding a qualification probe is. The set is deliberately mixed: a model must handle
/// both trivial extension lookups and multi-constraint requests to be trusted as the recommended pick.</summary>
public enum SemanticProbeComplexity
{
    /// <summary>A single-constraint request (e.g. an extension or a bare content word).</summary>
    Simple,

    /// <summary>A multi-constraint request (e.g. a file type combined with a content term or a date range).</summary>
    Complex,
}

/// <summary>
/// One model-qualification probe: a natural-language query plus the resolved-plan expectations that a
/// competent model MUST satisfy. Expectations are intentionally sparse — only the robust, unambiguous
/// signals a good small model reliably produces (extension → include glob, content word → pattern,
/// date phrasing → a date filter) — so scoring distinguishes a capable model from a broken one without
/// over-fitting to a single model's exact phrasing.
/// <para>
/// Every non-null expectation is an AND-condition: the probe passes only when the model's
/// <see cref="ResolvedSearchPlan"/> satisfies all of them (see <see cref="SemanticProbeScorer"/>).
/// </para>
/// </summary>
public sealed class SemanticProbe
{
    /// <summary>The natural-language query fed to the model.</summary>
    public required string Query { get; init; }

    /// <summary>Whether this is a simple or complex request.</summary>
    public required SemanticProbeComplexity Complexity { get; init; }

    /// <summary>Expected resolved search mode, or null to not assert it (used only where the mode is
    /// unambiguous — pure extension queries → <see cref="SearchMode.FileNames"/>, bare content words →
    /// <see cref="SearchMode.Content"/>).</summary>
    public SearchMode? ExpectedSearchMode { get; init; }

    /// <summary>A glob that MUST appear (case-insensitively) among the resolved include globs, e.g.
    /// <c>*.png</c>. Null to not assert.</summary>
    public string? ExpectedIncludeGlob { get; init; }

    /// <summary>A substring the resolved content pattern MUST contain (case-insensitively), e.g.
    /// <c>error</c>. Null to not assert.</summary>
    public string? ExpectedPatternContains { get; init; }

    /// <summary>When true, the plan MUST set at least one created/modified after/before date filter.
    /// Null to not assert.</summary>
    public bool? ExpectedHasDateFilter { get; init; }

    /// <summary>Expected "Search hidden files" toggle on the resolved plan
    /// (<see cref="ResolvedSearchPlan.SearchHiddenFiles"/>): <c>true</c> to require it enabled
    /// ("hidden files", "show hidden"), <c>false</c> to require it disabled ("no hidden files").
    /// Null to not assert. Yagu resolves this deterministically from the query text, so it verifies
    /// the end-to-end hidden-files pipeline (the toggle, never an exclude glob) rather than model
    /// reasoning.</summary>
    public bool? ExpectedSearchHidden { get; init; }

    /// <summary>A substring that MUST appear (case-insensitively) within at least one resolved exclude
    /// glob, e.g. <c>node_modules</c>. A substring — not exact — match because models phrase folder
    /// excludes many ways (<c>node_modules</c>, <c>**/node_modules/**</c>, …). Null to not assert.</summary>
    public string? ExpectedExcludeGlobContains { get; init; }

    /// <summary>Expected "Search inside archives" toggle (<see cref="ResolvedSearchPlan.SearchInsideArchives"/>).
    /// Null to not assert. Yagu enables it deterministically when the plan targets archive extensions.</summary>
    public bool? ExpectedSearchInsideArchives { get; init; }

    /// <summary>Expected "Search image text (OCR)" toggle (<see cref="ResolvedSearchPlan.SearchImageText"/>).
    /// Null to not assert. Yagu enables it deterministically for a content term scoped to image types.</summary>
    public bool? ExpectedSearchImageText { get; init; }

    /// <summary>Expected regex-mode toggle (<see cref="ResolvedSearchPlan.UseRegex"/>). Null to not assert.
    /// Model-driven — a good model sets it when the query names a regex/regular expression.</summary>
    public bool? ExpectedUseRegex { get; init; }

    /// <summary>Expected exact-match (literal) toggle (<see cref="ResolvedSearchPlan.ExactMatch"/>). Null to
    /// not assert. Model-driven — a good model sets it for "the exact phrase …".</summary>
    public bool? ExpectedExactMatch { get; init; }

    /// <summary>Expected cross-line (multiline) toggle (<see cref="ResolvedSearchPlan.Multiline"/>). Null to
    /// not assert. Yagu resolves it deterministically from cross-line phrasing ("on the next line").</summary>
    public bool? ExpectedMultiline { get; init; }

    /// <summary>Expected "Obey .gitignore" toggle (<see cref="ResolvedSearchPlan.ObeyGitignore"/>). Null to
    /// not assert. Yagu resolves it deterministically from obey/ignore phrasing.</summary>
    public bool? ExpectedObeyGitignore { get; init; }

    /// <summary>When true, the plan MUST set a created-before date filter
    /// (<see cref="ResolvedSearchPlan.CreatedBeforeDate"/>). Null to not assert.</summary>
    public bool? ExpectedHasCreatedBefore { get; init; }
}

/// <summary>
/// The curated set of qualification probes: a small, fixed mix of simple and complex queries with
/// authored ground-truth expectations. Kept intentionally short (a handful) so a full qualification
/// sweep across the candidate ladder stays fast even on a slow CPU.
/// </summary>
public static class SemanticProbeSet
{
    /// <summary>The default probes used to qualify a model, ordered simplest-first so a hopeless model
    /// (invalid JSON / wrong types) fails cheaply on the very first probe.</summary>
    public static readonly IReadOnlyList<SemanticProbe> Default =
    [
        // ── Simple: single-constraint extension and content lookups ─────────────────────────────
        new SemanticProbe
        {
            Query = "all png files",
            Complexity = SemanticProbeComplexity.Simple,
            ExpectedSearchMode = SearchMode.FileNames,
            ExpectedIncludeGlob = "*.png",
        },
        new SemanticProbe
        {
            Query = "python files",
            Complexity = SemanticProbeComplexity.Simple,
            ExpectedSearchMode = SearchMode.FileNames,
            ExpectedIncludeGlob = "*.py",
        },
        new SemanticProbe
        {
            Query = "files containing the word error",
            Complexity = SemanticProbeComplexity.Simple,
            ExpectedSearchMode = SearchMode.Content,
            ExpectedPatternContains = "error",
        },

        // ── Complex: file type + content term, and date-scoped requests ─────────────────────────
        new SemanticProbe
        {
            Query = "c# files containing 'async'",
            Complexity = SemanticProbeComplexity.Complex,
            ExpectedIncludeGlob = "*.cs",
            ExpectedPatternContains = "async",
        },
        new SemanticProbe
        {
            Query = "log files changed today",
            Complexity = SemanticProbeComplexity.Complex,
            ExpectedIncludeGlob = "*.log",
            ExpectedHasDateFilter = true,
        },
        new SemanticProbe
        {
            Query = "files modified in the last 7 days",
            Complexity = SemanticProbeComplexity.Complex,
            ExpectedHasDateFilter = true,
        },

        // ── Complex: a settings toggle ("Search hidden files") combined with a content term ──────
        // Yagu controls hidden files via its toggle, never an exclude glob; this probe checks the
        // model still extracts the content term while the plan flips the hidden-files toggle on.
        new SemanticProbe
        {
            Query = "hidden files containing TODO",
            Complexity = SemanticProbeComplexity.Complex,
            ExpectedSearchMode = SearchMode.Content,
            ExpectedPatternContains = "TODO",
            ExpectedSearchHidden = true,
        },

        // ── Complex: broader settings-surface coverage — each probe exercises one search toggle that
        //    the model (or Yagu's deterministic salvage) must set on the resolved plan. Kept as a
        //    representative one-per-mutation set so the sweep still finishes in a bounded time. ──────

        // Exclude glob: include one file type while excluding a folder (model must emit an exclude token).
        new SemanticProbe
        {
            Query = "javascript files but not inside node_modules",
            Complexity = SemanticProbeComplexity.Complex,
            ExpectedIncludeGlob = "*.js",
            ExpectedExcludeGlobContains = "node_modules",
        },
        // Search inside archives: content that lives inside a zip-family container.
        new SemanticProbe
        {
            Query = "search inside zip files for the word password",
            Complexity = SemanticProbeComplexity.Complex,
            ExpectedSearchMode = SearchMode.Content,
            ExpectedPatternContains = "password",
            ExpectedSearchInsideArchives = true,
        },
        // Image text (OCR): a content term scoped to an image type must enable OCR.
        new SemanticProbe
        {
            Query = "find the word invoice inside png images",
            Complexity = SemanticProbeComplexity.Complex,
            ExpectedSearchMode = SearchMode.Content,
            ExpectedPatternContains = "invoice",
            ExpectedSearchImageText = true,
        },
        // Regex + cross-line (multiline) matching in one request.
        new SemanticProbe
        {
            Query = "regex matching START then END spanning multiple lines",
            Complexity = SemanticProbeComplexity.Complex,
            ExpectedUseRegex = true,
            ExpectedMultiline = true,
        },
        // Exact-match (literal) phrase.
        new SemanticProbe
        {
            Query = "the exact phrase \"public static void\"",
            Complexity = SemanticProbeComplexity.Complex,
            ExpectedPatternContains = "public static void",
            ExpectedExactMatch = true,
        },
        // .gitignore behavior toggle: including gitignored files means "do not obey .gitignore".
        new SemanticProbe
        {
            Query = "search every file including gitignored ones",
            Complexity = SemanticProbeComplexity.Complex,
            ExpectedObeyGitignore = false,
        },
        // Created-before date filter.
        new SemanticProbe
        {
            Query = "files created before 2020",
            Complexity = SemanticProbeComplexity.Complex,
            ExpectedHasCreatedBefore = true,
        },
    ];
}
