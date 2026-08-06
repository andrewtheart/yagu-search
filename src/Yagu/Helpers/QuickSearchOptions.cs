namespace Yagu.Helpers;

/// <summary>
/// A full snapshot of the Advanced Options as they were when a <see cref="QuickSearchItem"/> was saved
/// with "Save current options". Restoring a quick search that carries one puts every Advanced Option back
/// exactly as it was, not just the four toggles the Quick searches tab exposes inline.
/// <para>
/// Only the tab's own fields are editable in the UI; everything here is captured from the live drawer and
/// replayed verbatim, so new Advanced Options only need a field added here plus a line in
/// <c>MainViewModel.CaptureAdvancedOptions</c>/<c>ApplyAdvancedOptions</c> to be covered.
/// </para>
/// The search directory is deliberately NOT captured: a quick search always runs over the folder currently
/// in the directory box.
/// </summary>
public sealed class QuickSearchOptions
{
    // Search tab
    public int SearchModeIndex { get; set; }
    public bool MultilineDotAll { get; set; }
    public int IncludeFilterModeIndex { get; set; }
    public string IncludeGlobs { get; set; } = string.Empty;
    public int ExcludeFilterModeIndex { get; set; }
    public string ExcludeGlobs { get; set; } = string.Empty;

    // Filters tab
    public bool ObeyGitignore { get; set; }
    public string SkipExtensions { get; set; } = string.Empty;
    public bool SearchBinary { get; set; }
    public string BinaryExtensions { get; set; } = string.Empty;
    public bool SearchInsideArchives { get; set; }
    public string ArchiveExtensions { get; set; } = string.Empty;
    public bool SearchOnlineOnlyFiles { get; set; }
    public bool SearchHiddenFiles { get; set; } = true;
    public bool SearchImageText { get; set; }
    public string ImageOcrEngine { get; set; } = string.Empty;
    public bool SearchPdfText { get; set; }
    public bool UseContentIndex { get; set; }

    // Size tab
    public long MinFileSizeBytes { get; set; }
    public long MaxFileSizeBytes { get; set; }

    // Dates tab
    public DateTimeOffset? CreatedAfterDate { get; set; }
    public DateTimeOffset? CreatedBeforeDate { get; set; }
    public DateTimeOffset? ModifiedAfterDate { get; set; }
    public DateTimeOffset? ModifiedBeforeDate { get; set; }

    /// <summary>Advanced tab max depth. Null means unlimited (the empty box).</summary>
    public double? MaxSearchDepth { get; set; }

    public QuickSearchOptions Clone() => new()
    {
        SearchModeIndex = SearchModeIndex,
        MultilineDotAll = MultilineDotAll,
        IncludeFilterModeIndex = IncludeFilterModeIndex,
        IncludeGlobs = IncludeGlobs,
        ExcludeFilterModeIndex = ExcludeFilterModeIndex,
        ExcludeGlobs = ExcludeGlobs,
        ObeyGitignore = ObeyGitignore,
        SkipExtensions = SkipExtensions,
        SearchBinary = SearchBinary,
        BinaryExtensions = BinaryExtensions,
        SearchInsideArchives = SearchInsideArchives,
        ArchiveExtensions = ArchiveExtensions,
        SearchOnlineOnlyFiles = SearchOnlineOnlyFiles,
        SearchHiddenFiles = SearchHiddenFiles,
        SearchImageText = SearchImageText,
        ImageOcrEngine = ImageOcrEngine,
        SearchPdfText = SearchPdfText,
        UseContentIndex = UseContentIndex,
        MinFileSizeBytes = MinFileSizeBytes,
        MaxFileSizeBytes = MaxFileSizeBytes,
        CreatedAfterDate = CreatedAfterDate,
        CreatedBeforeDate = CreatedBeforeDate,
        ModifiedAfterDate = ModifiedAfterDate,
        ModifiedBeforeDate = ModifiedBeforeDate,
        MaxSearchDepth = MaxSearchDepth,
    };

    /// <summary>Trims the free-text fields and clamps a nonsensical depth, so a hand-edited settings file
    /// cannot feed junk into the search. Returns a normalized copy.</summary>
    public QuickSearchOptions Normalized()
    {
        var copy = Clone();
        copy.IncludeGlobs = (copy.IncludeGlobs ?? string.Empty).Trim();
        copy.ExcludeGlobs = (copy.ExcludeGlobs ?? string.Empty).Trim();
        copy.SkipExtensions = (copy.SkipExtensions ?? string.Empty).Trim();
        copy.BinaryExtensions = (copy.BinaryExtensions ?? string.Empty).Trim();
        copy.ArchiveExtensions = (copy.ArchiveExtensions ?? string.Empty).Trim();
        copy.ImageOcrEngine = (copy.ImageOcrEngine ?? string.Empty).Trim();
        copy.SearchModeIndex = Math.Clamp(copy.SearchModeIndex, 0, 3);
        copy.IncludeFilterModeIndex = Math.Clamp(copy.IncludeFilterModeIndex, 0, 1);
        copy.ExcludeFilterModeIndex = Math.Clamp(copy.ExcludeFilterModeIndex, 0, 1);
        copy.MinFileSizeBytes = Math.Max(0, copy.MinFileSizeBytes);
        copy.MaxFileSizeBytes = Math.Max(0, copy.MaxFileSizeBytes);
        if (copy.MaxSearchDepth is { } depth && (double.IsNaN(depth) || depth < 0))
            copy.MaxSearchDepth = null;
        return copy;
    }
}
