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

    /// <summary>Prose footnote rendered as a wrapped paragraph beneath the aligned breakdown table.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "x:Bind resolves overlay bindings against the ViewModel instance.")]
    public string SkipFootnoteText => SkipFootnote;

    /// <summary>One rendered row of the skipped-files breakdown.</summary>
    public readonly record struct SkipBreakdownEntry(string Glyph, string Label, int Count);

    /// <summary>Headline skipped total, shown as the breakdown's summary line.</summary>
    public int SkipTotalCount => FilesSkipped;

    /// <summary>
    /// The counted categories, omitting zero rows. Together with <c>Unclassified</c> these partition
    /// <see cref="SkipTotalCount"/> exactly, so a skip path no category claims stays visible.
    /// </summary>
    public IReadOnlyList<SkipBreakdownEntry> SkipBreakdownEntries
    {
        get
        {
            var b = _lastSkipBreakdown;
            int total = FilesSkipped;
            var rows = new List<SkipBreakdownEntry>();
            if (b is null || total == 0)
                return rows;

            void Add(string glyph, string label, int count)
            {
                if (count > 0)
                    rows.Add(new SkipBreakdownEntry(glyph, label, count));
            }

            Add("🚫", "Excluded by glob", b.GlobOnlyExcluded);
            Add("🗂️", "Yagu OCR cache", b.OcrCacheExcluded);
            Add("🔒", "Binary files", b.Binary);
            Add("📄", "Extension skips", b.ByExtension);
            Add("📏", "Too large", b.TooLarge);
            Add("📐", "Below minimum size", b.TooSmall);
            Add("📅", "Outside date range", b.DateFiltered);
            Add("🔐", "Access denied", b.AccessDenied);
            Add("📁", "Inaccessible folders", b.Directories);
            Add("⚠️", "I/O errors", b.IOError);
            Add("⏱️", "I/O timeouts", b.IoTimeout);
            Add("❓", "Not found", b.NotFound);
            Add("🔤", "Encoding errors", b.Encoding);
            Add("☁️", "Cloud-only placeholders", b.CloudOnlyDuringScan);
            Add("🧵", "Multiline size/timeout", b.MultilineSkipped);
            Add("❔", "Other", b.Other);
            Add("➕", "Unclassified", b.Unclassified(total));
            return rows;
        }
    }

    /// <summary>Discovery-time filters, reported separately because they never entered the scan set.</summary>
    public IReadOnlyList<SkipBreakdownEntry> SkipDiscoveryEntries
    {
        get
        {
            var b = _lastSkipBreakdown;
            var rows = new List<SkipBreakdownEntry>();
            if (b is null || b.DiscoveryFilteredTotal <= 0)
                return rows;

            void Add(string glyph, string label, int count)
            {
                if (count > 0)
                    rows.Add(new SkipBreakdownEntry(glyph, label, count));
            }

            Add("🙈", ".gitignore rules", b.GitignoreExcluded);
            Add("📄", "Excluded extensions", b.ExtensionExcludedAtDiscovery);
            Add("☁️", "Cloud-only placeholders", b.CloudOnlyAtDiscovery);
            return rows;
        }
    }

    /// <summary>
    /// Formatted per-category breakdown WITHOUT the trailing footnote. The overlay renders the visual
    /// table from <see cref="SkipBreakdownEntries"/> instead; this text backs the indicator's accessible
    /// description, where the padded columns are read as plain text.
    /// </summary>
    public string SkipBreakdownDetails
    {
        get
        {
            var b = _lastSkipBreakdown;
            int total = FilesSkipped;

            if (b is null)
                return "No files skipped";

            var lines = new StringBuilder();
            if (total == 0)
                lines.AppendLine("No files skipped");
            else
            {
                lines.AppendLine("Skipped files breakdown:");
                lines.AppendLine();
                foreach (var entry in SkipBreakdownEntries)
                    AppendSkipRow(lines, entry.Glyph, entry.Label, entry.Count);
                lines.AppendLine();
                AppendSkipRow(lines, "  ", "Total skipped", total, force: true);
            }

            if (b.DiscoveryFilteredTotal > 0)
            {
                lines.AppendLine();
                lines.AppendLine("Filtered during discovery (not counted above):");
                lines.AppendLine();
                foreach (var entry in SkipDiscoveryEntries)
                    AppendSkipRow(lines, entry.Glyph, entry.Label, entry.Count);
            }

            return lines.ToString().TrimEnd();
        }
    }

    /// <summary>Breakdown plus footnote as one string, used for the indicator's accessible description.</summary>
    public string SkipTooltip =>
        $"{SkipBreakdownDetails}{Environment.NewLine}{Environment.NewLine}{SkipFootnote}";

    private void UpdateSkipBreakdown(SkipBreakdown? breakdown)
    {
        _lastSkipBreakdown = breakdown;
        OnPropertyChanged(nameof(SkipBreakdownDetails));
        OnPropertyChanged(nameof(SkipTooltip));
    }
}
