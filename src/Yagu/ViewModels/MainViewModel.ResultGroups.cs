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

    private const string SkipFootnote =
        "Some files are removed before Yagu can count them and appear in neither list: include-extension filters, search depth, the walker's hidden/system-file rules, and — when the Everything backend serves discovery — exclude patterns and size/date filters that are pushed into the Everything query itself.";

    /// <summary>Width the reason labels are padded to so the counts line up in the monospaced overlay.</summary>
    private const int SkipLabelWidth = 24;

    private static void AppendSkipRow(StringBuilder sb, string glyph, string label, int count, bool force = false)
    {
        if (count <= 0 && !force)
            return;
        sb.Append("  ").Append(glyph).Append("  ").Append(label.PadRight(SkipLabelWidth))
          .AppendLine(count.ToString("N0", CultureInfo.InvariantCulture).PadLeft(9));
    }

    /// <summary>
    /// Formatted per-category breakdown of skipped files, shown by the status-bar Skipped overlay.
    /// <para>
    /// The first block partitions the headline <see cref="FilesSkipped"/> exactly: every counted reason
    /// plus the <c>Unclassified</c> remainder sums to the total, so a skip path that this breakdown does
    /// not yet name is still visible instead of quietly disappearing. The second block lists discovery
    /// filters, which removed paths before the scan set existed and are therefore not part of the total.
    /// </para>
    /// </summary>
    public string SkipTooltip
    {
        get
        {
            var b = _lastSkipBreakdown;
            int total = FilesSkipped;

            if (b is null)
                return $"No files skipped{Environment.NewLine}{Environment.NewLine}{SkipFootnote}";

            var lines = new StringBuilder();
            if (total == 0)
                lines.AppendLine("No files skipped");
            else
            {
                lines.AppendLine("Skipped files breakdown:");
                lines.AppendLine();
                AppendSkipRow(lines, "🚫", "Excluded by glob", b.GlobOnlyExcluded);
                AppendSkipRow(lines, "🗂️", "Yagu OCR cache", b.OcrCacheExcluded);
                AppendSkipRow(lines, "🔒", "Binary files", b.Binary);
                AppendSkipRow(lines, "📄", "Extension skips", b.ByExtension);
                AppendSkipRow(lines, "📏", "Too large", b.TooLarge);
                AppendSkipRow(lines, "📐", "Below minimum size", b.TooSmall);
                AppendSkipRow(lines, "📅", "Outside date range", b.DateFiltered);
                AppendSkipRow(lines, "🔐", "Access denied", b.AccessDenied);
                AppendSkipRow(lines, "📁", "Inaccessible folders", b.Directories);
                AppendSkipRow(lines, "⚠️", "I/O errors", b.IOError);
                AppendSkipRow(lines, "⏱️", "I/O timeouts", b.IoTimeout);
                AppendSkipRow(lines, "❓", "Not found", b.NotFound);
                AppendSkipRow(lines, "🔤", "Encoding errors", b.Encoding);
                AppendSkipRow(lines, "☁️", "Cloud-only placeholders", b.CloudOnlyDuringScan);
                AppendSkipRow(lines, "🧵", "Multiline size/timeout", b.MultilineSkipped);
                AppendSkipRow(lines, "❔", "Other", b.Other);
                AppendSkipRow(lines, "➕", "Unclassified", b.Unclassified(total));
                AppendSkipRow(lines, "  ", "Total skipped", total, force: true);
            }

            if (b.DiscoveryFilteredTotal > 0)
            {
                lines.AppendLine();
                lines.AppendLine("Filtered during discovery (not counted above):");
                lines.AppendLine();
                AppendSkipRow(lines, "🙈", ".gitignore rules", b.GitignoreExcluded);
                AppendSkipRow(lines, "📄", "Excluded extensions", b.ExtensionExcludedAtDiscovery);
                AppendSkipRow(lines, "☁️", "Cloud-only placeholders", b.CloudOnlyAtDiscovery);
            }

            lines.AppendLine();
            lines.Append(SkipFootnote);
            return lines.ToString().TrimEnd();
        }
    }

    private void UpdateSkipBreakdown(SkipBreakdown? breakdown)
    {
        _lastSkipBreakdown = breakdown;
        OnPropertyChanged(nameof(SkipTooltip));
    }
}
