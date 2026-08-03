namespace Yagu.Services.Index;

/// <summary>
/// Supplies the candidate content-id set for an accelerated query from an alternative backend (plan §3.3).
/// The default in-process path evaluates <see cref="TrigramPostingIndex.EvaluateSet"/> directly; a
/// <see cref="IIndexCandidateSource"/> lets the isolated <c>Yagu.IndexWorker</c> produce the same set from
/// the generation's serialized <c>content.bin</c> instead, so a read-time / native fault is contained in the
/// worker. Implementations MUST be conservative: any failure returns <c>false</c> so the caller falls back to
/// the in-process evaluation (which never changes results).
/// </summary>
public interface IIndexCandidateSource
{
    /// <summary>
    /// Tries to evaluate <paramref name="query"/> against the generation stored in
    /// <paramref name="generationDir"/> and produce its candidate content-id set. Returns <c>false</c> (and
    /// an empty set) on any error/unavailability, signalling the caller to use the in-process fallback.
    /// Must not throw.
    /// </summary>
    bool TryEvaluate(string generationDir, TrigramExpression query, out IReadOnlySet<int> candidateContentIds);
}
