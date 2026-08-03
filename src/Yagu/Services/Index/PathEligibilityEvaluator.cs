using Yagu.Helpers;
using Yagu.Models;

namespace Yagu.Services.Index;

/// <summary>Why a candidate path is (in)eligible for the optional early-verification lane (plan §5).</summary>
public enum PathEligibilityResult
{
    Eligible,
    ExcludedByRoot,
    ExcludedByHidden,
    ExcludedBySize,
    ExcludedByDate,
    ExcludedByExtension,
    ExcludedByGlob,
}

/// <summary>Metadata about a candidate path, independent of any live filesystem access.</summary>
public readonly record struct PathEligibilityCandidate(
    string FullPath,
    long SizeBytes,
    DateTimeOffset? Created,
    DateTimeOffset? Modified,
    bool IsHidden);

/// <summary>
/// The shared, pure path-eligibility evaluator (plan §5). It reproduces the <em>stateless</em>
/// discovery filters — root containment, include/exclude globs, skip extensions, hidden, size, and
/// date — so a posting-selected candidate can be verified <em>early</em> (before authoritative
/// <see cref="FileLister"/> discovery reaches it) without changing results. When a traversal-dependent
/// filter (e.g. gitignore) is active, <see cref="CanEvaluate"/> is <c>false</c> and the early lane is
/// disabled; the candidate simply waits for authoritative discovery. Because the evaluator only
/// <em>enables</em> an optimization and never broadens scope, any uncertainty conservatively excludes
/// the path from the early lane — correctness is always preserved by <see cref="FileLister"/>.
/// </summary>
public sealed class PathEligibilityEvaluator
{
    private readonly string _normalizedRoot;
    private readonly GlobMatcher _globMatcher;
    private readonly bool _hasGlobs;
    private readonly IReadOnlySet<string> _skipExtensions;
    private readonly bool _searchHidden;
    private readonly long _minSize;
    private readonly long _maxSize;
    private readonly DateTimeOffset? _createdAfter;
    private readonly DateTimeOffset? _createdBefore;
    private readonly DateTimeOffset? _modifiedAfter;
    private readonly DateTimeOffset? _modifiedBefore;

    /// <summary>
    /// False when a traversal-dependent filter (currently gitignore) is active, so the early lane is
    /// disabled and posting candidates are queued for authoritative discovery instead.
    /// </summary>
    public bool CanEvaluate { get; }

    public PathEligibilityEvaluator(SearchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _normalizedRoot = string.IsNullOrWhiteSpace(options.Directory)
            ? string.Empty
            : IndexScopeIdentity.NormalizePath(options.Directory);

        _globMatcher = new GlobMatcher(
            options.IncludeGlobs ?? Array.Empty<string>(),
            options.ExcludeGlobs ?? Array.Empty<string>(),
            options.IncludeFilterMode,
            options.ExcludeFilterMode);
        _hasGlobs = options.IncludeGlobs is { Count: > 0 } || options.ExcludeGlobs is { Count: > 0 };

        _skipExtensions = options.SkipExtensions ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _searchHidden = options.SearchHiddenFiles;
        _minSize = Math.Max(0, options.MinFileSizeBytes);
        _maxSize = Math.Max(0, options.MaxFileSizeBytes);
        _createdAfter = options.CreatedAfterDate;
        _createdBefore = options.CreatedBeforeDate;
        _modifiedAfter = options.ModifiedAfterDate;
        _modifiedBefore = options.ModifiedBeforeDate;

        // Gitignore is traversal-dependent — it needs the directory tree state, not just per-path
        // metadata — so the early lane cannot reproduce it and is disabled.
        CanEvaluate = !options.ObeyGitignore;
    }

    /// <summary>
    /// Evaluates whether the candidate would pass the current search's stateless filters. Only
    /// meaningful when <see cref="CanEvaluate"/> is true; callers must gate on it first.
    /// </summary>
    public PathEligibilityResult Evaluate(PathEligibilityCandidate candidate)
    {
        if (_normalizedRoot.Length > 0 && !IsUnderRoot(candidate.FullPath))
            return PathEligibilityResult.ExcludedByRoot;

        if (!_searchHidden && candidate.IsHidden)
            return PathEligibilityResult.ExcludedByHidden;

        if (!SizeInRange(candidate.SizeBytes))
            return PathEligibilityResult.ExcludedBySize;

        if (!DateInRange(candidate.Created, _createdAfter, _createdBefore)
            || !DateInRange(candidate.Modified, _modifiedAfter, _modifiedBefore))
            return PathEligibilityResult.ExcludedByDate;

        if (ExtensionSkipped(candidate.FullPath))
            return PathEligibilityResult.ExcludedByExtension;

        if (_hasGlobs && !_globMatcher.Matches(candidate.FullPath))
            return PathEligibilityResult.ExcludedByGlob;

        return PathEligibilityResult.Eligible;
    }

    private bool IsUnderRoot(string fullPath)
    {
        string normalized = IndexScopeIdentity.NormalizePath(fullPath);
        if (normalized.Equals(_normalizedRoot, StringComparison.OrdinalIgnoreCase))
            return true;
        string prefix = _normalizedRoot.EndsWith('\\') ? _normalizedRoot : _normalizedRoot + "\\";
        return normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private bool SizeInRange(long size)
    {
        if (_minSize > 0 && size < _minSize) return false;
        if (_maxSize > 0 && size > _maxSize) return false;
        return true;
    }

    private static bool DateInRange(DateTimeOffset? value, DateTimeOffset? after, DateTimeOffset? before)
    {
        if (after is null && before is null)
            return true;
        // A filter is active but the value is unknown → cannot prove eligibility → exclude from the
        // early lane (conservative; authoritative discovery still handles it).
        if (value is not { } v)
            return false;
        if (after is { } a && v < a) return false;   // created/modified before the bound → skipped
        if (before is { } b && v > b) return false;  // created/modified after the bound → skipped
        return true;
    }

    private bool ExtensionSkipped(string fullPath)
    {
        if (_skipExtensions.Count == 0)
            return false;
        string ext = System.IO.Path.GetExtension(fullPath);
        if (ext.Length == 0)
            return false;
        return _skipExtensions.Contains(ext.TrimStart('.'));
    }
}
