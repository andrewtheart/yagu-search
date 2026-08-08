using Yagu.Helpers;
using Yagu.Services;
using Microsoft.Extensions.Logging;
using Yagu.Services.Logging;

namespace Yagu.ViewModels;

/// <summary>
/// Writing the view-model state back to <see cref="Yagu.Services.SettingsService"/>: resolving the
/// startup directory, pinning it, and the single persist path that maps every bound property onto
/// the saved settings (leaving semantic-resolved values out of the saved defaults).
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>User's drag-reordered order for the Advanced Options tab column, as stable tab keys.
    /// Empty means "shipped order". Plain (non-observable) state: nothing binds to it — the drawer
    /// reads it once when it first opens and writes it back after a drag completes.</summary>
    public List<string> AdvancedOptionsTabOrder { get; set; } = [];

    /// <summary>Persisted stable option ID → Advanced Options tab key overrides.</summary>
    public Dictionary<string, string> AdvancedOptionPlacements { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Resolves the directory the box should show at launch. Honors a pinned startup
    /// directory when the user has enabled the pin and a path was captured; otherwise starts empty so
    /// the search defaults to all drives. The legacy LastDirectory value is intentionally not restored
    /// here — it caused the box to spuriously preselect the last-used drive.</summary>
    private string ResolveStartupDirectory()
    {
        if (_settings.PinStartupDirectory && !string.IsNullOrWhiteSpace(_settings.PinnedStartupDirectory))
        {
            return _settings.PinnedStartupDirectory!;
        }

        return string.Empty;
    }

    /// <summary>Pins or unpins the current directory box for the next launch. Pinning snapshots the
    /// box value at the moment of the call (so later edits to the box do not change the pin) and
    /// persists immediately; unpinning clears the saved directory so the box starts empty next launch.
    /// This only affects what the box shows at startup and never overrides the box during a session.</summary>
    public async Task SetStartupDirectoryPinnedAsync(bool pinned)
    {
        PinStartupDirectory = pinned;
        _settings.PinStartupDirectory = pinned;
        _settings.PinnedStartupDirectory = pinned
            ? (string.IsNullOrWhiteSpace(Directory) ? null : Directory.Trim())
            : null;
        // The pinned-path snapshot lives on _settings (not an observable property), so re-pinning to a
        // DIFFERENT folder while PinStartupDirectory stays true wouldn't otherwise re-evaluate the star
        // highlight. Nudge the derived state explicitly so the toggle reflects the new snapshot now.
        OnPropertyChanged(nameof(IsCurrentDirectoryPinned));
        await _settingsService.SaveAsync(_settings).ConfigureAwait(false);
    }

    public async Task PersistSettingsAsync()
    {
        // While a completed semantic search's resolution is shown in Advanced Options, persist the saved
        // filter DEFAULTS (from the snapshot) instead of the resolved values, so a semantic search never
        // changes what a fresh Yagu instance opens with. (Directory is the one exception — a model-
        // resolved directory is meant to override and persist.) The snapshot captures the ENTIRE filter
        // surface — including the Skip/Binary/Archive extension lists (both the active and the persisted
        // Settings* mirror) and the OCR toggle — so a transient "Include & search" un-skip or any future
        // resolution path can never leak a resolved value to disk. Guard every filter field with `d`.
        var d = _semanticResolutionVisible ? _semanticDefaultsSnapshot : null;

        _settings.LastDirectory = Directory;
        _settings.CaseSensitive = d is null ? CaseSensitive : d.CaseSensitive;
        _settings.UseRegex = d is null ? UseRegex : d.UseRegex;
        _settings.ExactMatch = d is null ? ExactMatch : d.ExactMatch;
        _settings.MultilineSearchDefault = Multiline;
        _settings.ContextLines = ContextLines;
        _settings.PreviewContextLines = PreviewContextLines;
        _settings.ObeyGitignore = d is null ? ObeyGitignore : d.ObeyGitignore;
        _settings.GitignoreTakesPrecedence = GitignoreTakesPrecedence;
        _settings.GitignorePrecedencePreference = GitignorePrecedencePreference;
        _settings.DefaultToTraditionalSearchMode = DefaultToTraditionalSearchMode;
        _settings.SemanticSearchEnabled = SemanticSearchAvailable;
        _settings.SemanticModelAlias = SemanticModelAlias;
        _settings.SemanticDevicePreferenceOrder = SemanticDevicePreferenceOrder;
        _settings.SemanticUnloadModelAfterUse = SemanticUnloadModelAfterUse;
        _settings.IncludeGlobs = d is null ? IncludeGlobs : d.IncludeGlobs;
        _settings.ExcludeGlobs = d is null ? ExcludeGlobs : d.ExcludeGlobs;
        _settings.IncludeFilterModeIndex = d is null ? IncludeFilterModeIndex : d.IncludeFilterModeIndex;
        _settings.ExcludeFilterModeIndex = d is null ? ExcludeFilterModeIndex : d.ExcludeFilterModeIndex;
        _settings.MinFileSizeBytes = d is null ? MinFileSizeBytes : d.MinFileSizeBytes;
        _settings.MaxFileSizeBytes = d is null ? MaxFileSizeBytes : d.MaxFileSizeBytes;
        _settings.CreatedAfterDate = d is null ? CreatedAfterDate : d.CreatedAfterDate;
        _settings.CreatedBeforeDate = d is null ? CreatedBeforeDate : d.CreatedBeforeDate;
        _settings.ModifiedAfterDate = d is null ? ModifiedAfterDate : d.ModifiedAfterDate;
        _settings.ModifiedBeforeDate = d is null ? ModifiedBeforeDate : d.ModifiedBeforeDate;
        _settings.DefaultMinFileSizeBytes = DefaultMinFileSizeBytes;
        _settings.DefaultMaxFileSizeBytes = DefaultMaxFileSizeBytes;
        _settings.DefaultCreatedAfterDate = DefaultCreatedAfterDate;
        _settings.DefaultCreatedBeforeDate = DefaultCreatedBeforeDate;
        _settings.DefaultModifiedAfterDate = DefaultModifiedAfterDate;
        _settings.DefaultModifiedBeforeDate = DefaultModifiedBeforeDate;
        _settings.MaxResults = MaxResults;
        _settings.EditorCommand = EditorCommand;
        _settings.PreviewModeIndex = PreviewModeIndex;
        _settings.ThemeModeIndex = AppThemeService.NormalizeThemeModeIndex(ThemeModeIndex);
        _settings.PreviewWordWrap = PreviewWordWrap;
        _settings.PreviewWrapModeIndex = PreviewWrapModeIndex;
        _settings.PreviewLongLineWarningIndex = PreviewLongLineWarningIndex;
        _settings.PreviewAutoLoadMatches = PreviewAutoLoadMatches;
        _settings.SelectedPreviewContentBackgroundColor = ColorStringHelper.Normalize(
            SelectedPreviewContentBackgroundColor,
            Windows.UI.Color.FromArgb(0xFF, 0x00, 0x00, 0x00));
        _settings.UnselectedPreviewContentBackgroundColor = ColorStringHelper.Normalize(
            UnselectedPreviewContentBackgroundColor,
            Windows.UI.Color.FromArgb(0xFF, 0x1E, 0x1E, 0x1E));
        _settings.PreviewGutterContextColor = ColorStringHelper.Normalize(
            PreviewGutterContextColor,
            Windows.UI.Color.FromArgb(0xFF, 0x9C, 0xDC, 0xFE));
        _settings.PreviewGutterMatchColor = ColorStringHelper.Normalize(
            PreviewGutterMatchColor,
            Windows.UI.Color.FromArgb(0xFF, 0x9C, 0xDC, 0xFE));
        _settings.PreviewEditorGutterColor = ColorStringHelper.Normalize(
            PreviewEditorGutterColor,
            Windows.UI.Color.FromArgb(0xFF, 0x3A, 0x8F, 0xD6));
        // Preserve the empty "Auto" sentinel; only normalize an explicit override to canonical ARGB hex.
        _settings.PreviewEditorTextColor = string.IsNullOrWhiteSpace(PreviewEditorTextColor)
            ? string.Empty
            : ColorStringHelper.Normalize(
                PreviewEditorTextColor,
                Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
        _settings.PreviewMatchTextColor = ColorStringHelper.Normalize(
            PreviewMatchTextColor,
            Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xD7, 0x00));
        _settings.PreviewOverlayColor = ColorStringHelper.Normalize(
            PreviewOverlayColor,
            Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x45, 0x00));
        _settings.PreviewMatchLineColor = ColorStringHelper.Normalize(
            PreviewMatchLineColor,
            Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
        _settings.PreviewShowMoreEllipsisColor = ColorStringHelper.Normalize(
            PreviewShowMoreEllipsisColor,
            Windows.UI.Color.FromArgb(0xFF, 0x1E, 0x90, 0xFF));
        _settings.PreviewShowMoreEllipsisFontSize = Math.Clamp(
            PreviewShowMoreEllipsisFontSize <= 0 ? AppSettings.DefaultPreviewShowMoreEllipsisFontSize : PreviewShowMoreEllipsisFontSize,
            6,
            72);
        _settings.PreviewTextFontFamily = string.IsNullOrWhiteSpace(PreviewTextFontFamily)
            ? AppSettings.DefaultPreviewTextFontFamily
            : PreviewTextFontFamily.Trim();
        _settings.PreviewTextFontSize = Math.Clamp(
            PreviewTextFontSize <= 0 ? AppSettings.DefaultPreviewTextFontSize : PreviewTextFontSize,
            6,
            72);
        _settings.PreviewEditorFontFamily = string.IsNullOrWhiteSpace(PreviewEditorFontFamily)
            ? AppSettings.DefaultPreviewEditorFontFamily
            : PreviewEditorFontFamily.Trim();
        _settings.PreviewEditorFontSize = Math.Clamp(
            PreviewEditorFontSize <= 0 ? AppSettings.DefaultPreviewEditorFontSize : PreviewEditorFontSize,
            6,
            72);
        _settings.ResultListMatchTextFontFamily = string.IsNullOrWhiteSpace(ResultListMatchTextFontFamily)
            ? AppSettings.DefaultResultListMatchTextFontFamily
            : ResultListMatchTextFontFamily.Trim();
        _settings.ResultListMatchTextFontSize = Math.Clamp(
            ResultListMatchTextFontSize <= 0 ? AppSettings.DefaultResultListMatchTextFontSize : ResultListMatchTextFontSize,
            6,
            72);
        _settings.ResultListMatchHighlightColor = ColorStringHelper.Normalize(
            ResultListMatchHighlightColor,
            Windows.UI.Color.FromArgb(0xFF, 0xB8, 0x86, 0x0B));

        // ── File list overlay ──
        _settings.FileListOverlayHeight = Math.Clamp(FileListOverlayHeight <= 0 ? AppSettings.DefaultFileListOverlayHeight : FileListOverlayHeight, 20, 100);
        _settings.FileListOverlayFontSize = Math.Clamp(FileListOverlayFontSize <= 0 ? AppSettings.DefaultFileListOverlayFontSize : FileListOverlayFontSize, 6, 72);
        _settings.FileListOverlayFontColor = string.IsNullOrWhiteSpace(FileListOverlayFontColor) ? AppSettings.DefaultFileListOverlayFontColor : FileListOverlayFontColor.Trim();
        _settings.FileListOverlayFontFamily = string.IsNullOrWhiteSpace(FileListOverlayFontFamily) ? AppSettings.DefaultFileListOverlayFontFamily : FileListOverlayFontFamily.Trim();

        // ── Preview sticky header ──
        _settings.PreviewStickyHeaderHeight = Math.Clamp(PreviewStickyHeaderHeight <= 0 ? AppSettings.DefaultPreviewStickyHeaderHeight : PreviewStickyHeaderHeight, 20, 100);
        _settings.PreviewStickyHeaderFileNameFontSize = Math.Clamp(PreviewStickyHeaderFileNameFontSize <= 0 ? AppSettings.DefaultPreviewStickyHeaderFileNameFontSize : PreviewStickyHeaderFileNameFontSize, 6, 72);
        _settings.PreviewStickyHeaderFileNameFontColor = string.IsNullOrWhiteSpace(PreviewStickyHeaderFileNameFontColor) ? AppSettings.DefaultPreviewStickyHeaderFileNameFontColor : PreviewStickyHeaderFileNameFontColor.Trim();
        _settings.PreviewStickyHeaderFileNameFontFamily = string.IsNullOrWhiteSpace(PreviewStickyHeaderFileNameFontFamily) ? AppSettings.DefaultPreviewStickyHeaderFileNameFontFamily : PreviewStickyHeaderFileNameFontFamily.Trim();
        _settings.PreviewStickyHeaderDetailFontSize = Math.Clamp(PreviewStickyHeaderDetailFontSize <= 0 ? AppSettings.DefaultPreviewStickyHeaderDetailFontSize : PreviewStickyHeaderDetailFontSize, 6, 72);
        _settings.PreviewStickyHeaderDetailFontColor = string.IsNullOrWhiteSpace(PreviewStickyHeaderDetailFontColor) ? AppSettings.DefaultPreviewStickyHeaderDetailFontColor : PreviewStickyHeaderDetailFontColor.Trim();
        _settings.PreviewStickyHeaderDetailFontFamily = string.IsNullOrWhiteSpace(PreviewStickyHeaderDetailFontFamily) ? AppSettings.DefaultPreviewStickyHeaderDetailFontFamily : PreviewStickyHeaderDetailFontFamily.Trim();

        // ── File list drawer labels ──
        _settings.DrawerFileNameFontSize = Math.Clamp(DrawerFileNameFontSize <= 0 ? AppSettings.DefaultDrawerFileNameFontSize : DrawerFileNameFontSize, 6, 72);
        _settings.DrawerFileNameFontColor = string.IsNullOrWhiteSpace(DrawerFileNameFontColor) ? AppSettings.DefaultDrawerFileNameFontColor : DrawerFileNameFontColor.Trim();
        _settings.DrawerFileNameFontFamily = string.IsNullOrWhiteSpace(DrawerFileNameFontFamily) ? AppSettings.DefaultDrawerFileNameFontFamily : DrawerFileNameFontFamily.Trim();
        _settings.DrawerDirectoryFontSize = Math.Clamp(DrawerDirectoryFontSize <= 0 ? AppSettings.DefaultDrawerDirectoryFontSize : DrawerDirectoryFontSize, 6, 72);
        _settings.DrawerDirectoryFontColor = string.IsNullOrWhiteSpace(DrawerDirectoryFontColor) ? AppSettings.DefaultDrawerDirectoryFontColor : DrawerDirectoryFontColor.Trim();
        _settings.DrawerDirectoryFontFamily = string.IsNullOrWhiteSpace(DrawerDirectoryFontFamily) ? AppSettings.DefaultDrawerDirectoryFontFamily : DrawerDirectoryFontFamily.Trim();
        _settings.DrawerMetadataFontSize = Math.Clamp(DrawerMetadataFontSize <= 0 ? AppSettings.DefaultDrawerMetadataFontSize : DrawerMetadataFontSize, 6, 72);
        _settings.DrawerMetadataFontColor = string.IsNullOrWhiteSpace(DrawerMetadataFontColor) ? AppSettings.DefaultDrawerMetadataFontColor : DrawerMetadataFontColor.Trim();
        _settings.DrawerMetadataFontFamily = string.IsNullOrWhiteSpace(DrawerMetadataFontFamily) ? AppSettings.DefaultDrawerMetadataFontFamily : DrawerMetadataFontFamily.Trim();

        _settings.LogLevelIndex = FileLogLevelIndex;
        _settings.ConsoleLogLevelIndex = ConsoleLogLevelIndex;
        _settings.FileListerBackendIndex = FileListerBackendIndex;
        _settings.ParallelismIndex = ParallelismIndex;
        _settings.IoOversubscriptionIndex = IoOversubscriptionIndex;
        _settings.LineTruncationLength = LineTruncationLength;
        _settings.MaxRecentItems = MaxRecentItems;
        _settings.MaxSemanticRecentItems = MaxSemanticRecentItems;
        _settings.AutocompleteDropdownVisibleItems = AutocompleteDropdownVisibleItems;
        _settings.GlobalHotkeyEnabled = GlobalHotkeyEnabled;
        _settings.GlobalHotkeyKey = HotkeyService.TryNormalizeLetter(GlobalHotkeyKey, out var hotkeyKey)
            ? hotkeyKey.ToString()
            : HotkeyService.DefaultStartKey.ToString();
        _settings.MemoryLimitMB = MemoryLimitMB;
        _settings.MemoryPressurePercent = MemoryPressurePercent;
        _settings.SearchResultTempDirectory = ResultStoreTempLocationService.NormalizeTempDirectory(SearchResultTempDirectory);
        _settings.HasChosenSearchResultTempDirectory = HasChosenSearchResultTempDirectory;
        _settings.LowDiskSpaceWarningPercent = AppSettings.NormalizeLowDiskSpaceWarningPercent(LowDiskSpaceWarningPercent);
        _settings.ShowMemoryPressureWarningLabel = ShowMemoryPressureWarningLabel;
        _settings.ShowStatsForNerds = ShowStatsForNerds;
        _settings.ShowResourceUsageInStatusBar = ShowResourceUsageInStatusBar;
        _settings.ShowDebugPanel = ShowDebugPanel;
        _settings.ShowBuildNumberInTitleBar = ShowBuildNumberInTitleBar;
        _settings.ShowAutoScrollResultsCheckbox = ShowAutoScrollResultsCheckbox;
        _settings.SdkChannelBufferSize = SdkChannelBufferSize;
        _settings.MaxMatchesPerFile = MaxMatchesPerFile;
        _settings.MaxMatchesPerLine = MaxMatchesPerLine < 0 ? 0 : MaxMatchesPerLine;
        _settings.FileIoTimeoutSeconds = AppSettings.NormalizeFileIoTimeoutSeconds(FileIoTimeoutSeconds);
        _settings.AbsoluteMaxResults = AbsoluteMaxResults < 0 ? 0 : AbsoluteMaxResults;
        _settings.SkipBinary = d is null ? SkipBinary : d.SkipBinary;
        _settings.SearchOnlineOnlyFiles = SearchOnlineOnlyFiles;
        _settings.SearchHiddenFiles = d is null ? SearchHiddenFiles : d.SearchHiddenFiles;
        _settings.SearchImageText = d is null ? SearchImageText : d.SearchImageText;
        _settings.SearchPdfText = SearchPdfText;
        _settings.ImageOcrEngine = AppSettings.NormalizeImageOcrEngine(ImageOcrEngine);
        _settings.ImageOcrModel = AppSettings.NormalizeImageOcrModel(ImageOcrModel);
        _settings.ImageOcrMaxSide = AppSettings.NormalizeImageOcrMaxSide(ImageOcrMaxSide);
        _settings.ImageOcrWorkerParallelism = AppSettings.NormalizeImageOcrWorkerParallelism(ImageOcrWorkerParallelism);
        // The startup-directory pin flag mirrors the star toggle. The captured directory itself
        // (PinnedStartupDirectory) is a snapshot written by SetStartupDirectoryPinnedAsync at click
        // time, so it is intentionally NOT recaptured here and never drifts as the box changes.
        _settings.PinStartupDirectory = PinStartupDirectory;
        _settings.SearchInsideArchives = d is null ? SearchInsideArchives : d.SearchInsideArchives;
        _settings.ArchiveExtensions = d is null ? SettingsArchiveExtensions : d.SettingsArchiveExtensions;
        _settings.SkipExtensions = d is null ? SettingsSkipExtensions : d.SettingsSkipExtensions;
        _settings.BinaryExtensions = d is null ? SettingsBinaryExtensions : d.SettingsBinaryExtensions;
        _settings.SuppressAdminWarning = SuppressAdminWarning;
        _settings.SuppressEverythingNotRunningPrompt = SuppressEverythingNotRunningPrompt;
        // Tab order is a drawer-layout preference, not a search option, so it is persisted directly
        // and deliberately left out of the Advanced Options "save as defaults" / "reset" snapshot.
        _settings.AdvancedOptionsTabOrder = AdvancedOptionsTabOrder;
        _settings.AdvancedOptionPlacements = new Dictionary<string, string>(AdvancedOptionPlacements, StringComparer.Ordinal);
        _settings.SuppressEverythingIndexCoverageWarning = SuppressEverythingIndexCoverageWarning;
        _settings.SuppressExcludedExtensionWarnings = SuppressExcludedExtensionWarnings;
        _settings.IncludeExcludedExtensionByDefault = IncludeExcludedExtensionByDefault;
        _settings.SuppressFontContrastWarnings = SuppressFontContrastWarnings;
        _settings.FontContrastReminderAfterUtc = FontContrastReminderAfterUtc;
        _settings.ExcludeAdminProtectedPaths = ExcludeAdminProtectedPaths;
        _settings.AdminProtectedPathSegments = AdminProtectedPathSegments;
        _settings.HasCompletedFirstRun = HasCompletedFirstRun;
        _settings.HasShownFileDrawerIntroTip = HasShownFileDrawerIntroTip;
        _settings.HasShownFileDrawerLineNumberIntroTip = HasShownFileDrawerLineNumberIntroTip;
        _settings.HasShownPreviewMatchIntroTip = HasShownPreviewMatchIntroTip;
        _settings.LimitParallelismOnHdd = LimitParallelismOnHdd;
        _settings.SuppressHddParallelismWarnings = SuppressHddParallelismWarnings;
        _settings.SearchAllDrivesIncludesNetwork = SearchAllDrivesIncludesNetwork;
        _settings.SearchAllDrivesIncludesRemovable = SearchAllDrivesIncludesRemovable;
        _settings.SearchAllDrivesIncludesCloud = SearchAllDrivesIncludesCloud;
        _settings.SearchAllDrivesForceFullScan = SearchAllDrivesForceFullScan;
        _settings.BackupBeforeSave = BackupBeforeSave;
        _settings.ShowEditorSavedOverlay = ShowEditorSavedOverlay;
        _settings.EditorSyntaxHighlightingEnabled = EditorSyntaxHighlightingEnabled;
        _settings.WindowFocusBehavior = WindowFocusBehavior;
        _settings.StartInLauncherMode = StartInLauncherMode;
        _settings.CloseToTray = CloseToTray;
        _settings.HasShownCloseToTrayNotification = HasShownCloseToTrayNotification;
        _settings.MaximizeOnStartup = MaximizeOnStartup;
        _settings.LaunchWindowPosition = LaunchWindowPosition;
        _settings.LauncherWindowPosition = LauncherWindowPosition;
        _settings.AdvancedOptionsCollapsedWidthModeIndex = NormalizeAdvancedOptionsCollapsedWidthModeIndex(AdvancedOptionsCollapsedWidthModeIndex);
        _settings.TerminalDefaultWorkingDirectory = string.IsNullOrWhiteSpace(TerminalDefaultWorkingDirectory)
            ? string.Empty
            : TerminalDefaultWorkingDirectory.Trim();
        _settings.TerminalShellKindIndex = TerminalShell.NormalizeSettingsIndex(TerminalShellKindIndex);
        _settings.FileHeaderCheckAddsToPreview = FileHeaderCheckAddsToPreview;
        _settings.MatchLineCheckAddsToPreview = MatchLineCheckAddsToPreview;
        _settings.PreviewEditorMaxSizeMB = PreviewEditorMaxSizeMB;
        _settings.PreviewEditorMaxTextLength = PreviewEditorMaxTextLength;
        _settings.PreviewEditorMaxLineLength = PreviewEditorMaxLineLength;
        _settings.PreviewEditorPopOutMaxSizeMB = PreviewEditorPopOutMaxSizeMB;
        _settings.PreviewEditorPopOutArrangementIndex = PreviewEditorPopOutArrangementIndex;
        _settings.ContentSearchFileSizeMB = ContentSearchFileSizeMB;
        _settings.MaxResultsCeiling = MaxResultsCeiling > 0 ? MaxResultsCeiling : 50_000;
        _settings.MmfConcurrencyLimit = MmfConcurrencyLimit;
        _settings.NativeConcurrencyLimit = NativeConcurrencyLimit;
        _settings.MaxMatchesPerSection = MaxMatchesPerSection;
        _settings.PreviewSectionPageSize = PreviewSectionPageSize;
        _settings.MaxSelectedFilesPerPreview = MaxSelectedFilesPerPreview;
        _settings.MaxSelectedResultsPerPreview = MaxSelectedResultsPerPreview;
        _settings.MaxRenderedMatchesPerSection = MaxRenderedMatchesPerSection;
        _settings.FullFilePreviewLimitMB = FullFilePreviewLimitMB;
        _settings.FullFilePreviewMaxRenderLines = FullFilePreviewMaxRenderLines;
        _settings.FullFilePreviewMaxRenderChars = FullFilePreviewMaxRenderChars;
        _settings.ArchiveMaxNestingDepth = ArchiveMaxNestingDepth;
        _settings.ArchiveMaxEntryMB = ArchiveMaxEntryMB;

        Helpers.LineTruncator.TruncatedLength = LineTruncationLength;

        await _settingsService.SaveAsync(_settings).ConfigureAwait(false);
        YaguLog.For("Settings").LogInformation("Settings persisted");
        LogService.Instance.Flush();
    }
}
