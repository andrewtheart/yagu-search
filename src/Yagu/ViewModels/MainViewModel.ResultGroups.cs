using System.Collections.Specialized;
using Yagu.Models;
using System.Globalization;
using System.Text;

namespace Yagu.ViewModels;

/// <summary>
/// Result grouping rows: expand/collapse state, rebuilding the flattened row projection when the
/// visible groups change, and the skipped-files breakdown tooltip.
/// </summary>
public sealed partial class MainViewModel
{
    private void OnVisibleResultGroupsChanging(object? sender, EventArgs e)
        => ResultGroupsChanging?.Invoke(this, EventArgs.Empty);

    private void OnVisibleResultGroupsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems is not null && (GroupMode == GroupMode.None || IsSearching))
        {
            ResultRows.AppendRange(e.NewItems.Cast<object>().ToList());
            return;
        }

        if (GroupMode != GroupMode.None)
        {
            RebuildResultRows();
            return;
        }

        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewItems is not null:
                ResultRows.AppendRange(e.NewItems.Cast<object>().ToList());
                break;
            case NotifyCollectionChangedAction.Remove when e.OldItems is not null:
                foreach (var item in e.OldItems)
                    ResultRows.Remove(item);
                break;
            default:
                RebuildResultRows();
                break;
        }
    }

    public void ToggleResultGroupExpansion(ResultGroupHeaderRow header)
    {
        _expandedResultGroupKeys[header.Key] = !header.IsExpanded;
        RebuildResultRows();
    }

    private readonly Dictionary<string, bool> _expandedResultGroupKeys = new(StringComparer.Ordinal);

    private void RebuildResultRows()
    {
        var rows = ResultRowProjection.BuildRows(ResultGroups, GroupMode, _expandedResultGroupKeys);
        ResultRows.ReplaceAll(rows);
    }

    private SkipBreakdown? _lastSkipBreakdown;
    private const string ExtensionExclusionSkipNote = "Files excluded by extension during discovery are filtered before counting and are not included in skipped counts.";

    /// <summary>Formatted tooltip showing a per-category breakdown of skipped files.</summary>
    public string SkipTooltip
    {
        get
        {
            var b = _lastSkipBreakdown;
            if (b is null || FilesSkipped == 0)
                return $"No files skipped{Environment.NewLine}{Environment.NewLine}{ExtensionExclusionSkipNote}";

            var lines = new StringBuilder();
            lines.AppendLine("Skipped files breakdown:");
            lines.AppendLine(ExtensionExclusionSkipNote);
            lines.AppendLine();
            if (b.GlobExcluded > 0)   lines.AppendLine(CultureInfo.InvariantCulture, $"  🚫  Glob exclusions       {b.GlobExcluded,8:N0}");
            if (b.GitignoreExcluded > 0) lines.AppendLine(CultureInfo.InvariantCulture, $"  🙈  .gitignore excluded   {b.GitignoreExcluded,8:N0}");
            if (b.CloudOnly > 0)      lines.AppendLine(CultureInfo.InvariantCulture, $"  ☁️  Cloud-only skipped    {b.CloudOnly,8:N0}");
            if (b.Binary > 0)         lines.AppendLine(CultureInfo.InvariantCulture, $"  🔒  Binary files          {b.Binary,8:N0}");
            if (b.ByExtension > 0)    lines.AppendLine(CultureInfo.InvariantCulture, $"  📄  Scanner extension skips {b.ByExtension,8:N0}");
            if (b.TooLarge > 0)       lines.AppendLine(CultureInfo.InvariantCulture, $"  📏  Too large             {b.TooLarge,8:N0}");
            if (b.AccessDenied > 0)   lines.AppendLine(CultureInfo.InvariantCulture, $"  🔐  Access denied         {b.AccessDenied,8:N0}");
            if (b.Directories > 0)    lines.AppendLine(CultureInfo.InvariantCulture, $"  📁  Inaccessible dirs     {b.Directories,8:N0}");
            if (b.IOError > 0)        lines.AppendLine(CultureInfo.InvariantCulture, $"  ⚠️  I/O errors            {b.IOError,8:N0}");
            if (b.IoTimeout > 0)      lines.AppendLine(CultureInfo.InvariantCulture, $"  ⏱️  I/O timeouts          {b.IoTimeout,8:N0}");
            if (b.NotFound > 0)       lines.AppendLine(CultureInfo.InvariantCulture, $"  ❓  Not found             {b.NotFound,8:N0}");
            if (b.Encoding > 0)       lines.AppendLine(CultureInfo.InvariantCulture, $"  🔤  Encoding errors       {b.Encoding,8:N0}");
            if (b.Other > 0)          lines.AppendLine(CultureInfo.InvariantCulture, $"  ❔  Other                 {b.Other,8:N0}");

            return lines.ToString().TrimEnd();
        }
    }

    private void UpdateSkipBreakdown(SkipBreakdown? breakdown)
    {
        _lastSkipBreakdown = breakdown;
        OnPropertyChanged(nameof(SkipTooltip));
    }
}
