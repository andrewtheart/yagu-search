namespace Yagu.Models;

public static class ResultRowProjection
{
    public static List<object> BuildRows(
        IEnumerable<FileGroup> visibleGroups,
        GroupMode groupMode,
        IReadOnlyDictionary<string, bool> expandedGroupKeys)
    {
        var rows = new List<object>();
        if (groupMode == GroupMode.None)
        {
            rows.AddRange(visibleGroups);
            return rows;
        }

        string? headerText = null;
        string? headerKey = null;
        var headerGroups = new List<FileGroup>();

        void FlushHeaderGroup()
        {
            if (headerText is null || headerKey is null)
                return;

            bool isExpanded = !expandedGroupKeys.TryGetValue(headerKey, out bool expanded) || expanded;
            var header = new ResultGroupHeaderRow(
                headerKey,
                headerText,
                headerGroups.Count,
                headerGroups.Sum(group => group.MatchCount),
                isExpanded);
            rows.Add(header);
            if (isExpanded)
                rows.AddRange(headerGroups);

            headerGroups.Clear();
        }

        foreach (var group in visibleGroups)
        {
            if (group.HasGroupHeader)
            {
                FlushHeaderGroup();
                headerText = group.GroupHeaderText;
                headerKey = BuildHeaderKey(groupMode, headerText);
            }

            if (headerText is null)
                rows.Add(group);
            else
                headerGroups.Add(group);
        }

        FlushHeaderGroup();
        return rows;
    }

    public static string BuildHeaderKey(GroupMode groupMode, string? headerText) =>
        $"{(int)groupMode}|{headerText ?? string.Empty}";
}