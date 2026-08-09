using Yagu.Models;

namespace Yagu.Helpers;

/// <summary>
/// Accessible names for the virtualized results list. Without these a screen reader announces each row
/// as the item's type name ("Yagu.Models.FileGroup"), because a ListView falls back to
/// <see cref="object.ToString"/> when no automation name is set.
/// </summary>
internal static class ResultRowAccessibleName
{
    /// <summary>The announced name for <paramref name="row"/>, or null when the row type is unknown
    /// (the caller then leaves the container's existing name alone).</summary>
    internal static string? For(object? row) => row switch
    {
        FileGroup group => ForFileGroup(group.FileName, group.DirectoryName, group.MatchCount, group.IsExpanded),
        ResultGroupHeaderRow header => ForGroupHeader(header.Title, header.SummaryText, header.IsExpanded),
        _ => null,
    };

    internal static string ForFileGroup(string fileName, string directoryName, int matchCount, bool isExpanded)
    {
        string name = string.IsNullOrWhiteSpace(fileName) ? "(unnamed file)" : fileName;
        string matches = $"{matchCount:N0} {(matchCount == 1 ? "match" : "matches")}";
        string state = isExpanded ? "expanded" : "collapsed";
        return string.IsNullOrWhiteSpace(directoryName)
            ? $"{name}, {matches}, {state}"
            : $"{name}, {matches}, in {directoryName}, {state}";
    }

    internal static string ForGroupHeader(string title, string summary, bool isExpanded)
    {
        string name = string.IsNullOrWhiteSpace(title) ? "(unnamed group)" : title;
        string state = isExpanded ? "expanded" : "collapsed";
        return string.IsNullOrWhiteSpace(summary) ? $"{name}, {state}" : $"{name}, {summary}, {state}";
    }
}
