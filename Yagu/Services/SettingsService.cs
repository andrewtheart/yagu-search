using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Yagu.Models;

namespace Yagu.Services;

[JsonSerializable(typeof(AppSettings))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal sealed partial class AppSettingsJsonContext : JsonSerializerContext { }

public sealed class AppSettings
{
    public const string LegacyDefaultSkipExtensions = "exe;dll;pdb;obj;lib;so;dylib;zip;gz;tar;7z;rar;bz2;xz;iso;cab;msi;nupkg;whl;png;jpg;jpeg;gif;bmp;ico;tif;tiff;webp;svg;mp3;mp4;avi;mov;wmv;flv;mkv;wav;ogg;flac;woff;woff2;ttf;eot;otf;pdf;doc;docx;xls;xlsx;ppt;pptx";
    public const string LegacyExpandedBinaryPrefilterExtensions = "exe;dll;pdb;obj;lib;so;dylib;png;jpg;jpeg;gif;bmp;ico;tif;tiff;webp;svg;mp3;mp4;avi;mov;wmv;flv;mkv;wav;ogg;flac;woff;woff2;ttf;eot;otf;pdf;doc;xls;ppt;com;scr;sys;drv;ocx;cpl;mui;winmd;pri;cat;res;resources;o;a;lo;la;ilk;iobj;ipdb;exp;pyc;pyo;class;dex;wasm;bin;dat;db;db3;sqlite;sqlite3;edb;mdb;accdb;ldb;sdf;cache;tmp;bak;etl;evtx;dmp;mdmp;hdmp;hprof;vhd;vhdx;vmdk;pak;usm;bundle;assets;m4a;webm;heic;heif;avif";
    public const string DefaultSkipExtensions = "png;jpg;jpeg;gif;bmp;ico;tif;tiff;webp;svg;mp3;mp4;avi;mov;wmv;flv;mkv;wav;ogg;flac;m4a;webm;woff;woff2;ttf;eot;otf;pdf;doc;xls;ppt;bin;dat;db;db3;sqlite;sqlite3;edb;mdb;accdb;ldb;sdf;cache;tmp;bak;etl;evtx;dmp;mdmp;hdmp;hprof;vhd;vhdx;vmdk;pak;usm;bundle;assets;heic;heif;avif";
    public const string DefaultBinaryExtensions = "exe;dll;pdb;obj;lib;so;dylib;com;scr;sys;drv;ocx;cpl;mui;winmd;pri;cat;res;resources;o;a;lo;la;ilk;iobj;ipdb;exp;pyc;pyo;class;dex;wasm";
    public const string DefaultArchiveExtensions = "zip;jar;war;ear;nupkg;vsix;apk;aab;aar;appx;msix;appxbundle;msixbundle;docx;xlsx;pptx;odt;ods;odp;epub;whl;gz;tar;7z;rar;bz2;xz;iso;cab;msi;tgz;tbz2;txz;zst;zstd;br;lz4;lzma";
    public const string DefaultExcludeGlobs = "node_modules;bin;obj;.git";
    public const string DefaultSelectedPreviewContentBackgroundColor = "#FF000000";
    public const string DefaultUnselectedPreviewContentBackgroundColor = "#FF1E1E1E";

    // Preview editor font colors (ARGB hex strings)
    public const string LegacyDefaultPreviewGutterContextColor = "#FF505050";
    public const string LegacyDefaultPreviewGutterMatchColor = "#FF32CD32";
    public const string DefaultPreviewGutterColor = "#FF9CDCFE";
    public const string DefaultPreviewGutterContextColor = DefaultPreviewGutterColor;
    public const string DefaultPreviewGutterMatchColor = DefaultPreviewGutterColor;
    public const string DefaultPreviewEditorGutterColor = "#FF3A8FD6"; // Darker blue (passes light+dark)
    // Empty string means "follow the app/system theme" (white on dark, near-black on light). A non-empty
    // ARGB hex string is an explicit user override applied to the built-in editor's body text.
    public const string DefaultPreviewEditorTextColor = "";
    public const string DefaultPreviewMatchTextColor = "#FFFFD700"; // Gold
    public const string DefaultPreviewOverlayColor = "#FFFF4500"; // OrangeRed
    public const string DefaultPreviewMatchLineColor = "#FFFFFFFF"; // White
    public const string DefaultPreviewShowMoreEllipsisColor = "#FF1E90FF"; // DodgerBlue
    public const int DefaultPreviewShowMoreEllipsisFontSize = 17;
    public const string DefaultPreviewTextFontFamily = "Consolas";
    public const int DefaultPreviewTextFontSize = 14;
    public const string DefaultPreviewEditorFontFamily = "Consolas, Cascadia Mono, Segoe UI, Segoe UI Symbol, Segoe UI Emoji";
    public const int DefaultPreviewEditorFontSize = 13;
    public const string DefaultResultListMatchTextFontFamily = "Consolas";
    public const int DefaultResultListMatchTextFontSize = 12;
    public const string DefaultResultListMatchHighlightColor = "#FFB8860B"; // DarkGoldenrod (passes light+dark)

    // ── File list overlay (sticky header in results list) ──
    public const int DefaultFileListOverlayHeight = 36;
    public const int DefaultFileListOverlayFontSize = 12;
    public const string DefaultFileListOverlayFontColor = "#FFFFFFFF";
    public const string DefaultFileListOverlayFontFamily = "Segoe UI";

    // ── Preview sticky file header overlay ──
    public const int DefaultPreviewStickyHeaderHeight = 36;
    public const int DefaultPreviewStickyHeaderFileNameFontSize = 14;
    public const string DefaultPreviewStickyHeaderFileNameFontColor = "#FFFFFFFF";
    public const string DefaultPreviewStickyHeaderFileNameFontFamily = "Segoe UI";
    public const int DefaultPreviewStickyHeaderDetailFontSize = 12;
    public const string DefaultPreviewStickyHeaderDetailFontColor = "#B3FFFFFF"; // White @ 70% opacity
    public const string DefaultPreviewStickyHeaderDetailFontFamily = "Segoe UI";

    // ── File list drawer labels ──
    public const int DefaultDrawerFileNameFontSize = 13;
    public const string DefaultDrawerFileNameFontColor = "#FFFFFFFF";
    public const string DefaultDrawerFileNameFontFamily = "Segoe UI";
    public const int DefaultDrawerDirectoryFontSize = 13;
    public const string DefaultDrawerDirectoryFontColor = "#8CFFFFFF"; // White @ 55% opacity
    public const string DefaultDrawerDirectoryFontFamily = "Segoe UI";
    public const int DefaultDrawerMetadataFontSize = 11;
    public const string DefaultDrawerMetadataFontColor = "#73FFFFFF"; // White @ 45% opacity
    public const string DefaultDrawerMetadataFontFamily = "Segoe UI";

    public const int DefaultLowDiskSpaceWarningPercent = 98;
    public const int MinimumLowDiskSpaceWarningPercent = 1;
    public const int MaximumLowDiskSpaceWarningPercent = 99;

    public static int NormalizeLowDiskSpaceWarningPercent(int value) => value <= 0
        ? DefaultLowDiskSpaceWarningPercent
        : Math.Clamp(value, MinimumLowDiskSpaceWarningPercent, MaximumLowDiskSpaceWarningPercent);

    public string? LastDirectory { get; set; }
    public List<string> RecentDirectories { get; set; } = [];
    public List<string> SearchHistory { get; set; } = [];
    [JsonIgnore] public bool CaseSensitive { get; set; }
    [JsonIgnore] public bool UseRegex { get; set; }
    [JsonIgnore] public bool ExactMatch { get; set; } = true;
    [JsonIgnore] public bool ObeyGitignore { get; set; }
    public bool GitignoreTakesPrecedence { get; set; } = true;
    // User's saved preference for .gitignore vs Include-filter precedence on conflict.
    // null = unset (ask via the precedence dialog), true = .gitignore wins, false = Include filter wins.
    public bool? GitignorePrecedencePreference { get; set; }
    public int ContextLines { get; set; } = 3;
    public int PreviewContextLines { get; set; } = 10;
    // Advanced-Options path filters are per-session only: edits made in Advanced
    // Options must never persist to settings.json. They reset to defaults on next
    // launch, matching the size/date filters below.
    [JsonIgnore] public string IncludeGlobs { get; set; } = string.Empty;
    [JsonIgnore] public string ExcludeGlobs { get; set; } = DefaultExcludeGlobs;
    [JsonIgnore] public int IncludeFilterModeIndex { get; set; }
    [JsonIgnore] public int ExcludeFilterModeIndex { get; set; }
    [JsonIgnore] public long MinFileSizeBytes { get; set; }
    [JsonIgnore] public long MaxFileSizeBytes { get; set; }
    [JsonIgnore] public DateTimeOffset? CreatedAfterDate { get; set; }
    [JsonIgnore] public DateTimeOffset? CreatedBeforeDate { get; set; }
    [JsonIgnore] public DateTimeOffset? ModifiedAfterDate { get; set; }
    [JsonIgnore] public DateTimeOffset? ModifiedBeforeDate { get; set; }
    public long DefaultMinFileSizeBytes { get; set; }
    public long DefaultMaxFileSizeBytes { get; set; }
    public DateTimeOffset? DefaultCreatedAfterDate { get; set; }
    public DateTimeOffset? DefaultCreatedBeforeDate { get; set; }
    public DateTimeOffset? DefaultModifiedAfterDate { get; set; }
    public DateTimeOffset? DefaultModifiedBeforeDate { get; set; }
    public int MaxResults { get; set; }
    public string EditorCommand { get; set; } = EditorLauncher.DefaultCommand;
    public double SplitPanePosition { get; set; } = 0.5;
    public bool GlobalHotkeyEnabled { get; set; }
    public string GlobalHotkeyKey { get; set; } = HotkeyService.DefaultStartKey.ToString();
    public int PreviewModeIndex { get; set; } = 1; // 0 = Concatenated, 1 = Multi-highlight
    public int ThemeModeIndex { get; set; } // 0 = Auto (system theme), 1 = Dark, 2 = Light
    public bool PreviewWordWrap { get; set; }
    public int PreviewWrapModeIndex { get; set; } = 2; // 0 = Wrap, 1 = legacy PartialWrap, 2 = NoWrap
    public string SelectedPreviewContentBackgroundColor { get; set; } = DefaultSelectedPreviewContentBackgroundColor;
    public string UnselectedPreviewContentBackgroundColor { get; set; } = DefaultUnselectedPreviewContentBackgroundColor;
    public string PreviewGutterContextColor { get; set; } = DefaultPreviewGutterContextColor;
    public string PreviewGutterMatchColor { get; set; } = DefaultPreviewGutterMatchColor;
    public string PreviewEditorGutterColor { get; set; } = DefaultPreviewEditorGutterColor;
    public string PreviewEditorTextColor { get; set; } = DefaultPreviewEditorTextColor;
    public string PreviewMatchTextColor { get; set; } = DefaultPreviewMatchTextColor;
    public string PreviewOverlayColor { get; set; } = DefaultPreviewOverlayColor;
    public string PreviewMatchLineColor { get; set; } = DefaultPreviewMatchLineColor;
    public string PreviewShowMoreEllipsisColor { get; set; } = DefaultPreviewShowMoreEllipsisColor;
    public int PreviewShowMoreEllipsisFontSize { get; set; } = DefaultPreviewShowMoreEllipsisFontSize;
    public string PreviewTextFontFamily { get; set; } = DefaultPreviewTextFontFamily;
    public int PreviewTextFontSize { get; set; } = DefaultPreviewTextFontSize;
    public string PreviewEditorFontFamily { get; set; } = DefaultPreviewEditorFontFamily;
    public int PreviewEditorFontSize { get; set; } = DefaultPreviewEditorFontSize;
    public string ResultListMatchTextFontFamily { get; set; } = DefaultResultListMatchTextFontFamily;
    public int ResultListMatchTextFontSize { get; set; } = DefaultResultListMatchTextFontSize;
    public string ResultListMatchHighlightColor { get; set; } = DefaultResultListMatchHighlightColor;

    // ── File list overlay settings ──
    public int FileListOverlayHeight { get; set; } = DefaultFileListOverlayHeight;
    public int FileListOverlayFontSize { get; set; } = DefaultFileListOverlayFontSize;
    public string FileListOverlayFontColor { get; set; } = DefaultFileListOverlayFontColor;
    public string FileListOverlayFontFamily { get; set; } = DefaultFileListOverlayFontFamily;

    // ── Preview sticky file header overlay settings ──
    public int PreviewStickyHeaderHeight { get; set; } = DefaultPreviewStickyHeaderHeight;
    public int PreviewStickyHeaderFileNameFontSize { get; set; } = DefaultPreviewStickyHeaderFileNameFontSize;
    public string PreviewStickyHeaderFileNameFontColor { get; set; } = DefaultPreviewStickyHeaderFileNameFontColor;
    public string PreviewStickyHeaderFileNameFontFamily { get; set; } = DefaultPreviewStickyHeaderFileNameFontFamily;
    public int PreviewStickyHeaderDetailFontSize { get; set; } = DefaultPreviewStickyHeaderDetailFontSize;
    public string PreviewStickyHeaderDetailFontColor { get; set; } = DefaultPreviewStickyHeaderDetailFontColor;
    public string PreviewStickyHeaderDetailFontFamily { get; set; } = DefaultPreviewStickyHeaderDetailFontFamily;

    // ── File list drawer label settings ──
    public int DrawerFileNameFontSize { get; set; } = DefaultDrawerFileNameFontSize;
    public string DrawerFileNameFontColor { get; set; } = DefaultDrawerFileNameFontColor;
    public string DrawerFileNameFontFamily { get; set; } = DefaultDrawerFileNameFontFamily;
    public int DrawerDirectoryFontSize { get; set; } = DefaultDrawerDirectoryFontSize;
    public string DrawerDirectoryFontColor { get; set; } = DefaultDrawerDirectoryFontColor;
    public string DrawerDirectoryFontFamily { get; set; } = DefaultDrawerDirectoryFontFamily;
    public int DrawerMetadataFontSize { get; set; } = DefaultDrawerMetadataFontSize;
    public string DrawerMetadataFontColor { get; set; } = DefaultDrawerMetadataFontColor;
    public string DrawerMetadataFontFamily { get; set; } = DefaultDrawerMetadataFontFamily;

    public int LogLevelIndex { get; set; } = 1; // -1 = None, 0 = Critical, 1 = Warning, 2 = Info, 3 = Verbose (file logging)
    public int ConsoleLogLevelIndex { get; set; } = 1; // -1 = None, 0 = Critical, 1 = Warning, 2 = Info, 3 = Verbose
    public int FileListerBackendIndex { get; set; } // 0 = Auto, 1 = SDK, 2 = es.exe, 3 = Managed
    public int ParallelismIndex { get; set; } = 4; // 0 = safe cap, 1 = 1, 2 = half cores, 3 = 2x cores, 4 = all cores
    public int IoOversubscriptionIndex { get; set; } // 0 = Auto (SSD 1x, HDD 2x), 1 = 1x, 2 = 2x, 3 = 3x
    public int LineTruncationLength { get; set; } = 500;
    public int MaxRecentItems { get; set; } = 20;
    /// <summary>Hard process memory cap in MB. 0 = automatic sub-GB paging target.</summary>
    public int MemoryLimitMB { get; set; }
    /// <summary>System-wide memory pressure threshold (0-100). Search evicts cached results and switches to memory-saving mode when total machine memory usage exceeds this %. 0 = disabled.</summary>
    public int MemoryPressurePercent { get; set; } = 75;
    /// <summary>Directory used for memory-saving search result temp files.</summary>
    public string? SearchResultTempDirectory { get; set; }
    /// <summary>Whether the user has chosen the search result temp-file location.</summary>
    public bool HasChosenSearchResultTempDirectory { get; set; }
    /// <summary>Terminates active searches when the search result temp-file drive is more than this full. Valid range 1-99.</summary>
    public int LowDiskSpaceWarningPercent { get; set; } = DefaultLowDiskSpaceWarningPercent;
    /// <summary>When true, show the memory pressure warning label in the results toolbar. Hidden by default.</summary>
    public bool ShowMemoryPressureWarningLabel { get; set; }
    /// <summary>When true, show throughput labels and disk utilization sparkline in the bottom status bar.</summary>
    public bool ShowStatsForNerds { get; set; }
    /// <summary>When true, append the app version/build number to the main title bar.</summary>
    public bool ShowBuildNumberInTitleBar { get; set; }
    /// <summary>When true, show the Auto-scroll checkbox in the results toolbar. Hidden by default.</summary>
    public bool ShowAutoScrollResultsCheckbox { get; set; }
    /// <summary>Bounded channel buffer size for the Everything SDK streaming path. Higher values use more memory but can improve throughput.</summary>
    public int SdkChannelBufferSize { get; set; } = 4096;
    /// <summary>Current directory recursion depth. 0 = unlimited. This is intentionally session-only.</summary>
    [JsonIgnore] public int MaxSearchDepth { get; set; }
    /// <summary>Optional hard cap on stored matches per file. 0 = unlimited (default). Useful for capping pathological files (massive logs, generated dumps) that would otherwise dominate the heap.</summary>
    public int MaxMatchesPerFile { get; set; }
    /// <summary>Whether to skip binary files during content search. Default true.</summary>
    [JsonIgnore] public bool SkipBinary { get; set; } = true;
    /// <summary>When true, the scanner opens cloud-only (online-only) placeholder files
    /// — OneDrive Files On-Demand / Google Drive — hydrating them on demand when a live
    /// provider is present. When false (default), such files are skipped so the scan can
    /// never block on a hydration that may never complete. Default false.</summary>
    public bool SearchOnlineOnlyFiles { get; set; }
    /// <summary>When true (default), files and folders with the Windows Hidden attribute are searched. When false, hidden items are excluded. Persisted; also the default for the per-search Advanced Options toggle.</summary>
    public bool SearchHiddenFiles { get; set; } = true;
    /// <summary>When true, detect ZIP archives by file header and search text files inside them. Default true.</summary>
    [JsonIgnore] public bool SearchInsideArchives { get; set; }
    /// <summary>Semicolon-separated file extensions that are known ZIP-like containers (bypassed from skip-extensions when archive search is on). e.g. "zip;jar;docx;xlsx".</summary>
    public string ArchiveExtensions { get; set; } = DefaultArchiveExtensions;
    /// <summary>Semicolon-separated file extensions to skip entirely (no binary check, no content read).</summary>
    public string SkipExtensions { get; set; } = DefaultSkipExtensions;
    /// <summary>Semicolon-separated known binary/media/data extensions that are skipped by extension prefilter.</summary>
    public string BinaryExtensions { get; set; } = DefaultBinaryExtensions;
    /// <summary>When true, do not show the non-admin access warning banner on startup.</summary>
    public bool SuppressAdminWarning { get; set; }
    /// <summary>When true, do not show theme/font contrast warnings.</summary>
    public bool SuppressFontContrastWarnings { get; set; }
    /// <summary>UTC time before which theme/font contrast warnings are snoozed.</summary>
    public DateTimeOffset? FontContrastReminderAfterUtc { get; set; }
    /// <summary>When true (default) and the process is not elevated, file listing skips well-known admin-protected paths (System Volume Information, $Recycle.Bin, Windows\System32\config, etc.) to speed up search.</summary>
    public bool ExcludeAdminProtectedPaths { get; set; } = true;
    /// <summary>Semicolon- or newline-separated list of path segments (e.g. <c>\Windows\System32\config</c>) treated as admin-protected. Used only when <see cref="ExcludeAdminProtectedPaths"/> is true and the process is not elevated. Empty falls back to the built-in defaults.</summary>
    public string AdminProtectedPathSegments { get; set; } = DefaultAdminProtectedPathSegments;
    public const string DefaultAdminProtectedPathSegments =
        @"\Windows\System32\config;" +
        @"\Windows\System32\LogFiles\WMI;" +
        @"\Windows\System32\Microsoft\Protect;" +
        @"\Windows\System32\sru;" +
        @"\Windows\CSC;" +
        @"\Windows\Installer;" +
        @"\Windows\ServiceProfiles;" +
        @"\Windows\security;" +
        @"\Windows\Minidump;" +
        @"\Windows\appcompat\Programs\Install;" +
        @"\Windows\PrintService;" +
        @"\Windows\WaaS;" +
        @"\Windows\ModemLogs;" +
        @"\System Volume Information;" +
        @"\$Recycle.Bin;" +
        @"\Recovery;" +
        @"\Config.Msi";
    /// <summary>Whether the first-run experience has been completed (context menu prompt, etc.).</summary>
    public bool HasCompletedFirstRun { get; set; }
    /// <summary>Whether the first file-drawer introductory tooltip has been shown.</summary>
    public bool HasShownFileDrawerIntroTip { get; set; }
    /// <summary>Whether the first expanded-drawer line-number introductory tooltip has been shown.</summary>
    public bool HasShownFileDrawerLineNumberIntroTip { get; set; }
    /// <summary>Whether the first preview-match introductory tooltip has been shown.</summary>
    public bool HasShownPreviewMatchIntroTip { get; set; }
    /// <summary>When true, do not show the "another instance is already running" dialog on startup.</summary>
    public bool SuppressMultiInstanceWarning { get; set; }
    /// <summary>When true (default), automatically limit parallelism to 1 on HDD drives and warn the user. When false, no auto-limit or warning.</summary>
    public bool LimitParallelismOnHdd { get; set; } = true;
    /// <summary>When true, back up the file to .yagubak before saving in the built-in editor. Default true.</summary>
    public bool BackupBeforeSave { get; set; } = true;
    /// <summary>When true, show a brief confirmation overlay after the built-in editor successfully saves. Default true.</summary>
    public bool ShowEditorSavedOverlay { get; set; } = true;
    /// <summary>When true (default), the built-in editor applies syntax coloring based on the file name/extension.</summary>
    public bool EditorSyntaxHighlightingEnabled { get; set; } = true;
    /// <summary>When true (default), Yagu starts in the compact launcher window (single search bar,
    /// no results pane). When false, Yagu starts in the traditional full window with title bar and
    /// results pane visible.</summary>
    public bool StartInLauncherMode { get; set; } = true;
    /// <summary>Migration marker: once true, legacy installs have been split between
    /// <see cref="StartInLauncherMode"/> and <see cref="WindowFocusBehavior"/> (which previously
    /// conflated the two concepts via the deprecated value 3 = Traditional window).</summary>
    public bool StartInLauncherModeMigrated { get; set; }
    /// <summary>What the compact launcher does when it loses focus.
    /// 0 = Minimize to system tray, 1 = Stay open (default), 2 = Always on top.
    /// Value 3 (Traditional window startup) is deprecated — use <see cref="StartInLauncherMode"/>
    /// instead.</summary>
    public int WindowFocusBehavior { get; set; } = 1; // 1 = Stay open (default)
    /// <summary>Migration marker: once true, the user's <see cref="WindowFocusBehavior"/> has been
    /// rebased onto a modern default at least once. Kept for backwards compatibility with installs
    /// migrated by an earlier build; the new migration uses <see cref="StartInLauncherModeMigrated"/>.</summary>
    public bool WindowFocusBehaviorMigratedFromLegacyDefault { get; set; }
    /// <summary>When true (default), closing the window docks to system tray instead of exiting.</summary>
    public bool CloseToTray { get; set; } = true;
    /// <summary>Whether the user has been informed that closing docks to the system tray.</summary>
    public bool HasShownCloseToTrayNotification { get; set; }
    /// <summary>When true, maximize the window on startup. Default false.</summary>
    public bool MaximizeOnStartup { get; set; }
    /// <summary>Legacy Advanced Options width setting. Retained for settings-file compatibility; the drawer now always uses the query-box width.</summary>
    public int AdvancedOptionsCollapsedWidthModeIndex { get; set; }
    /// <summary>Optional default working directory for the embedded terminal. Empty uses the Yagu launch directory.</summary>
    public string TerminalDefaultWorkingDirectory { get; set; } = string.Empty;
    /// <summary>When true (default), checking a file header checkbox immediately adds it to the preview pane.</summary>
    public bool FileHeaderCheckAddsToPreview { get; set; } = true;
    /// <summary>When true (default), checking a match line checkbox immediately adds it to the preview pane.</summary>
    public bool MatchLineCheckAddsToPreview { get; set; } = true;
    /// <summary>Number of matches to auto-load when user scrolls to the end of a truncated section. 0 = disabled.</summary>
    public int PreviewAutoLoadMatches { get; set; } = 50;
    /// <summary>Built-in editor: maximum file size in MB. Files larger than this are blocked from opening.</summary>
    public int PreviewEditorMaxSizeMB { get; set; } = 32;
    /// <summary>Built-in editor: maximum total character count. Files with more characters are blocked.</summary>
    public int PreviewEditorMaxTextLength { get; set; } = 20_000_000;
    /// <summary>Built-in editor: maximum single-line length in characters. Files with a line longer than this are blocked.</summary>
    public int PreviewEditorMaxLineLength { get; set; } = 1_000_000;
    /// <summary>Content-search file size ceiling in MB. Files larger than this are skipped when no explicit max-size filter is set. 0 = no ceiling.</summary>
    public int ContentSearchFileSizeMB { get; set; } = 100;
    /// <summary>Absolute ceiling for max results regardless of user settings. Must be > 0.</summary>
    public int MaxResultsCeiling { get; set; } = 50_000;
    /// <summary>Maximum concurrent memory-mapped file views during search. 0 = default (16).</summary>
    public int MmfConcurrencyLimit { get; set; }
    /// <summary>Maximum concurrent native (Rust) scans. 0 = default (min(64, ProcessorCount×2)).</summary>
    public int NativeConcurrencyLimit { get; set; }

    /// <summary>Max matches to render per file section before truncating with overflow. 0 = 500 (default).</summary>
    public int MaxMatchesPerSection { get; set; }

    /// <summary>Max file sections to render per page. 0 = 50 (default).</summary>
    public int PreviewSectionPageSize { get; set; }

    /// <summary>Max file size (MB) for full-file preview mode. 0 = 1024 (1 GB default).</summary>
    public int FullFilePreviewLimitMB { get; set; }

    /// <summary>Max nesting depth when searching inside nested archives. 0 = 5 (default).</summary>
    public int ArchiveMaxNestingDepth { get; set; }

    /// <summary>Max individual entry size (MB) inside archives. 0 = 64 (default).</summary>
    public int ArchiveMaxEntryMB { get; set; }

    public const int MaxRecent = 20; // kept for backward compat; prefer MaxRecentItems
}

public sealed class SettingsService
{
    private readonly string _path;
    private static readonly JsonSerializerOptions JsonOpts = AppSettingsJsonContext.Default.Options;

    public SettingsService() : this(DefaultPath()) { }

    public SettingsService(string path) { _path = path; }

    public static string DefaultPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Yagu", "settings.json");

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_path)) return new AppSettings();
            using var fs = File.OpenRead(_path);
            var settings = JsonSerializer.Deserialize(fs, AppSettingsJsonContext.Default.AppSettings) ?? new AppSettings();
            // Migrate: old default was int.MaxValue which caused unbounded memory growth.
            if (settings.MaxResults > SearchOptions.MaxResultsCeiling)
                settings.MaxResults = SearchOptions.MaxResultsCeiling;
            if (settings.SkipExtensions is null)
                settings.SkipExtensions = AppSettings.DefaultSkipExtensions;
            if (settings.ArchiveExtensions is null)
                settings.ArchiveExtensions = AppSettings.DefaultArchiveExtensions;
            if (IsLegacyDefaultSkipExtensions(settings.SkipExtensions))
                settings.SkipExtensions = AppSettings.DefaultSkipExtensions;
            if (settings.BinaryExtensions is null)
                settings.BinaryExtensions = AppSettings.DefaultBinaryExtensions;
            else if (IsLegacyExpandedBinaryPrefilter(settings.BinaryExtensions))
            {
                settings.BinaryExtensions = AppSettings.DefaultBinaryExtensions;
                settings.SkipExtensions = MergeExtensionLists(settings.SkipExtensions, AppSettings.DefaultSkipExtensions);
            }
            MigrateLegacyPreviewGutterColors(settings);
            MigrateLegacyWindowFocusBehavior(settings);
            NormalizeFilterModeSettings(settings);
            NormalizeThemeSettings(settings);
            NormalizePreviewTextFontSettings(settings);
            NormalizePreviewEditorFontSettings(settings);
            NormalizeResultListMatchTextSettings(settings);
            NormalizePreviewShowMoreSettings(settings);
            settings.LowDiskSpaceWarningPercent = AppSettings.NormalizeLowDiskSpaceWarningPercent(settings.LowDiskSpaceWarningPercent);
            settings.TerminalDefaultWorkingDirectory ??= string.Empty;
            return settings;
        }
        catch (Exception ex) { LogService.Instance.Warning("Settings", $"Failed to load settings from {_path}", ex); return new AppSettings(); }
    }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(_path)) return new AppSettings();
            await using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.Asynchronous);
            var settings = await JsonSerializer.DeserializeAsync(fs, AppSettingsJsonContext.Default.AppSettings, cancellationToken).ConfigureAwait(false) ?? new AppSettings();
            if (settings.MaxResults > SearchOptions.MaxResultsCeiling)
                settings.MaxResults = SearchOptions.MaxResultsCeiling;
            if (settings.SkipExtensions is null)
                settings.SkipExtensions = AppSettings.DefaultSkipExtensions;
            if (settings.ArchiveExtensions is null)
                settings.ArchiveExtensions = AppSettings.DefaultArchiveExtensions;
            if (IsLegacyDefaultSkipExtensions(settings.SkipExtensions))
                settings.SkipExtensions = AppSettings.DefaultSkipExtensions;
            if (settings.BinaryExtensions is null)
                settings.BinaryExtensions = AppSettings.DefaultBinaryExtensions;
            else if (IsLegacyExpandedBinaryPrefilter(settings.BinaryExtensions))
            {
                settings.BinaryExtensions = AppSettings.DefaultBinaryExtensions;
                settings.SkipExtensions = MergeExtensionLists(settings.SkipExtensions, AppSettings.DefaultSkipExtensions);
            }
            MigrateLegacyPreviewGutterColors(settings);
            MigrateLegacyWindowFocusBehavior(settings);
            NormalizeFilterModeSettings(settings);
            NormalizeThemeSettings(settings);
            NormalizePreviewTextFontSettings(settings);
            NormalizePreviewEditorFontSettings(settings);
            NormalizeResultListMatchTextSettings(settings);
            NormalizePreviewShowMoreSettings(settings);
            settings.TerminalDefaultWorkingDirectory ??= string.Empty;
            return settings;
        }
        catch (Exception ex) { LogService.Instance.Warning("Settings", $"Failed to load settings from {_path}", ex); return new AppSettings(); }
    }

    // Earlier Yagu versions stored the startup window mode (launcher vs traditional) and the
    // launcher's focus-loss behavior in a single setting (WindowFocusBehavior 0..3 where 3 meant
    // "Traditional window"). We've since split those into StartInLauncherMode + WindowFocusBehavior
    // (0=MinimizeToTray, 1=StayOpen, 2=AlwaysOnTop). Migrate legacy installs once.
    private static void MigrateLegacyWindowFocusBehavior(AppSettings settings)
    {
        if (settings.StartInLauncherModeMigrated) return;

        switch (settings.WindowFocusBehavior)
        {
            case 3:
                // Legacy "Traditional window" → start in traditional window, stay-open in launcher when invoked manually.
                settings.StartInLauncherMode = false;
                settings.WindowFocusBehavior = 1;
                break;
            case 0 when !settings.WindowFocusBehaviorMigratedFromLegacyDefault:
                // Original Yagu default (Minimize to tray) — flip to the new Stay-open default.
                settings.WindowFocusBehavior = 1;
                settings.StartInLauncherMode = true;
                break;
            default:
                // 1 (StayOpen) and 2 (AlwaysOnTop) are still valid; keep them.
                if (settings.WindowFocusBehavior < 0 || settings.WindowFocusBehavior > 2)
                    settings.WindowFocusBehavior = 1;
                break;
        }

        settings.StartInLauncherModeMigrated = true;
        settings.WindowFocusBehaviorMigratedFromLegacyDefault = true;
    }

    private static void NormalizeFilterModeSettings(AppSettings settings)
    {
        settings.IncludeFilterModeIndex = settings.IncludeFilterModeIndex == 1 ? 1 : 0;
        settings.ExcludeFilterModeIndex = settings.ExcludeFilterModeIndex == 1 ? 1 : 0;
        settings.IncludeGlobs ??= string.Empty;
        settings.ExcludeGlobs ??= AppSettings.DefaultExcludeGlobs;
    }

    private static void NormalizeThemeSettings(AppSettings settings)
    {
        settings.ThemeModeIndex = settings.ThemeModeIndex is >= 0 and <= 2 ? settings.ThemeModeIndex : 0;
    }

    private static void NormalizePreviewTextFontSettings(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.PreviewTextFontFamily))
            settings.PreviewTextFontFamily = AppSettings.DefaultPreviewTextFontFamily;

        settings.PreviewTextFontSize = Math.Clamp(
            settings.PreviewTextFontSize <= 0 ? AppSettings.DefaultPreviewTextFontSize : settings.PreviewTextFontSize,
            6,
            72);
    }

    private static void NormalizePreviewEditorFontSettings(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.PreviewEditorFontFamily))
            settings.PreviewEditorFontFamily = AppSettings.DefaultPreviewEditorFontFamily;

        settings.PreviewEditorFontSize = Math.Clamp(
            settings.PreviewEditorFontSize <= 0 ? AppSettings.DefaultPreviewEditorFontSize : settings.PreviewEditorFontSize,
            6,
            72);
    }

    private static void NormalizeResultListMatchTextSettings(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.ResultListMatchTextFontFamily))
            settings.ResultListMatchTextFontFamily = AppSettings.DefaultResultListMatchTextFontFamily;

        settings.ResultListMatchTextFontSize = Math.Clamp(
            settings.ResultListMatchTextFontSize <= 0 ? AppSettings.DefaultResultListMatchTextFontSize : settings.ResultListMatchTextFontSize,
            6,
            72);

        settings.ResultListMatchHighlightColor = NormalizeArgbHexString(
            settings.ResultListMatchHighlightColor,
            AppSettings.DefaultResultListMatchHighlightColor);
    }

    private static void NormalizePreviewShowMoreSettings(AppSettings settings)
    {
        settings.PreviewShowMoreEllipsisColor = NormalizeArgbHexString(
            settings.PreviewShowMoreEllipsisColor,
            AppSettings.DefaultPreviewShowMoreEllipsisColor);

        settings.PreviewShowMoreEllipsisFontSize = Math.Clamp(
            settings.PreviewShowMoreEllipsisFontSize <= 0 ? AppSettings.DefaultPreviewShowMoreEllipsisFontSize : settings.PreviewShowMoreEllipsisFontSize,
            6,
            72);
    }

    private static string NormalizeArgbHexString(string? value, string fallback)
    {
        if (TryParseArgbHex(value, out var color))
            return FormatArgbHex(color);

        return TryParseArgbHex(fallback, out var fallbackColor)
            ? FormatArgbHex(fallbackColor)
            : AppSettings.DefaultPreviewMatchTextColor;
    }

    private static bool TryParseArgbHex(string? value, out uint color)
    {
        color = 0;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        string hex = value.Trim();
        if (hex.StartsWith('#'))
            hex = hex[1..];

        if (hex.Length == 6 && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        {
            color = 0xFF000000 | rgb;
            return true;
        }

        if (hex.Length == 8 && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var argb))
        {
            color = argb;
            return true;
        }

        return false;
    }

    private static string FormatArgbHex(uint color)
        => "#" + color.ToString("X8", CultureInfo.InvariantCulture);

    private static bool IsLegacyDefaultSkipExtensions(string skipExtensions) =>
        string.Equals(skipExtensions, AppSettings.LegacyDefaultSkipExtensions, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(skipExtensions, AppSettings.LegacyExpandedBinaryPrefilterExtensions, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(skipExtensions, AppSettings.DefaultBinaryExtensions, StringComparison.OrdinalIgnoreCase);

    private static void MigrateLegacyPreviewGutterColors(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.PreviewGutterContextColor)
            || string.Equals(settings.PreviewGutterContextColor, AppSettings.LegacyDefaultPreviewGutterContextColor, StringComparison.OrdinalIgnoreCase))
        {
            settings.PreviewGutterContextColor = AppSettings.DefaultPreviewGutterContextColor;
        }

        if (string.IsNullOrWhiteSpace(settings.PreviewGutterMatchColor)
            || string.Equals(settings.PreviewGutterMatchColor, AppSettings.LegacyDefaultPreviewGutterMatchColor, StringComparison.OrdinalIgnoreCase))
        {
            settings.PreviewGutterMatchColor = AppSettings.DefaultPreviewGutterMatchColor;
        }

        if (string.IsNullOrWhiteSpace(settings.PreviewEditorGutterColor))
            settings.PreviewEditorGutterColor = AppSettings.DefaultPreviewEditorGutterColor;

        settings.PreviewEditorTextColor = NormalizeEditorTextColor(settings.PreviewEditorTextColor);
    }

    // Editor body-text color uses an empty string as an "Auto" sentinel meaning "follow the app/system
    // theme". A non-empty value is normalized to canonical ARGB hex; null/whitespace/invalid collapse to Auto.
    private static string NormalizeEditorTextColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return AppSettings.DefaultPreviewEditorTextColor;

        return TryParseArgbHex(value, out var color)
            ? FormatArgbHex(color)
            : AppSettings.DefaultPreviewEditorTextColor;
    }

    private static bool IsLegacyExpandedBinaryPrefilter(string binaryExtensions) =>
        string.Equals(binaryExtensions, AppSettings.LegacyExpandedBinaryPrefilterExtensions, StringComparison.OrdinalIgnoreCase);

    private static string MergeExtensionLists(string first, string second)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<string>();
        AddExtensions(first, seen, merged);
        AddExtensions(second, seen, merged);
        return string.Join(';', merged);
    }

    private static void AddExtensions(string value, HashSet<string> seen, List<string> target)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        foreach (var extension in value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var normalized = extension.TrimStart('.', '*');
            if (!string.IsNullOrWhiteSpace(normalized) && seen.Add(normalized))
                target.Add(normalized);
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using var fs = File.Create(_path);
            JsonSerializer.Serialize(fs, settings, AppSettingsJsonContext.Default.AppSettings);
        }
        catch (Exception ex) { LogService.Instance.Warning("Settings", $"Failed to save settings to {_path}", ex); }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            await using var fs = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous);
            await JsonSerializer.SerializeAsync(fs, settings, AppSettingsJsonContext.Default.AppSettings, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) { LogService.Instance.Warning("Settings", $"Failed to save settings to {_path}", ex); }
    }

    public static void PushRecent(List<string> list, string value, int max = AppSettings.MaxRecent)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        list.RemoveAll(s => string.Equals(s, value, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, value);
        while (list.Count > max) list.RemoveAt(list.Count - 1);
    }
}
