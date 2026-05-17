using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Yagu.Models;
using Yagu.Helpers;
using Yagu.Services;

namespace Yagu.ViewModels;

/// <summary>A single extension entry in the skip-extensions dropdown.</summary>
public sealed partial class SkipExtensionItem : ObservableObject
{
    public string Extension { get; }
    public string Category { get; }

    [ObservableProperty] public partial bool IsEnabled { get; set; }

    public SkipExtensionItem(string extension, string category, bool isEnabled)
    {
        Extension = extension;
        Category = category;
        IsEnabled = isEnabled;
    }
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly SearchService _search;
    private readonly SettingsService _settingsService;
    private readonly EditorLauncher _editor;
    private readonly DispatcherQueue _dispatcher;

    private CancellationTokenSource? _cts;
    private readonly SemaphoreSlim _searchLifecycleGate = new(1, 1);
    private int _searchRunId;
    private AppSettings _settings;
    private readonly SearchResultCollection _resultCollection = new();
    private ResultStore? _resultStore;
    private CancellationTokenSource _metadataCts = new();
    private bool _metadataSortFilterRefreshQueued;
    private bool _clearedDefaultExcludeForRegexMode;
    private System.Diagnostics.Stopwatch? _searchTimer;
    private long _bytesScanned;
    private long _prevBytesScanned;
    private int _prevFilesScanned;
    private double _prevSampleTime;
    internal readonly List<(double filesPerSec, double mbPerSec)> ThroughputSamples = new();
    private readonly DirectoryAutoCompleteService _dirAutoComplete = new();
    private CancellationTokenSource? _dirAutoCompleteCts;
    private static int s_postEvictionCompactingGcInFlight;
    private static long s_lastPostEvictionCompactingGcTicks;
    private static readonly TimeSpan PostEvictionCompactingGcCooldown = TimeSpan.FromSeconds(15);

    public MainViewModel() : this(new SearchService(), new SettingsService(), new EditorLauncher(),
                                   DispatcherQueue.GetForCurrentThread())
    { }

    public MainViewModel(SearchService search, SettingsService settingsService, EditorLauncher editor, DispatcherQueue dispatcher)
    {
        _search = search;
        _settingsService = settingsService;
        _editor = editor;
        _dispatcher = dispatcher;

        _settings = _settingsService.Load();
        _editor.Command = _settings.EditorCommand;

        Directory = _settings.LastDirectory ?? string.Empty;
        CaseSensitive = _settings.CaseSensitive;
        UseRegex = _settings.UseRegex;
        ExactMatch = _settings.ExactMatch;
        ContextLines = _settings.ContextLines;
        PreviewContextLines = _settings.PreviewContextLines;
        ObeyGitignore = _settings.ObeyGitignore;
        GitignoreTakesPrecedence = _settings.GitignoreTakesPrecedence;
        IncludeGlobs = _settings.IncludeGlobs;
        ExcludeGlobs = _settings.ExcludeGlobs;
        IncludeFilterModeIndex = _settings.IncludeFilterModeIndex;
        ExcludeFilterModeIndex = _settings.ExcludeFilterModeIndex;
        DefaultMinFileSizeBytes = _settings.DefaultMinFileSizeBytes;
        DefaultMaxFileSizeBytes = _settings.DefaultMaxFileSizeBytes;
        MinFileSizeBytes = DefaultMinFileSizeBytes;
        MaxFileSizeBytes = DefaultMaxFileSizeBytes;
        DefaultCreatedAfterDate = _settings.DefaultCreatedAfterDate;
        DefaultCreatedBeforeDate = _settings.DefaultCreatedBeforeDate;
        DefaultModifiedAfterDate = _settings.DefaultModifiedAfterDate;
        DefaultModifiedBeforeDate = _settings.DefaultModifiedBeforeDate;
        CreatedAfterDate = DefaultCreatedAfterDate;
        CreatedBeforeDate = DefaultCreatedBeforeDate;
        ModifiedAfterDate = DefaultModifiedAfterDate;
        ModifiedBeforeDate = DefaultModifiedBeforeDate;
        MaxResults = _settings.MaxResults <= 0 ? 0 : Math.Min(_settings.MaxResults, SearchOptions.MaxResultsCeiling);
        EditorCommand = _settings.EditorCommand;
        PreviewModeIndex = _settings.PreviewModeIndex;
        PreviewWordWrap = _settings.PreviewWordWrap;
        PreviewAutoLoadMatches = _settings.PreviewAutoLoadMatches;
        FileLogLevelIndex = _settings.LogLevelIndex;
        ConsoleLogLevelIndex = _settings.ConsoleLogLevelIndex;
        FileListerBackendIndex = _settings.FileListerBackendIndex;
        ParallelismIndex = _settings.ParallelismIndex;
        LineTruncationLength = _settings.LineTruncationLength;
        MaxRecentItems = _settings.MaxRecentItems;
        GlobalHotkeyEnabled = _settings.GlobalHotkeyEnabled;
        GlobalHotkeyKey = HotkeyService.TryNormalizeLetter(_settings.GlobalHotkeyKey, out var hotkeyKey)
            ? hotkeyKey.ToString()
            : HotkeyService.DefaultStartKey.ToString();
        MemoryLimitMB = _settings.MemoryLimitMB;
        MemoryPressurePercent = _settings.MemoryPressurePercent;
        SdkChannelBufferSize = _settings.SdkChannelBufferSize;
        MaxMatchesPerFile = _settings.MaxMatchesPerFile;
        ApplyMaxMatchesPerFile(MaxMatchesPerFile);
        SkipBinary = _settings.SkipBinary;
        SearchInsideArchives = _settings.SearchInsideArchives;
        ArchiveExtensions = _settings.ArchiveExtensions;
        SkipExtensions = _settings.SkipExtensions;
        SuppressAdminWarning = _settings.SuppressAdminWarning;
        ExcludeAdminProtectedPaths = _settings.ExcludeAdminProtectedPaths;
        AdminProtectedPathSegments = string.IsNullOrWhiteSpace(_settings.AdminProtectedPathSegments)
            ? AppSettings.DefaultAdminProtectedPathSegments
            : _settings.AdminProtectedPathSegments;
        HasCompletedFirstRun = _settings.HasCompletedFirstRun;
        BackupBeforeSave = _settings.BackupBeforeSave;
        WindowFocusBehavior = _settings.WindowFocusBehavior;
        CloseToTray = _settings.CloseToTray;
        PreviewEditorMaxSizeMB = _settings.PreviewEditorMaxSizeMB;
        PreviewEditorMaxTextLength = _settings.PreviewEditorMaxTextLength;
        PreviewEditorMaxLineLength = _settings.PreviewEditorMaxLineLength;

        Helpers.LineTruncator.TruncatedLength = LineTruncationLength;

        foreach (var d in _settings.RecentDirectories) RecentDirectories.Add(d);
        foreach (var d in _settings.RecentDirectories) DirectorySuggestions.Add(d);
        foreach (var q in _settings.SearchHistory) SearchHistory.Add(q);

        SyncSkipExtensionItems();
        SyncArchiveExtensionItems();
    }

    [ObservableProperty] public partial string Directory { get; set; } = string.Empty;
    [ObservableProperty] public partial string Query { get; set; } = string.Empty;
    [ObservableProperty] public partial bool CaseSensitive { get; set; }
    [ObservableProperty] public partial bool UseRegex { get; set; }
    [ObservableProperty] public partial bool ExactMatch { get; set; } = true;

    public Microsoft.UI.Xaml.Visibility HasQueryText =>
        string.IsNullOrEmpty(Query) ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

    partial void OnQueryChanged(string value) => OnPropertyChanged(nameof(HasQueryText));

    [ObservableProperty] public partial int ContextLines { get; set; } = 3;
    [ObservableProperty] public partial int PreviewContextLines { get; set; } = 20;
    [ObservableProperty] public partial bool ObeyGitignore { get; set; }
    [ObservableProperty] public partial bool GitignoreTakesPrecedence { get; set; } = true;
    [ObservableProperty] public partial string IncludeGlobs { get; set; } = string.Empty;
    [ObservableProperty] public partial string ExcludeGlobs { get; set; } = AppSettings.DefaultExcludeGlobs;
    [ObservableProperty] public partial int IncludeFilterModeIndex { get; set; }
    [ObservableProperty] public partial int ExcludeFilterModeIndex { get; set; }
    [ObservableProperty] public partial long MinFileSizeBytes { get; set; }
    [ObservableProperty] public partial long MaxFileSizeBytes { get; set; }
    [ObservableProperty] public partial long DefaultMinFileSizeBytes { get; set; }
    [ObservableProperty] public partial long DefaultMaxFileSizeBytes { get; set; }
    [ObservableProperty] public partial DateTimeOffset? CreatedAfterDate { get; set; }
    [ObservableProperty] public partial DateTimeOffset? CreatedBeforeDate { get; set; }
    [ObservableProperty] public partial DateTimeOffset? ModifiedAfterDate { get; set; }
    [ObservableProperty] public partial DateTimeOffset? ModifiedBeforeDate { get; set; }
    [ObservableProperty] public partial DateTimeOffset? DefaultCreatedAfterDate { get; set; }
    [ObservableProperty] public partial DateTimeOffset? DefaultCreatedBeforeDate { get; set; }
    [ObservableProperty] public partial DateTimeOffset? DefaultModifiedAfterDate { get; set; }
    [ObservableProperty] public partial DateTimeOffset? DefaultModifiedBeforeDate { get; set; }
    [ObservableProperty] public partial int MaxResults { get; set; }
    [ObservableProperty] public partial string EditorCommand { get; set; } = EditorLauncher.DefaultCommand;
    [ObservableProperty] public partial string FileNameFilter { get; set; } = string.Empty;
    [ObservableProperty] public partial int SearchModeIndex { get; set; }
    [ObservableProperty] public partial int SortModeIndex { get; set; }
    [ObservableProperty] public partial int SortDirectionIndex { get; set; }
    [ObservableProperty] public partial int GroupModeIndex { get; set; }
    [ObservableProperty] public partial int GroupSortDirectionIndex { get; set; }
    [ObservableProperty] public partial int DateRangeFilterIndex { get; set; }

