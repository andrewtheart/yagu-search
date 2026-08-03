namespace Yagu.Services.Index;

/// <summary>
/// Per-folder build-time glob overrides for one registered indexed root (resolved by
/// <see cref="IndexedRootFilterPolicy"/>). These layer on top of the <b>global</b>
/// <c>AppSettings.IndexExcludedGlobs</c>: the global excludes are the baseline for every root, a root's
/// <see cref="ExcludeGlobs"/> add more excludes for that root only, and a root's <see cref="IncludeGlobs"/>
/// <b>re-admit</b> (gitignore-style "<c>!</c>" negation) paths a broader exclude would otherwise drop — so
/// you can, for example, exclude <c>node_modules</c> everywhere globally but still index it under one
/// specific folder. Build-time only: a path left out of the index is simply live-scanned at query time, so
/// a mis-configured override can never hide a search match. Glob strings use the same comma/semicolon
/// separated format as the global index globs.
/// </summary>
public sealed class IndexedRootFilter
{
    /// <summary>The registered root path these overrides apply to (canonicalized via <see cref="IndexScopeIdentity.NormalizePath"/>).</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Comma/semicolon-separated globs that re-admit paths a broader exclude would drop (gitignore-style negation). Empty = none.</summary>
    public string IncludeGlobs { get; set; } = string.Empty;

    /// <summary>Comma/semicolon-separated build-time exclude globs added on top of the global excludes, for this root only. Empty = none.</summary>
    public string ExcludeGlobs { get; set; } = string.Empty;
}
