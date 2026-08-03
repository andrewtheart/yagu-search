using Microsoft.Extensions.Logging;
using Yagu.Models;
using Yagu.Services.Logging;

namespace Yagu.Services.Index;

/// <summary>The actionable content-index problem found before a search starts.</summary>
public enum ContentIndexReadinessIssueKind
{
    Missing,
    RefreshRequired,
}

/// <summary>
/// Describes one query-relevant index problem. <paramref name="SearchRoot"/> is the root the search will
/// enumerate; <paramref name="IndexRoot"/> is the physical maintained/leftover index root selected for it.
/// </summary>
public sealed record ContentIndexReadinessIssue(
    string SearchRoot,
    string IndexRoot,
    ContentIndexReadinessIssueKind Kind,
    string Reason,
    bool Registered,
    bool Repairable)
{
    /// <summary>A registered missing/stale root can be built/rebuilt directly.</summary>
    public bool CanRebuild => Registered && Repairable;

    /// <summary>An unregistered missing/stale root can be enrolled and built.</summary>
    public bool CanAdd => !Registered && Repairable;

    /// <summary>Stable session key used to avoid repeating an acknowledged warning every search.</summary>
    public string WarningKey => $"{Kind}|{IndexRoot}";
}

/// <summary>
/// Performs the same cheap, fail-closed query-planning and per-layer freshness preflight as the mapped
/// worker path, but does not open/map the worker session and never mutates an index. It is used before a
/// GUI search so a missing or freshness-bypassed index is explained before Yagu commits to a full live scan.
/// Query-shape bypasses are intentionally ignored because rebuilding cannot make those queries eligible.
/// </summary>
public static class ContentIndexReadinessChecker
{
    public static ContentIndexReadinessIssue? CheckRoot(
        IContentIndexPathProvider paths,
        string searchRoot,
        IReadOnlyList<string> registeredRoots,
        SearchOptions options,
        int retainedGenerations,
        ContentIndexFreshnessEvaluator.JournalReader journalReader)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrEmpty(searchRoot);
        ArgumentNullException.ThrowIfNull(registeredRoots);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(journalReader);

        try
        {
            var manager = new ContentIndexManager(paths, retainedGenerations);
            string indexRoot = manager.ResolveBestAvailableIndexRoot(searchRoot, registeredRoots);
            string scopeId = ContentIndexManager.ScopeIdForRoot(indexRoot);
            var store = new ContentIndexStore(paths, scopeId, retainedGenerations);

            IndexQueryOpenRequest? request = ContentIndexShadowScopeBuilder.TryBuild(
                store, options, sessionId: 0, journalReader, out string reason);
            if (request is not null)
                return null;

            ContentIndexReadinessIssueKind? kind = ClassifyReason(reason);
            if (kind is null)
                return null;

            (reason, bool repairable) = ResolveRepairability(
                kind.Value,
                reason,
                () => ContentIndexManager.VolumeSupportsChangeJournal(indexRoot));

            bool registered = IndexedRootsPolicy.FindBestCoveringRoot(registeredRoots, searchRoot) is not null;
            return new ContentIndexReadinessIssue(
                IndexScopeIdentity.NormalizePath(searchRoot),
                IndexScopeIdentity.NormalizePath(indexRoot),
                kind.Value,
                reason,
                registered,
                repairable);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // This is a user-notification preflight only. Any diagnostic uncertainty must fail open to the
            // real search path, whose own gate remains authoritative and fail-safe.
            YaguLog.For("ContentIndex").LogDebug(ex,
                "Content-index readiness preflight failed for {Root}; continuing to the authoritative search gate.",
                searchRoot);
            return null;
        }
    }

    internal static ContentIndexReadinessIssueKind? ClassifyReason(string? reason)
    {
        if (reason?.Contains("no trusted index", StringComparison.OrdinalIgnoreCase) == true)
            return ContentIndexReadinessIssueKind.Missing;

        if (reason?.Contains("layer not fresh", StringComparison.OrdinalIgnoreCase) == true
            || reason?.Contains("layer freshness inputs unreadable", StringComparison.OrdinalIgnoreCase) == true
            || reason?.Contains("JournalDiscontinuity", StringComparison.OrdinalIgnoreCase) == true
            || reason?.Contains("CheckpointInvalid", StringComparison.OrdinalIgnoreCase) == true
            || reason?.Contains("JournalUnavailable", StringComparison.OrdinalIgnoreCase) == true)
            return ContentIndexReadinessIssueKind.RefreshRequired;

        return null;
    }

    internal static (string Reason, bool Repairable) ResolveRepairability(
        ContentIndexReadinessIssueKind kind,
        string reason,
        Func<bool> volumeSupportsChangeJournal)
    {
        if (kind == ContentIndexReadinessIssueKind.RefreshRequired
            && reason.Contains("CheckpointInvalid", StringComparison.OrdinalIgnoreCase)
            && !volumeSupportsChangeJournal())
        {
            return (
                "layer not fresh: UnsupportedChangeJournal - this volume does not provide a supported change journal",
                false);
        }

        return (reason, true);
    }
}