    public GroupMode GroupMode => (GroupMode)GroupModeIndex;
    public FilterPatternMode IncludeFilterMode => IncludeFilterModeIndex == 1 ? FilterPatternMode.Regex : FilterPatternMode.GlobPath;
    public FilterPatternMode ExcludeFilterMode => ExcludeFilterModeIndex == 1 ? FilterPatternMode.Regex : FilterPatternMode.GlobPath;
    public string IncludeFilterPlaceholder => IncludeFilterMode == FilterPatternMode.Regex
        ? @"e.g. \.(cs|xaml)$"
        : "e.g. ts,js,py or *.cs";
    public string ExcludeFilterPlaceholder => ExcludeFilterMode == FilterPatternMode.Regex
        ? @"e.g. (^|/)node_modules/|\.min\.js$"
        : "e.g. node_modules;bin;obj";
    public string GroupModeLabel => GroupMode switch
    {
        GroupMode.None => "None",
        GroupMode.Folder => "Folder",
        GroupMode.DateRangeModified => "Date range (Modified)",
        GroupMode.DateRangeCreated => "Date range (Created)",
        GroupMode.DateRangeModifiedCreated => "Date range (Modified + Created)",
        GroupMode.Extension => "Extension",
        GroupMode.FileSize => "File size",
        _ => "None",
    };
    public string GroupSortDirectionLabel => GroupMode switch
    {
        GroupMode.FileSize => GroupSortDirectionIndex == 0 ? "Small to large" : "Large to small",
        GroupMode.DateRangeModified or GroupMode.DateRangeCreated or GroupMode.DateRangeModifiedCreated =>
            GroupSortDirectionIndex == 0 ? "Recent first" : "Older first",
        _ => GroupSortDirectionIndex == 0 ? "A-Z" : "Z-A",
    };
    public DateRangeFilter DateRangeFilter => (DateRangeFilter)DateRangeFilterIndex;
    public string DateRangeFilterLabel => DateRangeFilter switch
    {
        DateRangeFilter.None => "Any date",
        DateRangeFilter.PastDay => "Last day",
        DateRangeFilter.PastWeek => "Last week",
        DateRangeFilter.PastTwoWeeks => "Last 2 weeks",
        DateRangeFilter.PastMonth => "Last month",
        DateRangeFilter.PastThreeMonths => "Last 3 months",
        DateRangeFilter.PastSixMonths => "Last 6 months",
        DateRangeFilter.PastNineMonths => "Last 9 months",
        DateRangeFilter.PastYear => "Last year",
        DateRangeFilter.PastTwoYears => "Last 2 years",
        DateRangeFilter.PastThreeYears => "Last 3 years",
        DateRangeFilter.PastFiveYears => "Last 5 years",
        _ => "Any date",
    };
    [ObservableProperty] public partial int PreviewModeIndex { get; set; } = 1; // 0 = Concatenated, 1 = Multi-highlight
    [ObservableProperty] public partial bool PreviewWordWrap { get; set; }
    [ObservableProperty] public partial int PreviewAutoLoadMatches { get; set; } = 50;
    [ObservableProperty] public partial int FileLogLevelIndex { get; set; } = 1; // -1 = None, 0 = Critical, 1 = Warning, 2 = Info, 3 = Verbose
    [ObservableProperty] public partial int ConsoleLogLevelIndex { get; set; } = 1; // -1 = None, 0 = Critical, 1 = Warning, 2 = Info, 3 = Verbose
    [ObservableProperty] public partial int FileListerBackendIndex { get; set; } // 0 = Auto, 1 = SDK, 2 = es.exe, 3 = Managed
    [ObservableProperty] public partial int ParallelismIndex { get; set; } // 0 = Auto, 1 = 1 thread, 2 = half cores, 3 = 2x cores, 4 = all cores
    [ObservableProperty] public partial int LineTruncationLength { get; set; } = 500;
    [ObservableProperty] public partial int MaxRecentItems { get; set; } = 20;
    [ObservableProperty] public partial bool GlobalHotkeyEnabled { get; set; }
    [ObservableProperty] public partial int MemoryLimitMB { get; set; }
    [ObservableProperty] public partial int MemoryPressurePercent { get; set; } = 80;
    [ObservableProperty] public partial int SdkChannelBufferSize { get; set; } = 4096;
    [ObservableProperty] public partial int MaxMatchesPerFile { get; set; }

    partial void OnMaxMatchesPerFileChanged(int value) => ApplyMaxMatchesPerFile(value);

    private static void ApplyMaxMatchesPerFile(int value)
    {
        Yagu.Models.FileGroup.MaxMatchesPerGroup = value > 0 ? value : int.MaxValue;
    }
    [ObservableProperty] public partial bool SkipBinary { get; set; } = true;
    [ObservableProperty] public partial string SkipExtensions { get; set; } = AppSettings.DefaultSkipExtensions;
    [ObservableProperty] public partial bool SearchInsideArchives { get; set; }
    [ObservableProperty] public partial string ArchiveExtensions { get; set; } = AppSettings.DefaultArchiveExtensions;
    [ObservableProperty] public partial int PreviewEditorMaxSizeMB { get; set; } = 32;
    [ObservableProperty] public partial int PreviewEditorMaxTextLength { get; set; } = 20_000_000;
    [ObservableProperty] public partial int PreviewEditorMaxLineLength { get; set; } = 1_000_000;

    public double MinFileSizeMB
    {
        get => MinFileSizeBytes == 0 ? double.NaN : MinFileSizeBytes / (1024d * 1024d);
        set
        {
            long bytes = MegabytesToBytes(value);
            if (MinFileSizeBytes != bytes)
                MinFileSizeBytes = bytes;
        }
    }

    public double MaxFileSizeMB
    {
        get => MaxFileSizeBytes == 0 ? double.NaN : MaxFileSizeBytes / (1024d * 1024d);
        set
        {
            long bytes = MegabytesToBytes(value);
            if (MaxFileSizeBytes != bytes)
                MaxFileSizeBytes = bytes;
        }
    }

    public double DefaultMinFileSizeMB
    {
        get => DefaultMinFileSizeBytes / (1024d * 1024d);
        set
        {
            long bytes = MegabytesToBytes(value);
            if (DefaultMinFileSizeBytes != bytes)
                DefaultMinFileSizeBytes = bytes;
        }
    }

    public double DefaultMaxFileSizeMB
    {
        get => DefaultMaxFileSizeBytes / (1024d * 1024d);
        set
        {
            long bytes = MegabytesToBytes(value);
            if (DefaultMaxFileSizeBytes != bytes)
                DefaultMaxFileSizeBytes = bytes;
        }
    }

    private static long MegabytesToBytes(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
            return 0;

        double bytes = value * 1024d * 1024d;
        if (bytes >= long.MaxValue)
            return long.MaxValue;

        return (long)Math.Round(bytes);
    }

    private static bool IsDateRangeInvalid(DateTimeOffset? after, DateTimeOffset? before)
        => after.HasValue && before.HasValue && after.Value.LocalDateTime.Date > before.Value.LocalDateTime.Date;

    private bool _suppressAdminWarning;
    public bool SuppressAdminWarning
    {
        get => _suppressAdminWarning;
        set => SetProperty(ref _suppressAdminWarning, value);
    }

    [ObservableProperty] public partial bool ExcludeAdminProtectedPaths { get; set; } = true;
    [ObservableProperty] public partial string AdminProtectedPathSegments { get; set; } = AppSettings.DefaultAdminProtectedPathSegments;

    [ObservableProperty] public partial bool HasCompletedFirstRun { get; set; }
    [ObservableProperty] public partial bool BackupBeforeSave { get; set; } = true;
    [ObservableProperty] public partial int WindowFocusBehavior { get; set; } // 0 = MinimizeToTray, 1 = StayOpen, 2 = AlwaysOnTop, 3 = FullWindow
    [ObservableProperty] public partial bool CloseToTray { get; set; } = true;

