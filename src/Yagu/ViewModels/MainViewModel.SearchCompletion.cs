using Yagu.Models;
using Yagu.Services;
using System.Security;
using System.Text.RegularExpressions;

namespace Yagu.ViewModels;

/// <summary>
/// Finishing a search: revalidating results whose files changed, the completion status line,
/// throughput/elapsed formatting, the final sort/filter pass, recent-directory sync, and the
/// search-complete toast.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>
    /// Re-scan a file's content against the current query and update the result list.
    /// Removes matches that no longer exist and updates surviving match text/positions.
    /// </summary>
    /// <param name="filePath">The saved file path.</param>
    /// <param name="savedText">The text that was written to disk.</param>
    /// <returns>True if the file group still has matches; false if it was removed entirely.</returns>
    public bool RevalidateFileResults(string filePath, string savedText)
    {
        var group = _resultCollection.FindGroup(filePath);
        if (group is null) return false;

        // Build the same matcher the search engine uses.
        var query = Query;
        if (string.IsNullOrEmpty(query)) return group.Count > 0;

        Regex? regex = null;
        string? literal = null;
        StringComparison literalComparison = StringComparison.OrdinalIgnoreCase;

        if (UseRegex)
        {
            var regexOptions = RegexOptions.Multiline;
            if (!CaseSensitive) regexOptions |= RegexOptions.IgnoreCase;
            try { regex = new Regex(query, regexOptions, TimeSpan.FromSeconds(5)); }
            catch { return group.Count > 0; } // invalid regex — don't remove anything
        }
        else
        {
            literal = query;
            literalComparison = CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        }

        // Split saved text into lines.
        var lines = savedText.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].EndsWith('\r'))
                lines[i] = lines[i][..^1];
        }

        int contextLineCount = ContextLines;

        // Build new results from the saved content.
        var newResults = new List<SearchResult>();
        for (int i = 0; i < lines.Length; i++)
        {
            var matches = ContentSearcher.FindMatches(lines[i], regex, literal, literalComparison);
            if (matches.Count == 0) continue;

            // Build context before/after.
            var before = new List<string>(contextLineCount);
            for (int b = Math.Max(0, i - contextLineCount); b < i; b++)
                before.Add(Helpers.LineTruncator.Truncate(lines[b]));
            var after = new List<string>(contextLineCount);
            for (int a = i + 1; a <= Math.Min(lines.Length - 1, i + contextLineCount); a++)
                after.Add(Helpers.LineTruncator.Truncate(lines[a]));

            foreach (var (start, length) in matches)
            {
                var displayLine = Helpers.LineTruncator.TruncateAroundMatch(lines[i], start, length);
                newResults.Add(new SearchResult(
                    FilePath: filePath,
                    LineNumber: i + 1,
                    MatchLine: displayLine.Text,
                    MatchStartColumn: displayLine.MatchStart,
                    MatchLength: length,
                    ContextBefore: before,
                    ContextAfter: after)
                { SourceMatchStartColumn = start });
            }
        }

        // Replace the group contents.
        int removedCount = group.Count;
        group.Clear();
        if (newResults.Count > 0)
        {
            foreach (var r in newResults)
                group.Add(r);
        }
        else
        {
            _resultCollection.RemoveGroup(group);
        }

        // Adjust MatchesFound to reflect the delta.
        int delta = newResults.Count - removedCount;
        MatchesFound = Math.Max(0, MatchesFound + delta);

        NotifyResultAvailabilityChanged();
        return newResults.Count > 0;
    }

    private static string BuildCompletionStatus(SearchSummary s, TimeSpan elapsed)
    {
        var time = FormatElapsed(elapsed);
        var rate = FormatThroughput(s.FilesScanned, s.BytesScanned, elapsed);
        if (s.Cancelled)
            return $"Cancelled — {s.TotalMatches:N0} matches in {s.FilesWithMatches:N0} files ({time}, {rate})";
        if (s.Truncated)
            return $"Truncated at {s.TotalMatches:N0} matches ({time}, {rate})";
        if (s.Degraded)
            return $"{s.TotalMatches:N0} matches in {s.FilesWithMatches:N0} files ({time}, {rate})";
        return $"{s.TotalMatches:N0} matches in {s.FilesWithMatches:N0} files ({time}, {rate})";
    }

    private static string FormatElapsed(TimeSpan elapsed) =>
        $"{(int)elapsed.TotalHours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2} elapsed";

    private static string FormatThroughput(int filesProcessed, long bytesScanned, TimeSpan elapsed)
    {
        double seconds = Math.Max(elapsed.TotalSeconds, 0.001);
        return $"{filesProcessed / seconds:N1} files/sec";
    }

    private static int ClampMatchCount(long matchCount) =>
        matchCount >= int.MaxValue ? int.MaxValue : (int)Math.Max(0, matchCount);

    private double _instantFilesPerSec;
    private double _instantMbPerSec;
    private double _prevDisplayTime;
    private int _prevDisplayFiles;
    private long _prevDisplayBytes;

    private void UpdateFilesPerSecond()
    {
        if (_searchTimer is null)
        {
            return;
        }
        double seconds = Math.Max(_searchTimer.Elapsed.TotalSeconds, 0.001);
        int filesWithMatches = _resultCollection.AllGroups.Count;

        // Update instantaneous rate display (~2s window, like Task Manager)
        double displayDt = seconds - _prevDisplayTime;
        if (displayDt >= 2.0 && FilesScanned > 0)
        {
            int deltaFiles = FilesScanned - _prevDisplayFiles;
            long deltaBytes = _bytesScanned - _prevDisplayBytes;
            _instantFilesPerSec = deltaFiles / displayDt;
            _instantMbPerSec = deltaBytes / (1024.0 * 1024.0) / displayDt;
            _prevDisplayFiles = FilesScanned;
            _prevDisplayBytes = _bytesScanned;
            _prevDisplayTime = seconds;
        }

        string? sourcePhase = _sourceBackedSearchProgress?.BuildPhaseLabel(FilesScanned, TotalFiles);
        string phaseSuffix = sourcePhase is null ? string.Empty : $" — {sourcePhase}";
        StatusText = $"{MatchesFound:N0} matches in {filesWithMatches:N0} files ({FormatElapsed(_searchTimer.Elapsed)}, {_instantFilesPerSec:N1} files/sec){phaseSuffix}";

        // Collect incremental sample for sparkline (~0.15s window, rolling 30s)
        double dt = seconds - _prevSampleTime;
        if (dt >= 0.15 && FilesScanned > 0) // sample ~6-7x per second
        {
            int deltaFiles = FilesScanned - _prevFilesScanned;
            long deltaBytes = _bytesScanned - _prevBytesScanned;
            double sampleFps = deltaFiles / dt;
            double sampleMbps = deltaBytes / (1024.0 * 1024.0) / dt;
            ThroughputSamples.Add((sampleFps, sampleMbps));
            // Keep only last 30 seconds of samples (30s / 0.15s = 200)
            const int maxSamples = 200;
            if (ThroughputSamples.Count > maxSamples)
                ThroughputSamples.RemoveRange(0, ThroughputSamples.Count - maxSamples);
            _prevFilesScanned = FilesScanned;
            _prevBytesScanned = _bytesScanned;
            _prevSampleTime = seconds;
        }
    }

    partial void OnFileNameFilterChanged(string value) => ApplySortAndFilter();

    private void ApplySortAndFilter()
    {
        _resultCollection.FileNameFilter = FileNameFilter;
        _resultCollection.IncludeGlobs = IncludeGlobs;
        _resultCollection.ExcludeGlobs = EffectiveExcludeGlobsText;
        _resultCollection.IncludeFilterMode = IncludeFilterMode;
        _resultCollection.ExcludeFilterMode = ExcludeFilterMode;
        _resultCollection.SortModeIndex = SortModeIndex;
        _resultCollection.SortDirectionIndex = SortDirectionIndex;
        _resultCollection.SetSortCriteria(_sortCriteria);
        _resultCollection.GroupMode = GroupMode;
        _resultCollection.GroupSortDirectionIndex = GroupSortDirectionIndex;
        _resultCollection.DateRangeFilter = DateRangeFilter;
        _resultCollection.SetExtensionFilters(_selectedExtensionFilters);
        _resultCollection.ApplySortAndFilter();

        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    private void SyncRecent()
    {
        RecentDirectories.Clear();
        foreach (var d in _settings.RecentDirectories) RecentDirectories.Add(d);
        SearchHistory.Clear();
        foreach (var q in _settings.SearchHistory) SearchHistory.Add(q);
        SemanticSearchHistory.Clear();
        foreach (var q in _settings.SemanticSearchHistory) SemanticSearchHistory.Add(q);
    }

    private static void ShowSearchCompleteToast(SearchSummary s, TimeSpan elapsed)
    {
        try
        {
            var title = s.Cancelled ? "Search Cancelled" : "Search Complete";
            var body = $"{s.TotalMatches:N0} matches in {s.FilesWithMatches:N0} files";
            if (s.FilesSkipped > 0)
                body += $" — {s.FilesSkipped:N0} skipped";
            body += $" ({elapsed.TotalSeconds:F1}s)";

            var xml = $"""
                <toast>
                  <visual>
                    <binding template="ToastGeneric">
                      <text>{SecurityElement.Escape(title)}</text>
                      <text>{SecurityElement.Escape(body)}</text>
                    </binding>
                  </visual>
                </toast>
                """;

            var notification = new Microsoft.Windows.AppNotifications.AppNotification(xml);
            Microsoft.Windows.AppNotifications.AppNotificationManager.Default.Show(notification);
        }
        catch
        {
            // Toast failures should never break the app.
        }
    }
}
