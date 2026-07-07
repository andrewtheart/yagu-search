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
    ];
}
