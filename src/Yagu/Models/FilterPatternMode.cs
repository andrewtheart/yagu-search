namespace Yagu.Models;

/// <summary>How include/exclude file filters are interpreted.</summary>
public enum FilterPatternMode
{
    /// <summary>Extensions, path segments, and glob wildcards.</summary>
    GlobPath = 0,
    /// <summary>A regular expression matched against the normalized full path.</summary>
    Regex = 1,
}