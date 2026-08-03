namespace Yagu.Services.Index;

/// <summary>Structural (version/integrity) trust of a generation (plan §3.4).</summary>
public enum IndexStructuralVerdict
{
    Trusted,
    Missing,
    IncompatibleFormat,
    IncompatibleRepresentation,
    Corrupt,
}

/// <summary>Whether a root's change history is continuously proven via the USN journal (plan §3.5).</summary>
public enum RootFreshnessVerdict
{
    Continuous,
    JournalUnavailable,
    JournalDiscontinuity,
    UnsupportedFilesystem,
    AccessDenied,
    CheckpointInvalid,
}

/// <summary>The independent version dimensions of the persistent index format (plan §3.4). The scanner
/// ABI (<c>qg_abi_version</c>) and the planner-semantics version are intentionally <b>not</b> here:
/// the ABI only controls whether the worker may call the DLL, and the planner version is
/// query-independent — neither can invalidate an otherwise-compatible generation.</summary>
public readonly record struct IndexVersionSet(int FormatVersion, int RepresentationVersion);

/// <summary>The composed inputs to the per-generation trust decision (plan §3.4).</summary>
public readonly record struct IndexTrustInputs(
    IndexStructuralVerdict Structural,
    RootFreshnessVerdict RootFreshness,
    bool QueryEligible);

/// <summary>The decision for a whole generation/root (plan §3.4).</summary>
public abstract record GenerationDecision
{
    private GenerationDecision() { }

    /// <summary>The generation may be used to prune this search's candidates.</summary>
    public sealed record UseGeneration : GenerationDecision;

    /// <summary>Do not use the index for this root/search; fall back to a full live scan.</summary>
    public sealed record BypassRoot(string Reason) : GenerationDecision;
}

/// <summary>The per-path decision once a generation is usable (plan §3.4/§3.5).</summary>
public abstract record PathDecision
{
    private PathDecision() { }

    /// <summary>Scan the path live (member, dirty, unindexed, special-source, or untrusted).</summary>
    public sealed record LiveScanPath(string Reason) : PathDecision;

    /// <summary>Provisionally prune the path (a fresh nonmember) until the final USN reconciliation.</summary>
    public sealed record ProvisionalPrune(long AliasId) : PathDecision;
}

/// <summary>
/// The single trust-decision surface (plan §3.4). It composes the independent version/trust dimensions
/// into <see cref="GenerationDecision"/> and per-path <see cref="PathDecision"/> verdicts so the
/// builder, query worker, <c>--index-status</c>, and Settings never scatter these checks or invent a
/// different reason. Every decision is pure and fully branch-testable; when in doubt it falls back to
/// live scan (fail-safe).
/// </summary>
public static class IndexTrustDecision
{
    /// <summary>
    /// Compares a generation's persisted version set against the current build. Format and
    /// representation versions gate trust; the ABI and planner-semantics versions are excluded by
    /// design (plan §3.4). Only <see cref="IndexStructuralVerdict.Trusted"/> permits use.
    /// </summary>
    public static IndexStructuralVerdict EvaluateStructural(IndexVersionSet generation, IndexVersionSet current)
    {
        if (generation.FormatVersion != current.FormatVersion)
            return IndexStructuralVerdict.IncompatibleFormat;
        if (generation.RepresentationVersion != current.RepresentationVersion)
            return IndexStructuralVerdict.IncompatibleRepresentation;
        return IndexStructuralVerdict.Trusted;
    }

    /// <summary>Decides whether a generation may accelerate this search, or the root is bypassed.</summary>
    public static GenerationDecision DecideGeneration(IndexTrustInputs inputs)
    {
        if (inputs.Structural != IndexStructuralVerdict.Trusted)
            return new GenerationDecision.BypassRoot($"structural: {inputs.Structural}");
        if (inputs.RootFreshness != RootFreshnessVerdict.Continuous)
            return new GenerationDecision.BypassRoot($"freshness: {inputs.RootFreshness}");
        if (!inputs.QueryEligible)
            return new GenerationDecision.BypassRoot("ineligible query");
        return new GenerationDecision.UseGeneration();
    }

    /// <summary>
    /// Maps a per-path classification to its routing decision (plan §3.5). Only a fresh posting
    /// <b>nonmember</b> is provisionally pruned; every other class — members (verified live), dirty,
    /// unindexed, special-source, and untrusted-root — is live-scanned.
    /// </summary>
    public static PathDecision DecidePath(IndexPathClassification classification)
    {
        ArgumentNullException.ThrowIfNull(classification);
        return classification switch
        {
            IndexPathClassification.FreshIndexedNonmember nonmember
                => new PathDecision.ProvisionalPrune(nonmember.AliasId),
            IndexPathClassification.DirtyByUsn dirty
                => new PathDecision.LiveScanPath($"dirty: {dirty.Reason}"),
            IndexPathClassification.Unindexed unindexed
                => new PathDecision.LiveScanPath($"unindexed: {unindexed.Reason}"),
            IndexPathClassification.SpecialSource special
                => new PathDecision.LiveScanPath($"special source: {special.Kind}"),
            IndexPathClassification.UntrustedRoot untrusted
                => new PathDecision.LiveScanPath($"untrusted root: {untrusted.Reason}"),
            _ => new PathDecision.LiveScanPath("fresh posting member"),
        };
    }
}