    /// <summary>Observable collection of skip-extension items for the multi-select dropdown.</summary>
    public ObservableCollection<SkipExtensionItem> SkipExtensionItems { get; } = [];

    /// <summary>Summary label for the skip-extensions dropdown button.</summary>
    public string SkipExtensionsSummary
    {
        get
        {
            int enabled = SkipExtensionItems.Count(i => i.IsEnabled);
            int total = SkipExtensionItems.Count;
            return total == 0 ? "Skip: none" : $"Skip: {enabled}/{total} ext";
        }
    }

    private static readonly Dictionary<string, string> ExtensionCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        // Binaries / Build
        ["exe"] = "Binaries", ["dll"] = "Binaries", ["pdb"] = "Binaries", ["obj"] = "Binaries",
        ["lib"] = "Binaries", ["so"] = "Binaries", ["dylib"] = "Binaries",
        ["com"] = "Binaries", ["scr"] = "Binaries", ["sys"] = "Binaries", ["drv"] = "Binaries",
        ["ocx"] = "Binaries", ["cpl"] = "Binaries", ["mui"] = "Binaries", ["winmd"] = "Binaries",
        ["pri"] = "Binaries", ["cat"] = "Binaries", ["res"] = "Binaries", ["resources"] = "Binaries",
        ["o"] = "Binaries", ["a"] = "Binaries", ["lo"] = "Binaries", ["la"] = "Binaries",
        ["ilk"] = "Binaries", ["iobj"] = "Binaries", ["ipdb"] = "Binaries", ["exp"] = "Binaries",
        ["pyc"] = "Binaries", ["pyo"] = "Binaries", ["class"] = "Binaries", ["dex"] = "Binaries",
        ["wasm"] = "Binaries",
        // Data / Dumps
        ["bin"] = "Data", ["dat"] = "Data", ["db"] = "Data", ["db3"] = "Data",
        ["sqlite"] = "Data", ["sqlite3"] = "Data", ["edb"] = "Data", ["mdb"] = "Data",
        ["accdb"] = "Data", ["ldb"] = "Data", ["sdf"] = "Data", ["cache"] = "Data",
        ["tmp"] = "Data", ["bak"] = "Data", ["etl"] = "Data", ["evtx"] = "Data",
        ["dmp"] = "Data", ["mdmp"] = "Data", ["hdmp"] = "Data", ["hprof"] = "Data",
        ["vhd"] = "Data", ["vhdx"] = "Data", ["vmdk"] = "Data", ["pak"] = "Data",
        ["usm"] = "Data", ["bundle"] = "Data", ["assets"] = "Data",
        // Images
        ["png"] = "Images", ["jpg"] = "Images", ["jpeg"] = "Images", ["gif"] = "Images",
        ["bmp"] = "Images", ["ico"] = "Images", ["tif"] = "Images", ["tiff"] = "Images",
        ["webp"] = "Images", ["svg"] = "Images", ["heic"] = "Images", ["heif"] = "Images",
        ["avif"] = "Images",
        // Audio / Video
        ["mp3"] = "Media", ["mp4"] = "Media", ["avi"] = "Media", ["mov"] = "Media",
        ["wmv"] = "Media", ["flv"] = "Media", ["mkv"] = "Media", ["wav"] = "Media",
        ["ogg"] = "Media", ["flac"] = "Media", ["m4a"] = "Media", ["webm"] = "Media",
        // Fonts
        ["woff"] = "Fonts", ["woff2"] = "Fonts", ["ttf"] = "Fonts", ["eot"] = "Fonts", ["otf"] = "Fonts",
        // Documents
        ["pdf"] = "Documents", ["doc"] = "Documents",
        ["xls"] = "Documents", ["ppt"] = "Documents",
    };

    private static string CategorizeExtension(string ext) =>
        ExtensionCategories.TryGetValue(ext, out var cat) ? cat : "Other";

    private bool _suppressSkipExtensionSync;
    private bool _updatingSkipExtensionsFromItems;

    partial void OnSkipExtensionsChanged(string value)
    {
        if (_suppressSkipExtensionSync || _updatingSkipExtensionsFromItems) return;
        SyncSkipExtensionItems();
    }

    /// <summary>Rebuild the <see cref="SkipExtensionItems"/> collection from the current <see cref="SkipExtensions"/> string.</summary>
    public void SyncSkipExtensionItems()
    {
        _suppressSkipExtensionSync = true;
        try
        {
            var enabled = ParseExtensionSet(SkipExtensions);
            // Gather all known extensions: union of currently enabled + all category keys
            var allExts = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in enabled) allExts.Add(e);
            foreach (var e in ExtensionCategories.Keys) allExts.Add(e);

            SkipExtensionItems.Clear();

            // Group by category, sorted
            var groups = allExts
                .GroupBy(CategorizeExtension)
                .OrderBy(g => g.Key);

            foreach (var group in groups)
            {
                foreach (var ext in group.OrderBy(e => e, StringComparer.OrdinalIgnoreCase))
                {
                    SkipExtensionItems.Add(new SkipExtensionItem(ext, group.Key, enabled.Contains(ext)));
                }
            }
            OnPropertyChanged(nameof(SkipExtensionsSummary));
        }
        finally
        {
            _suppressSkipExtensionSync = false;
        }
    }

    /// <summary>Called when a skip-extension item is toggled. Rebuilds the string and persists.</summary>
    public void OnSkipExtensionToggled()
    {
        if (_suppressSkipExtensionSync) return;
        var enabled = SkipExtensionItems.Where(i => i.IsEnabled).Select(i => i.Extension);
        _updatingSkipExtensionsFromItems = true;
        try
        {
            SkipExtensions = string.Join(';', enabled);
        }
        finally
        {
            _updatingSkipExtensionsFromItems = false;
        }
        OnPropertyChanged(nameof(SkipExtensionsSummary));
    }

    // ── Archive (ZIP-like) extensions dropdown ────────────────────

    /// <summary>Observable collection of archive-extension items for the multi-select dropdown.</summary>
    public ObservableCollection<SkipExtensionItem> ArchiveExtensionItems { get; } = [];

    /// <summary>Summary label for the archive-extensions dropdown button.</summary>
    public string ArchiveExtensionsSummary
    {
        get
        {
            int count = ArchiveExtensionItems.Count;
            return count == 0 ? "Archive ext: none" : $"Archive ext: {count}";
        }
    }

    public Microsoft.UI.Xaml.Visibility ArchiveExtensionsVisibility => SearchInsideArchives
        ? Microsoft.UI.Xaml.Visibility.Visible
        : Microsoft.UI.Xaml.Visibility.Collapsed;

    private static readonly Dictionary<string, string> ArchiveExtensionCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zip"] = "Archives", ["jar"] = "Java", ["war"] = "Java", ["ear"] = "Java",
        ["nupkg"] = ".NET", ["vsix"] = ".NET",
        ["apk"] = "Android", ["aab"] = "Android", ["aar"] = "Android",
        ["appx"] = "Windows", ["msix"] = "Windows", ["appxbundle"] = "Windows", ["msixbundle"] = "Windows",
        ["docx"] = "Office", ["xlsx"] = "Office", ["pptx"] = "Office",
        ["odt"] = "OpenDoc", ["ods"] = "OpenDoc", ["odp"] = "OpenDoc",
        ["epub"] = "eBooks",
        ["whl"] = "Python",
        ["gz"] = "Compressed", ["tar"] = "Compressed", ["7z"] = "Compressed",
        ["rar"] = "Compressed", ["bz2"] = "Compressed", ["xz"] = "Compressed",
        ["iso"] = "Disk Images", ["cab"] = "Installers", ["msi"] = "Installers",
        ["tgz"] = "Compressed", ["tbz2"] = "Compressed", ["txz"] = "Compressed",
        ["zst"] = "Compressed", ["zstd"] = "Compressed", ["br"] = "Compressed",
        ["lz4"] = "Compressed", ["lzma"] = "Compressed",
    };

    private static string CategorizeArchiveExtension(string ext) =>
        ArchiveExtensionCategories.TryGetValue(ext, out var cat) ? cat : "Other";

    private bool _suppressArchiveExtensionSync;
    private bool _updatingArchiveExtensionsFromItems;

    partial void OnArchiveExtensionsChanged(string value)
    {
        if (_suppressArchiveExtensionSync || _updatingArchiveExtensionsFromItems) return;
        SyncArchiveExtensionItems();
    }

    /// <summary>Rebuild the <see cref="ArchiveExtensionItems"/> collection from the current <see cref="ArchiveExtensions"/> string.</summary>
    public void SyncArchiveExtensionItems()
    {
        _suppressArchiveExtensionSync = true;
        try
        {
            var enabled = ParseExtensionSet(ArchiveExtensions);

            ArchiveExtensionItems.Clear();

            // Only show extensions that are currently configured — removing one
            // from the settings text box removes it from the dropdown entirely.
            var groups = enabled
                .GroupBy(CategorizeArchiveExtension)
                .OrderBy(g => g.Key);

            foreach (var group in groups)
            {
                foreach (var ext in group.OrderBy(e => e, StringComparer.OrdinalIgnoreCase))
                {
                    ArchiveExtensionItems.Add(new SkipExtensionItem(ext, group.Key, isEnabled: true));
                }
            }
            OnPropertyChanged(nameof(ArchiveExtensionsSummary));
        }
        finally
        {
            _suppressArchiveExtensionSync = false;
        }
    }

    /// <summary>Called when an archive-extension item is toggled. Removes unchecked items and persists.</summary>
    public void OnArchiveExtensionToggled()
    {
        if (_suppressArchiveExtensionSync) return;

        // Remove unchecked items directly instead of rebuilding the whole
        // collection — a full Clear()+rebuild while the ItemsRepeater is
        // materializing templates crashes WinUI.
        for (int i = ArchiveExtensionItems.Count - 1; i >= 0; i--)
        {
            if (!ArchiveExtensionItems[i].IsEnabled)
                ArchiveExtensionItems.RemoveAt(i);
        }

        _updatingArchiveExtensionsFromItems = true;
        try
        {
            ArchiveExtensions = string.Join(';', ArchiveExtensionItems.Select(i => i.Extension));
        }
        finally
        {
            _updatingArchiveExtensionsFromItems = false;
        }
        OnPropertyChanged(nameof(ArchiveExtensionsSummary));
    }

    private string _globalHotkeyKey = HotkeyService.DefaultStartKey.ToString();
    public string GlobalHotkeyKey
    {
        get => _globalHotkeyKey;
        set
        {
            var normalized = HotkeyService.TryNormalizeLetter(value, out var key)
                ? key.ToString()
                : HotkeyService.DefaultStartKey.ToString();
            SetProperty(ref _globalHotkeyKey, normalized);
        }
    }

    [ObservableProperty] public partial bool IsSearching { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; } = string.Empty;
    [ObservableProperty] public partial string? ErrorText { get; set; }
    [ObservableProperty] public partial string? FallbackReason { get; set; }
    [ObservableProperty] public partial int FilesScanned { get; set; }
    [ObservableProperty] public partial int TotalFiles { get; set; }

    public string ProgressTooltip
    {
        get
        {
            if (TotalFiles <= 0) return "Waiting for file list…";
            double pct = (double)FilesScanned / TotalFiles * 100;
            return $"{pct:F1}% complete ({FilesScanned:N0} files out of {TotalFiles:N0} total files)";
        }
    }

    partial void OnFilesScannedChanged(int value) => OnPropertyChanged(nameof(ProgressTooltip));
    partial void OnTotalFilesChanged(int value) => OnPropertyChanged(nameof(ProgressTooltip));
    [ObservableProperty] public partial int MatchesFound { get; set; }
    [ObservableProperty] public partial int FilesSkipped { get; set; }
    [ObservableProperty] public partial int AccessDeniedCount { get; set; }
    [ObservableProperty] public partial bool Truncated { get; set; }
    [ObservableProperty] public partial bool Degraded { get; set; }
    [ObservableProperty] public partial string DegradedNoticeText { get; set; } = string.Empty;
    [ObservableProperty] public partial string FilesPerSecondText { get; set; } = string.Empty;

    /// <summary>Disk-backed store for evicted results. Null before first search.</summary>
    public ResultStore? ActiveResultStore => _resultStore;

    public ObservableCollection<FileGroup> ResultGroups => _resultCollection.VisibleGroups;
    public ObservableCollection<string> RecentDirectories { get; } = [];
    public ObservableCollection<string> DirectorySuggestions { get; } = [];
    public ObservableCollection<string> SearchHistory { get; } = [];

    public bool HasResults => ResultGroups.Count > 0;
    public bool ShowEmptyState => !IsSearching && ResultGroups.Count == 0;
    public bool HasFallbackReason => !string.IsNullOrEmpty(FallbackReason);
    public bool HasErrorText => !string.IsNullOrEmpty(ErrorText);
    public int OtherSkippedCount => Math.Max(0, FilesSkipped - AccessDeniedCount);

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

            var lines = new System.Text.StringBuilder();
            lines.AppendLine("Skipped files breakdown:");
            lines.AppendLine(ExtensionExclusionSkipNote);
            lines.AppendLine();
            if (b.GlobExcluded > 0)   lines.AppendLine($"  🚫  Glob exclusions       {b.GlobExcluded,8:N0}");
            if (b.GitignoreExcluded > 0) lines.AppendLine($"  🙈  .gitignore excluded   {b.GitignoreExcluded,8:N0}");
            if (b.Binary > 0)         lines.AppendLine($"  🔒  Binary files          {b.Binary,8:N0}");
            if (b.ByExtension > 0)    lines.AppendLine($"  📄  Scanner extension skips {b.ByExtension,8:N0}");
            if (b.TooLarge > 0)       lines.AppendLine($"  📏  Too large             {b.TooLarge,8:N0}");
            if (b.AccessDenied > 0)   lines.AppendLine($"  🔐  Access denied         {b.AccessDenied,8:N0}");
            if (b.Directories > 0)    lines.AppendLine($"  📁  Inaccessible dirs     {b.Directories,8:N0}");
            if (b.IOError > 0)        lines.AppendLine($"  ⚠️  I/O errors            {b.IOError,8:N0}");
            if (b.NotFound > 0)       lines.AppendLine($"  ❓  Not found             {b.NotFound,8:N0}");
            if (b.Encoding > 0)       lines.AppendLine($"  🔤  Encoding errors       {b.Encoding,8:N0}");
            if (b.Other > 0)          lines.AppendLine($"  ❔  Other                 {b.Other,8:N0}");

            return lines.ToString().TrimEnd();
        }
    }

    private void UpdateSkipBreakdown(SkipBreakdown? breakdown)
    {
        _lastSkipBreakdown = breakdown;
        OnPropertyChanged(nameof(SkipTooltip));
    }

    partial void OnFallbackReasonChanged(string? value) => OnPropertyChanged(nameof(HasFallbackReason));
    partial void OnErrorTextChanged(string? value) => OnPropertyChanged(nameof(HasErrorText));
    partial void OnFilesSkippedChanged(int value) { OnPropertyChanged(nameof(OtherSkippedCount)); }
    partial void OnAccessDeniedCountChanged(int value) { OnPropertyChanged(nameof(OtherSkippedCount)); }
    partial void OnSortModeIndexChanged(int value) => ApplySortAndFilter();
    partial void OnSortDirectionIndexChanged(int value) => ApplySortAndFilter();
    partial void OnGroupModeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(GroupMode));
        OnPropertyChanged(nameof(GroupModeLabel));
        OnPropertyChanged(nameof(GroupSortDirectionLabel));
        ApplySortAndFilter();
    }
    partial void OnGroupSortDirectionIndexChanged(int value)
    {
        OnPropertyChanged(nameof(GroupSortDirectionLabel));
        ApplySortAndFilter();
    }
    partial void OnDateRangeFilterIndexChanged(int value)
    {
        OnPropertyChanged(nameof(DateRangeFilter));
        OnPropertyChanged(nameof(DateRangeFilterLabel));
        ApplySortAndFilter();
    }
    partial void OnSearchInsideArchivesChanged(bool value) => OnPropertyChanged(nameof(ArchiveExtensionsVisibility));
    partial void OnIncludeGlobsChanged(string value) => ApplySortAndFilter();
    partial void OnExcludeGlobsChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            _clearedDefaultExcludeForRegexMode = false;
        ApplySortAndFilter();
    }
    partial void OnIncludeFilterModeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IncludeFilterMode));
        OnPropertyChanged(nameof(IncludeFilterPlaceholder));
        ApplySortAndFilter();
    }
    partial void OnExcludeFilterModeIndexChanged(int value)
    {
        if (ExcludeFilterMode == FilterPatternMode.Regex && IsDefaultExcludeGlobs(ExcludeGlobs))
        {
            _clearedDefaultExcludeForRegexMode = true;
            ExcludeGlobs = string.Empty;
        }
        else if (ExcludeFilterMode == FilterPatternMode.GlobPath
            && _clearedDefaultExcludeForRegexMode
            && string.IsNullOrWhiteSpace(ExcludeGlobs))
        {
            ExcludeGlobs = AppSettings.DefaultExcludeGlobs;
        }

        OnPropertyChanged(nameof(ExcludeFilterMode));
        OnPropertyChanged(nameof(ExcludeFilterPlaceholder));
        ApplySortAndFilter();
    }
    partial void OnMinFileSizeBytesChanged(long value)
    {
        OnPropertyChanged(nameof(MinFileSizeMB));
    }
    partial void OnMaxFileSizeBytesChanged(long value)
    {
        OnPropertyChanged(nameof(MaxFileSizeMB));
    }
    partial void OnDefaultMinFileSizeBytesChanged(long value) => OnPropertyChanged(nameof(DefaultMinFileSizeMB));
    partial void OnDefaultMaxFileSizeBytesChanged(long value) => OnPropertyChanged(nameof(DefaultMaxFileSizeMB));
    partial void OnFileLogLevelIndexChanged(int value)
    {
        LogService.Instance.FileLevel = (LogLevel)value;
        LogService.Instance.Info("Settings", $"File log level changed to {(LogLevel)value}");
    }
    partial void OnConsoleLogLevelIndexChanged(int value)
    {
        LogService.Instance.ConsoleLevel = (LogLevel)value;
        LogService.Instance.Info("Settings", $"Console log level changed to {(LogLevel)value}");
    }

    partial void OnFileListerBackendIndexChanged(int value)
    {
        var backend = (FileListerBackend)value;
        FileLister.Backend = backend;
        LogService.Instance.Info("Settings", $"FileLister backend set to {backend}");
    }

    [RelayCommand]
    public async Task StartSearchAsync()
    {
        if (string.IsNullOrWhiteSpace(Directory))
        {
            ErrorText = "Choose a directory to search.";
            return;
        }
        if (!System.IO.Directory.Exists(Directory))
        {
            ErrorText = $"Directory does not exist: {Directory}";
            return;
        }
        if (string.IsNullOrEmpty(Query))
        {
            ErrorText = "Enter a search query.";
            return;
        }

        // Validate: skip extensions must not contradict archive extensions when archive search is on.
        if (SearchInsideArchives)
        {
            var skipSet = ParseExtensionSet(SkipExtensions);
            var archiveSet = ParseExtensionSet(ArchiveExtensions);
            var conflicts = skipSet.Intersect(archiveSet, StringComparer.OrdinalIgnoreCase).OrderBy(e => e, StringComparer.OrdinalIgnoreCase).ToList();
            if (conflicts.Count > 0)
            {
                ErrorText = $"Conflicting extensions found in both Skip and Archive lists: {string.Join(", ", conflicts.Select(e => $".{e}"))}. " +
                            "Remove them from the Skip list or the Archive list to proceed.";
                return;
            }
        }

        long effectiveMinFileSizeBytes = MinFileSizeBytes;
        long effectiveMaxFileSizeBytes = MaxFileSizeBytes;
        if (effectiveMinFileSizeBytes > 0 && effectiveMaxFileSizeBytes > 0 && effectiveMinFileSizeBytes > effectiveMaxFileSizeBytes)
        {
            ErrorText = "Minimum file size cannot be larger than maximum file size.";
            return;
        }

        if (IsDateRangeInvalid(CreatedAfterDate, CreatedBeforeDate))
        {
            ErrorText = "Created after date cannot be later than created before date.";
            return;
        }

        if (IsDateRangeInvalid(ModifiedAfterDate, ModifiedBeforeDate))
        {
            ErrorText = "Modified after date cannot be later than modified before date.";
            return;
        }

        int runId = System.Threading.Interlocked.Increment(ref _searchRunId);
        CancelPreviousSearchForNewRun(runId);

        await _searchLifecycleGate.WaitAsync();

        CancellationTokenSource? cts = null;
        try
        {
            if (runId != Volatile.Read(ref _searchRunId))
                return;

            ResetStateForNewSearch();

            SettingsService.PushRecent(_settings.RecentDirectories, Directory, MaxRecentItems);
            SettingsService.PushRecent(_settings.SearchHistory, Query, MaxRecentItems);
            SyncRecent();
            await PersistSettingsAsync();

            var options = new SearchOptions
            {
                Directory = Directory,
                Query = Query,
                CaseSensitive = CaseSensitive,
                UseRegex = UseRegex,
                ExactMatch = ExactMatch,
                ContextLines = ContextLines,
                SearchMode = (SearchMode)SearchModeIndex,
                IncludeGlobs = SplitFilterPatterns(IncludeGlobs, IncludeFilterMode),
                ExcludeGlobs = SplitFilterPatterns(ExcludeGlobs, ExcludeFilterMode),
                IncludeFilterMode = IncludeFilterMode,
                ExcludeFilterMode = ExcludeFilterMode,
                MinFileSizeBytes = effectiveMinFileSizeBytes,
                MaxFileSizeBytes = effectiveMaxFileSizeBytes,
                CreatedAfterDate = CreatedAfterDate,
                CreatedBeforeDate = CreatedBeforeDate,
                ModifiedAfterDate = ModifiedAfterDate,
                ModifiedBeforeDate = ModifiedBeforeDate,
                MaxResults = MaxResults,
                SkipBinary = SkipBinary,
                ObeyGitignore = ObeyGitignore,
                GitignoreTakesPrecedence = GitignoreTakesPrecedence,
                SkipExtensions = ParseExtensionSet(SkipExtensions),
                SearchInsideArchives = SearchInsideArchives,
                ArchiveExtensions = ParseDottedExtensionSet(ArchiveExtensions),
                MaxDegreeOfParallelism = ResolveParallelism(ParallelismIndex),
                MaxProcessMemoryBytes = MemoryLimitMB > 0 ? (long)MemoryLimitMB * 1024 * 1024 : 0,
                MemoryPressurePercent = MemoryPressurePercent,
                SdkChannelBufferSize = SdkChannelBufferSize,
                ExcludeAdminProtectedPaths = ExcludeAdminProtectedPaths,
                AdminProtectedPathSegments = Yagu.Services.FileLister.ParseAdminProtectedSegments(AdminProtectedPathSegments),
            };

            cts = new CancellationTokenSource();
            _cts = cts;
            var token = cts.Token;
            LogService.Instance.Warning("Search", $"Starting search #{runId}: query='{Query}', dir='{Directory}', regex={UseRegex}, caseSensitive={CaseSensitive}, mode={SearchModeIndex}");

            // Yield to the UI message pump periodically so the app stays responsive
            // when the events channel is draining many buffered items synchronously.
            // Without this, the await foreach completes synchronously for thousands of
            // already-buffered items, starving the WinUI message pump and freezing the UI.
            long yieldTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
            long yieldIntervalTicks = System.Diagnostics.Stopwatch.Frequency / 60; // ~16ms (one frame)

            // UI consumer diagnostics
            long uiEventsReceived = 0;
            long uiMatchesReceived = 0;
            long uiYieldCount = 0;
            long uiLastLogTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            const long UiLogIntervalSec = 10;
            var uiEventSw = new System.Diagnostics.Stopwatch();

            await foreach (var evt in _search.SearchAsync(options, token).ConfigureAwait(true))
            {
                uiEventsReceived++;
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                if (now - yieldTimestamp >= yieldIntervalTicks)
                {
                    uiYieldCount++;
                    await Task.Delay(1, token).ConfigureAwait(true);
                    yieldTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
                }

                if (!IsCurrentSearch(runId, cts))
                {
                    LogService.Instance.Warning("Search", $"Ignoring stale search #{runId} event after a newer search started");
                    break;
                }

                // Periodic UI consumer throughput log
                now = System.Diagnostics.Stopwatch.GetTimestamp();
                if ((now - uiLastLogTicks) >= System.Diagnostics.Stopwatch.Frequency * UiLogIntervalSec)
                {
                    uiLastLogTicks = now;
                    LogService.Instance.Warning("UIConsumer",
                        $"Events received={uiEventsReceived:N0}, matchesReceived={uiMatchesReceived:N0}, " +
                        $"groups={_resultCollection.AllGroups.Count:N0}, yields={uiYieldCount:N0}, " +
                        $"degraded={Degraded}, diskEvicted={_resultStore?.EvictedCount ?? 0:N0}");
                }

                switch (evt)
                {
                    case SearchEvent.Fallback f:
                        FallbackReason = f.Reason;
                        break;
                    case SearchEvent.DiscoveryComplete d:
                        TotalFiles = d.TotalFiles;
                        StatusText = $"Searching {d.TotalFiles:N0} files…";
                        break;
                    case SearchEvent.Match m:
                        uiMatchesReceived++;
                        AddMatch(m.Result);
                        break;
                    case SearchEvent.MatchBatch mb:
                        // Drain the whole batch under a single dispatcher tick. AddMatch is
                        // O(1) per result; doing them in a tight loop keeps allocations and
                        // PropertyChanged churn from each ResultGroups.Add to the absolute
                        // minimum. The list itself was produced by the discovery thread —
                        // we own it now and don't need a copy.
                        uiMatchesReceived += mb.Results.Count;
                        uiEventSw.Restart();
                        AddMatches(mb.Results);
                        uiEventSw.Stop();
                        if (uiEventSw.ElapsedMilliseconds > 200)
                        {
                            LogService.Instance.Warning("UIConsumer",
                                $"Slow AddMatches: {mb.Results.Count} results took {uiEventSw.ElapsedMilliseconds}ms " +
                                $"(groups={_resultCollection.AllGroups.Count:N0})");
                        }
                        break;
                    case SearchEvent.Progress p:
                        FilesScanned = p.Snapshot.FilesScanned;
                        TotalFiles = p.Snapshot.TotalFiles;
                        MatchesFound = p.Snapshot.MatchesFound;
                        FilesSkipped = p.Snapshot.FilesSkipped;
                        AccessDeniedCount = p.Snapshot.AccessDenied;
                        _bytesScanned = p.Snapshot.BytesScanned;
                        UpdateSkipBreakdown(p.Snapshot.SkipReasons);
                        UpdateFilesPerSecond();
                        break;
                    case SearchEvent.Error e:
                        ErrorText = e.Message;
                        break;
                    case SearchEvent.MemoryPressure mp:
                        DegradedNoticeText = "Memory pressure — paging results to disk";
                        Degraded = true;
                        LogService.Instance.Warning("ViewModel", $"Memory pressure event received — starting async eviction ({_resultCollection.AllGroups.Count:N0} groups, {MatchesFound:N0} matches)");
                        // Fire-and-forget: the eviction must NOT block this UI event loop,
                        // otherwise Progress/Match events back up while paging is in flight
                        // and the search appears frozen. EvictAll only enqueues into the
                        // ResultStore's background drain channel and returns immediately;
                        // the actual disk writes and post-eviction compacting GC happen
                        // on the threadpool below.
                        _ = Task.Run(() =>
                        {
                            var evictSw = System.Diagnostics.Stopwatch.StartNew();
                            int enqueued = EvictAllResults();
                            evictSw.Stop();
                            LogService.Instance.Warning("ViewModel", $"Eviction enqueued {enqueued:N0} results in {evictSw.ElapsedMilliseconds}ms (drain continues in background)");

                            // Acknowledge immediately so SearchService leaves eviction-in-flight
                            // state and can fire the next pressure cycle if memory is still high.
                            try { mp.AcknowledgeEviction(enqueued); }
                            catch (Exception ex) { LogService.Instance.Warning("ViewModel", "AcknowledgeEviction threw", ex); }

                            // Wait for the background drain to flush bytes to disk before
                            // triggering the compacting GC — otherwise we'd compact while
                            // the match-line/context strings are still rooted by the channel.
                            try { _resultStore?.Drain(); }
                            catch (Exception ex) { LogService.Instance.Warning("ViewModel", "ResultStore drain failed", ex); }

                            if (IsSearching)
                                SearchService.CollectForMemoryPressureIfDue(TimeSpan.FromSeconds(3));
                            else
                                CollectPostEvictionIfDue();
                        });
                        break;
                    case SearchEvent.MemoryPressureRelieved relieved:
                        Degraded = false;
                        DegradedNoticeText = string.Empty;
                        UpdateFilesPerSecond();
                        LogService.Instance.Warning("ViewModel", $"Memory pressure relieved — leaving memory-saving mode ({relieved.Diagnostics})");
                        break;
                    case SearchEvent.Completed c:
                        LogService.Instance.Warning("UIConsumer",
                            $"Search #{runId} completed: uiEvents={uiEventsReceived:N0}, uiMatches={uiMatchesReceived:N0}, " +
                            $"groups={_resultCollection.AllGroups.Count:N0}, yields={uiYieldCount:N0}, " +
                            $"diskEvicted={_resultStore?.EvictedCount ?? 0:N0}");
                        var completedElapsed = StopSearchTimer();
                        FilesScanned = c.Summary.FilesScanned;
                        TotalFiles = c.Summary.TotalFiles;
                        MatchesFound = c.Summary.TotalMatches;
                        FilesSkipped = c.Summary.FilesSkipped;
                        AccessDeniedCount = c.Summary.SkipReasons?.AccessDenied ?? 0;
                        UpdateSkipBreakdown(c.Summary.SkipReasons);
                        Truncated = c.Summary.Truncated;
                        Degraded = c.Summary.Degraded;
                        // Use the actual file-group count so the status bar matches
                        // the clipboard export. Filename-only matches create UI
                        // groups but aren't tracked by the engine's filesWithMatches
                        // counter when content search is also active.
                        var actualFileCount = Math.Max(c.Summary.FilesWithMatches, _resultCollection.AllGroups.Count);
                        var displaySummary = c.Summary with { FilesWithMatches = actualFileCount };
                        StatusText = BuildCompletionStatus(displaySummary, completedElapsed);
                        ApplySortAndFilter();
                        ShowSearchCompleteToast(displaySummary, completedElapsed);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (cts is not null && IsCurrentSearch(runId, cts))
            {
                var cancelledElapsed = StopSearchTimer();
                StatusText = BuildCancelledStatus(cancelledElapsed);
                DegradedNoticeText = string.Empty;
                LogService.Instance.Info("Search", $"Search #{runId} cancelled by user");
            }
        }
        catch (Exception ex)
        {
            if (cts is not null && IsCurrentSearch(runId, cts))
            {
                StopSearchTimer();
                ErrorText = $"Search failed: {ex.Message}";
                LogService.Instance.Critical("Search", $"Search #{runId} failed", ex);
            }
        }
        finally
        {
            if (cts is not null && IsCurrentSearch(runId, cts))
            {
                IsSearching = false;
                FilesPerSecondText = string.Empty;
                OnPropertyChanged(nameof(HasResults));
                OnPropertyChanged(nameof(ShowEmptyState));
                _cts = null;
            }

            cts?.Dispose();
            _searchLifecycleGate.Release();
        }
    }

    private void CancelPreviousSearchForNewRun(int runId)
    {
        var previous = _cts;
        if (previous is null) return;

        try
        {
            StatusText = "Cleaning up previous search…";
            previous.Cancel();
            LogService.Instance.Info("Search", $"Cancelling previous search before starting search #{runId}");
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning("Search", "Previous search cleanup cancellation failed", ex);
        }
    }

    private void ResetStateForNewSearch()
    {
        _cts = null;

        // Cancel pending metadata tasks first so fire-and-forget closures
        // release their FileGroup references promptly.
        _metadataCts.Cancel();
        _metadataCts.Dispose();
        _metadataCts = new CancellationTokenSource();

        _resultCollection.Clear();
        FileMetadataCache.Clear();

        _resultStore?.Dispose();
        _resultStore = new ResultStore();

        // Reclaim the previous search's result graph on the threadpool so the
        // UI thread isn't blocked by a full compacting GC.
        _ = Task.Run(() =>
        {
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
                System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(0, GCCollectionMode.Forced, blocking: true);
        });

        ErrorText = null;
        FallbackReason = null;
        FilesScanned = 0;
        TotalFiles = 0;
        MatchesFound = 0;
        FilesSkipped = 0;
        AccessDeniedCount = 0;
        FilesPerSecondText = string.Empty;
        UpdateSkipBreakdown(null);
        Truncated = false;
        Degraded = false;
        DegradedNoticeText = string.Empty;
        IsSearching = true;
        _bytesScanned = 0;
        _prevBytesScanned = 0;
        _prevFilesScanned = 0;
        _prevSampleTime = 0;
        _prevDisplayTime = 0;
        _prevDisplayFiles = 0;
        _prevDisplayBytes = 0;
        _instantFilesPerSec = 0;
        _instantMbPerSec = 0;
        ThroughputSamples.Clear();
        _searchTimer = System.Diagnostics.Stopwatch.StartNew();
        StatusText = "Searching…";

        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    private bool IsCurrentSearch(int runId, CancellationTokenSource cts) =>
        runId == Volatile.Read(ref _searchRunId) && ReferenceEquals(_cts, cts);

    private string BuildProgressStatus(SearchProgress progress)
    {
        var prefix = Degraded ? "Searching (memory-saving mode)" : "Searching";
        if (progress.TotalFiles > 0)
        {
            return $"{prefix} {progress.FilesScanned:N0}/{progress.TotalFiles:N0} files... {progress.MatchesFound:N0} matches";
        }

        return $"{prefix}... {progress.FilesScanned:N0} files scanned, {progress.MatchesFound:N0} matches";
    }

    private string BuildCurrentSearchStatus()
    {
        var prefix = Degraded ? "Searching (memory-saving mode)" : "Searching";
        if (TotalFiles > 0)
        {
            return $"{prefix} {FilesScanned:N0}/{TotalFiles:N0} files... {MatchesFound:N0} matches";
        }

        return $"{prefix}... {FilesScanned:N0} files scanned, {MatchesFound:N0} matches";
    }

    private static string BuildMemoryPressureStatus(SearchEvent.MemoryPressure memoryPressure)
    {
        return "Memory pressure high; paging Yagu results to disk and continuing in memory-saving mode...";
    }

    private TimeSpan StopSearchTimer()
    {
        var timer = _searchTimer;
        if (timer is null)
            return TimeSpan.Zero;

        timer.Stop();
        _searchTimer = null;
        return timer.Elapsed;
    }

    private string BuildCancelledStatus(TimeSpan elapsed)
    {
        var time = FormatElapsed(elapsed);
        var rate = FormatThroughput(FilesScanned, _bytesScanned, elapsed);
        return $"Cancelled — {MatchesFound:N0} matches, {FilesScanned:N0} files processed ({time}, {rate})";
    }

    [RelayCommand]
    public Task CancelAsync()
    {
        try { _cts?.Cancel(); } catch (Exception ex) { LogService.Instance.Warning("Search", "Cancel failed", ex); }
        return Task.CompletedTask;
    }

    [RelayCommand]
    public void OpenInEditor(SearchResult? result)
    {
        if (result is null) return;
        _editor.Command = EditorCommand;
        _editor.Open(result.FilePath, result.LineNumber);
    }

    [RelayCommand]
    public void OpenContainingFolder(SearchResult? result)
    {
        if (result is null) return;
        EditorLauncher.OpenContainingFolder(result.FilePath);
    }

    [RelayCommand]
    public void OpenTerminalHere(SearchResult? result)
    {
        if (result is null) return;
        EditorLauncher.OpenTerminalAt(result.FilePath);
    }

    [RelayCommand]
    public void CopyFilePath(SearchResult? result)
    {
        if (result is null) return;
        SetClipboard(result.FilePath);
    }

    [RelayCommand]
    public void CopyMatchLine(SearchResult? result)
    {
        if (result is null) return;
        SetClipboard(result.MatchLine);
    }

    private static void SetClipboard(string text)
    {
        try
        {
            var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage();
            pkg.SetText(text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);
        }
        catch (Exception ex) { LogService.Instance.Verbose("Clipboard", "Clipboard unavailable", ex); }
    }

    private void AddMatch(SearchResult result)
    {
        bool resultAvailabilityChanged = AddMatchCore(result, evictedResultWriter: null);

        if (Degraded && _resultStore is not null)
            _resultStore.EnqueueEvict(result);

        if (resultAvailabilityChanged)
            NotifyResultAvailabilityChanged();
    }

    private void AddMatches(IReadOnlyList<SearchResult> results)
    {
        bool resultAvailabilityChanged = _resultCollection.AddRange(
            results,
            InitializeResultGroup,
            evictNewResults: Degraded,
            Degraded ? _resultStore : null);

        if (resultAvailabilityChanged)
            NotifyResultAvailabilityChanged();
    }

    private bool AddMatchCore(
        SearchResult result,
        Func<string, IReadOnlyList<string>, IReadOnlyList<string>, long>? evictedResultWriter)
    {
        // FilePath comes from FileLister and is already a full path on Windows.
        // Avoiding Path.GetFullPath here removes a per-match string allocation +
        // PInvoke that was running on the UI dispatcher.
        var path = result.FilePath;
        bool watched = Yagu.Services.FileWatchDiagnostics.IsWatched(path);
        if (watched)
            Yagu.Services.FileWatchDiagnostics.Checkpoint(path, "UI-ADDMATCH-ENTER", -1, $"line={result.LineNumber} groups={_resultCollection.AllGroups.Count}");

        bool resultAvailabilityChanged = _resultCollection.Add(
            result,
            InitializeResultGroup,
            evictNewResult: Degraded && evictedResultWriter is not null,
            evictedResultWriter);

        if (watched)
            Yagu.Services.FileWatchDiagnostics.Checkpoint(path, "UI-ADDMATCH-EXIT", -1, $"groupCount={_resultCollection.AllGroups.Count} visibleGroups={ResultGroups.Count}");
        // MatchesFound is updated via throttled Progress / Completed events to avoid
        // pumping a PropertyChanged for every single result on huge searches.
        return resultAvailabilityChanged;
    }

    private void InitializeResultGroup(FileGroup group)
    {
        // Load metadata on a worker thread — the FileInfo syscall on the UI
        // dispatcher was a measurable stall on searches with thousands of
        // distinct files.
        group.BeginLoadMetadata(action => _dispatcher.TryEnqueue(() => action()), _metadataCts.Token, OnResultGroupMetadataLoaded);
    }

    private void OnResultGroupMetadataLoaded(FileGroup group)
    {
        if (!IsMetadataSensitiveView)
            return;

        if (_metadataSortFilterRefreshQueued)
            return;

        _metadataSortFilterRefreshQueued = true;
        _dispatcher.TryEnqueue(() =>
        {
            _metadataSortFilterRefreshQueued = false;
            ApplySortAndFilter();
        });
    }

    private bool IsMetadataSensitiveView =>
        DateRangeFilter != DateRangeFilter.None
        || GroupMode is GroupMode.DateRangeModified or GroupMode.DateRangeCreated or GroupMode.DateRangeModifiedCreated
        || GroupMode == GroupMode.FileSize
        || SortModeIndex is 2 or 3;

    private void NotifyResultAvailabilityChanged()
    {
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    /// <summary>Evict all in-memory results to the disk-backed store to free memory.</summary>
    /// <returns>The number of results actually evicted.</returns>
    private int EvictAllResults()
    {
        int evicted = _resultCollection.EvictAll(_resultStore);
        LogService.Instance.Info("ViewModel", $"Evicted {evicted:N0} results to disk ({_resultStore?.EvictedCount ?? 0:N0} total on disk)");
        // GC is now triggered by the worker threads after the eviction signal,
        // keeping the UI thread responsive.
        return evicted;
    }

    private static void CollectPostEvictionIfDue()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        long last = Volatile.Read(ref s_lastPostEvictionCompactingGcTicks);
        if (last != 0)
        {
            double secondsSinceLast = (double)(now - last) / System.Diagnostics.Stopwatch.Frequency;
            if (secondsSinceLast < PostEvictionCompactingGcCooldown.TotalSeconds)
                return;
        }

        if (Interlocked.CompareExchange(ref s_postEvictionCompactingGcInFlight, 1, 0) != 0)
            return;

        var gcStopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
                System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning("ViewModel", "Post-eviction compacting GC failed", ex);
        }
        finally
        {
            gcStopwatch.Stop();
            Volatile.Write(ref s_lastPostEvictionCompactingGcTicks, System.Diagnostics.Stopwatch.GetTimestamp());
            Volatile.Write(ref s_postEvictionCompactingGcInFlight, 0);

            if (gcStopwatch.ElapsedMilliseconds >= 500)
                LogService.Instance.Warning("ViewModel", $"Post-eviction compacting GC took {gcStopwatch.ElapsedMilliseconds:N0}ms");
            else
                LogService.Instance.Info("ViewModel", $"Post-eviction compacting GC took {gcStopwatch.ElapsedMilliseconds:N0}ms");
        }
    }

    /// <summary>
    /// Clear all search results, dispose the disk-backed temp store,
    /// and perform a compacting GC.
    /// </summary>
    public async Task ClearResultsAsync()
    {
        if (IsSearching)
            await CancelAsync();

        _resultCollection.Clear();
        FileMetadataCache.Clear();

        var oldStore = _resultStore;
        _resultStore = null;

        MatchesFound = 0;
        FilesScanned = 0;
        TotalFiles = 0;
        FilesSkipped = 0;
        AccessDeniedCount = 0;
        ErrorText = null;
        FallbackReason = null;
        Truncated = false;
        Degraded = false;
        DegradedNoticeText = string.Empty;
        FilesPerSecondText = string.Empty;
        StatusText = string.Empty;
        ThroughputSamples.Clear();

        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(ShowEmptyState));

        // Dispose the old store (deletes temp file) and GC on the threadpool
        // so the UI stays responsive.
        await Task.Run(() =>
        {
            oldStore?.Dispose();

            System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
                System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        }).ConfigureAwait(true);
    }

    /// <summary>Hydrate an evicted result from disk so its full data is available.</summary>
    public void HydrateResult(SearchResult result)
    {
        if (result.IsEvicted && _resultStore is not null)
        {
            try
            {
                result.Hydrate(_resultStore);
            }
            catch (Exception ex) when (ex is EndOfStreamException or FormatException or InvalidOperationException or ObjectDisposedException)
            {
                LogService.Instance.Warning("ViewModel", $"Could not hydrate result at offset {result.DiskOffset}: {ex.Message}");
            }
        }
    }

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

        System.Text.RegularExpressions.Regex? regex = null;
        string? literal = null;
        StringComparison literalComparison = StringComparison.OrdinalIgnoreCase;

        if (UseRegex)
        {
            var regexOptions = System.Text.RegularExpressions.RegexOptions.Multiline;
            if (!CaseSensitive) regexOptions |= System.Text.RegularExpressions.RegexOptions.IgnoreCase;
            try { regex = new System.Text.RegularExpressions.Regex(query, regexOptions, TimeSpan.FromSeconds(5)); }
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
                    ContextAfter: after));
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
            return $"{s.TotalMatches:N0} matches in {s.FilesWithMatches:N0} files — some results paged to disk ({time}, {rate})";
        return $"{s.TotalMatches:N0} matches in {s.FilesWithMatches:N0} files ({time}, {rate})";
    }

    private static string FormatElapsed(TimeSpan elapsed) =>
        $"{(int)elapsed.TotalHours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2} elapsed";

    private static string FormatThroughput(int filesProcessed, long bytesScanned, TimeSpan elapsed)
    {
        double seconds = Math.Max(elapsed.TotalSeconds, 0.001);
        double mbPerSec = bytesScanned / (1024.0 * 1024.0) / seconds;
        return $"{filesProcessed / seconds:N1} files/sec, {mbPerSec:N0} MB/s";
    }

    private double _instantFilesPerSec;
    private double _instantMbPerSec;
    private double _prevDisplayTime;
    private int _prevDisplayFiles;
    private long _prevDisplayBytes;

    private void UpdateFilesPerSecond()
    {
        if (_searchTimer is null || FilesScanned == 0)
        {
            return;
        }
        double seconds = Math.Max(_searchTimer.Elapsed.TotalSeconds, 0.001);
        int filesWithMatches = _resultCollection.AllGroups.Count;

        // Update instantaneous rate display (~2s window, like Task Manager)
        double displayDt = seconds - _prevDisplayTime;
        if (displayDt >= 2.0)
        {
            int deltaFiles = FilesScanned - _prevDisplayFiles;
            long deltaBytes = _bytesScanned - _prevDisplayBytes;
            _instantFilesPerSec = deltaFiles / displayDt;
            _instantMbPerSec = deltaBytes / (1024.0 * 1024.0) / displayDt;
            _prevDisplayFiles = FilesScanned;
            _prevDisplayBytes = _bytesScanned;
            _prevDisplayTime = seconds;
        }

        StatusText = $"{MatchesFound:N0} matches in {filesWithMatches:N0} files ({FormatElapsed(_searchTimer.Elapsed)}, {_instantFilesPerSec:N1} files/sec, {_instantMbPerSec:N0} MB/s)";

        // Collect incremental sample for sparkline (~0.15s window, rolling 30s)
        double dt = seconds - _prevSampleTime;
        if (dt >= 0.15) // sample ~6-7x per second
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
        _resultCollection.ExcludeGlobs = ExcludeGlobs;
        _resultCollection.IncludeFilterMode = IncludeFilterMode;
        _resultCollection.ExcludeFilterMode = ExcludeFilterMode;
        _resultCollection.SortModeIndex = SortModeIndex;
        _resultCollection.SortDirectionIndex = SortDirectionIndex;
        _resultCollection.GroupMode = GroupMode;
        _resultCollection.GroupSortDirectionIndex = GroupSortDirectionIndex;
        _resultCollection.DateRangeFilter = DateRangeFilter;
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
    }

    public async Task PersistSettingsAsync()
    {
        _settings.LastDirectory = Directory;
        _settings.CaseSensitive = CaseSensitive;
        _settings.UseRegex = UseRegex;
        _settings.ExactMatch = ExactMatch;
        _settings.ContextLines = ContextLines;
        _settings.PreviewContextLines = PreviewContextLines;
        _settings.ObeyGitignore = ObeyGitignore;
        _settings.GitignoreTakesPrecedence = GitignoreTakesPrecedence;
        _settings.IncludeGlobs = IncludeGlobs;
        _settings.ExcludeGlobs = ExcludeGlobs;
        _settings.IncludeFilterModeIndex = IncludeFilterModeIndex;
        _settings.ExcludeFilterModeIndex = ExcludeFilterModeIndex;
        _settings.MinFileSizeBytes = MinFileSizeBytes;
        _settings.MaxFileSizeBytes = MaxFileSizeBytes;
        _settings.CreatedAfterDate = CreatedAfterDate;
        _settings.CreatedBeforeDate = CreatedBeforeDate;
        _settings.ModifiedAfterDate = ModifiedAfterDate;
        _settings.ModifiedBeforeDate = ModifiedBeforeDate;
        _settings.DefaultMinFileSizeBytes = DefaultMinFileSizeBytes;
        _settings.DefaultMaxFileSizeBytes = DefaultMaxFileSizeBytes;
        _settings.DefaultCreatedAfterDate = DefaultCreatedAfterDate;
        _settings.DefaultCreatedBeforeDate = DefaultCreatedBeforeDate;
        _settings.DefaultModifiedAfterDate = DefaultModifiedAfterDate;
        _settings.DefaultModifiedBeforeDate = DefaultModifiedBeforeDate;
        _settings.MaxResults = MaxResults;
        _settings.EditorCommand = EditorCommand;
        _settings.PreviewModeIndex = PreviewModeIndex;
        _settings.PreviewWordWrap = PreviewWordWrap;
        _settings.PreviewAutoLoadMatches = PreviewAutoLoadMatches;
        _settings.LogLevelIndex = FileLogLevelIndex;
        _settings.ConsoleLogLevelIndex = ConsoleLogLevelIndex;
        _settings.FileListerBackendIndex = FileListerBackendIndex;
        _settings.ParallelismIndex = ParallelismIndex;
        _settings.LineTruncationLength = LineTruncationLength;
        _settings.MaxRecentItems = MaxRecentItems;
        _settings.GlobalHotkeyEnabled = GlobalHotkeyEnabled;
        _settings.GlobalHotkeyKey = HotkeyService.TryNormalizeLetter(GlobalHotkeyKey, out var hotkeyKey)
            ? hotkeyKey.ToString()
            : HotkeyService.DefaultStartKey.ToString();
        _settings.MemoryLimitMB = MemoryLimitMB;
        _settings.MemoryPressurePercent = MemoryPressurePercent;
        _settings.SdkChannelBufferSize = SdkChannelBufferSize;
        _settings.MaxMatchesPerFile = MaxMatchesPerFile;
        _settings.SkipBinary = SkipBinary;
        _settings.SearchInsideArchives = SearchInsideArchives;
        _settings.ArchiveExtensions = ArchiveExtensions;
        _settings.SkipExtensions = SkipExtensions;
        _settings.SuppressAdminWarning = SuppressAdminWarning;
        _settings.ExcludeAdminProtectedPaths = ExcludeAdminProtectedPaths;
        _settings.AdminProtectedPathSegments = AdminProtectedPathSegments;
        _settings.HasCompletedFirstRun = HasCompletedFirstRun;
        _settings.BackupBeforeSave = BackupBeforeSave;
        _settings.WindowFocusBehavior = WindowFocusBehavior;
        _settings.CloseToTray = CloseToTray;
        _settings.PreviewEditorMaxSizeMB = PreviewEditorMaxSizeMB;
        _settings.PreviewEditorMaxTextLength = PreviewEditorMaxTextLength;
        _settings.PreviewEditorMaxLineLength = PreviewEditorMaxLineLength;

        Helpers.LineTruncator.TruncatedLength = LineTruncationLength;

        await _settingsService.SaveAsync(_settings).ConfigureAwait(false);
        LogService.Instance.Info("Settings", "Settings persisted");
        LogService.Instance.Flush();
    }

    public List<SearchResult> GetAllSelectedResults()
    {
        return _resultCollection.GetAllSelectedResults();
    }

    public void SetDirectoryFromArgs(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return;
        if (!System.IO.Directory.Exists(dir))
        {
            ErrorText = $"--dir path does not exist or is not a directory: {dir}";
            return;
        }
        Directory = dir;
    }

    private static List<string> SplitCsv(string s) =>
        string.IsNullOrWhiteSpace(s)
            ? []
            : [.. s.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private static IReadOnlyList<string> SplitFilterPatterns(string s, FilterPatternMode mode) =>
        string.IsNullOrWhiteSpace(s)
            ? []
            : mode == FilterPatternMode.Regex
                ? [s.Trim()]
                : SplitCsv(s);

    private static bool IsDefaultExcludeGlobs(string value) =>
        string.Equals(value?.Trim(), AppSettings.DefaultExcludeGlobs, StringComparison.OrdinalIgnoreCase);

    private static HashSet<string> ParseExtensionSet(string s) =>
        string.IsNullOrWhiteSpace(s)
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(
                s.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                 .Select(e => e.TrimStart('.', '*')),
                StringComparer.OrdinalIgnoreCase);

    /// <summary>Parse a semicolon-separated extension string into a set WITH leading dots (e.g. ".zip", ".docx").</summary>
    private static HashSet<string> ParseDottedExtensionSet(string s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return new HashSet<string>(
            s.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
             .Select(e => e.TrimStart('.', '*'))
             .Select(e => "." + e),
            StringComparer.OrdinalIgnoreCase);
    }

    private static int ResolveParallelism(int index)
    {
        return SearchOptions.ResolveContentSearchParallelism(index, Environment.ProcessorCount);
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
                      <text>{System.Security.SecurityElement.Escape(title)}</text>
                      <text>{System.Security.SecurityElement.Escape(body)}</text>
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

    /// <summary>
    /// Called when the directory text changes. Debounces and fetches subdirectory suggestions.
    /// </summary>
    internal async Task UpdateDirectorySuggestionsAsync(string text)
    {
        // Cancel any previous in-flight lookup.
        _dirAutoCompleteCts?.Cancel();
        _dirAutoCompleteCts = new CancellationTokenSource();
        var ct = _dirAutoCompleteCts.Token;

        try
        {
            // Debounce: wait 250ms before querying.
            await Task.Delay(250, ct).ConfigureAwait(false);

            var suggestions = await _dirAutoComplete.GetSuggestionsAsync(text, ct).ConfigureAwait(false);

            // If no subdirectory suggestions, show recent directories as fallback.
            if (suggestions.Count == 0 && string.IsNullOrWhiteSpace(text))
            {
                _dispatcher.TryEnqueue(() =>
                {
                    DirectorySuggestions.Clear();
                    foreach (var d in _settings.RecentDirectories)
                        DirectorySuggestions.Add(d);
                });
                return;
            }

            _dispatcher.TryEnqueue(() =>
            {
                DirectorySuggestions.Clear();
                foreach (var s in suggestions)
                    DirectorySuggestions.Add(s);
            });
        }
        catch (OperationCanceledException)
        {
            // Expected when user keeps typing.
        }
    }
}
