using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Yagu.Models;
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
    private System.Diagnostics.Stopwatch? _searchTimer;
    private long _bytesScanned;

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
        ContextLines = _settings.ContextLines;
        PreviewContextLines = _settings.PreviewContextLines;
        IncludeGlobs = _settings.IncludeGlobs;
        ExcludeGlobs = _settings.ExcludeGlobs;
        MaxFileSizeBytes = _settings.MaxFileSizeBytes;
        MaxResults = _settings.MaxResults <= 0 ? 0 : Math.Min(_settings.MaxResults, SearchOptions.MaxResultsCeiling);
        EditorCommand = _settings.EditorCommand;
        PreviewModeIndex = _settings.PreviewModeIndex;
        PreviewWordWrap = _settings.PreviewWordWrap;
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
        SkipBinary = _settings.SkipBinary;
        SearchInsideArchives = _settings.SearchInsideArchives;
        ArchiveExtensions = _settings.ArchiveExtensions;
        SkipExtensions = _settings.SkipExtensions;
        SuppressAdminWarning = _settings.SuppressAdminWarning;
        HasCompletedFirstRun = _settings.HasCompletedFirstRun;

        Helpers.LineTruncator.TruncatedLength = LineTruncationLength;

        foreach (var d in _settings.RecentDirectories) RecentDirectories.Add(d);
        foreach (var q in _settings.SearchHistory) SearchHistory.Add(q);

        SyncSkipExtensionItems();
        SyncArchiveExtensionItems();
    }

    [ObservableProperty] public partial string Directory { get; set; } = string.Empty;
    [ObservableProperty] public partial string Query { get; set; } = string.Empty;
    [ObservableProperty] public partial bool CaseSensitive { get; set; }
    [ObservableProperty] public partial bool UseRegex { get; set; }
    [ObservableProperty] public partial int ContextLines { get; set; } = 3;
    [ObservableProperty] public partial int PreviewContextLines { get; set; } = 20;
    [ObservableProperty] public partial string IncludeGlobs { get; set; } = string.Empty;
    [ObservableProperty] public partial string ExcludeGlobs { get; set; } = "node_modules;bin;obj;.git";
    [ObservableProperty] public partial long MaxFileSizeBytes { get; set; } = 100L * 1024 * 1024;
    [ObservableProperty] public partial int MaxResults { get; set; }
    [ObservableProperty] public partial string EditorCommand { get; set; } = EditorLauncher.DefaultCommand;
    [ObservableProperty] public partial string ResultFilter { get; set; } = string.Empty;
    [ObservableProperty] public partial string FileNameFilter { get; set; } = string.Empty;
    [ObservableProperty] public partial int SearchModeIndex { get; set; }
    [ObservableProperty] public partial int SortModeIndex { get; set; }
    [ObservableProperty] public partial int SortDirectionIndex { get; set; }
    [ObservableProperty] public partial bool GroupByDirectory { get; set; }
    [ObservableProperty] public partial int PreviewModeIndex { get; set; } = 1; // 0 = Concatenated, 1 = Multi-highlight
    [ObservableProperty] public partial bool PreviewWordWrap { get; set; }
    [ObservableProperty] public partial int FileLogLevelIndex { get; set; } // -1 = None, 0 = Critical, 1 = Warning, 2 = Info, 3 = Verbose
    [ObservableProperty] public partial int ConsoleLogLevelIndex { get; set; } = -1; // -1 = None, 0 = Critical, 1 = Warning, 2 = Info, 3 = Verbose
    [ObservableProperty] public partial int FileListerBackendIndex { get; set; } // 0 = Auto, 1 = SDK, 2 = es.exe, 3 = Managed
    [ObservableProperty] public partial int ParallelismIndex { get; set; } // 0 = Auto, 1 = 1 thread, 2 = half cores, 3 = 2x cores, 4 = all cores
    [ObservableProperty] public partial int LineTruncationLength { get; set; } = 500;
    [ObservableProperty] public partial int MaxRecentItems { get; set; } = 20;
    [ObservableProperty] public partial bool GlobalHotkeyEnabled { get; set; }
    [ObservableProperty] public partial int MemoryLimitMB { get; set; }
    [ObservableProperty] public partial int MemoryPressurePercent { get; set; } = 80;
    [ObservableProperty] public partial int SdkChannelBufferSize { get; set; } = 4096;
    [ObservableProperty] public partial bool SkipBinary { get; set; } = true;
    [ObservableProperty] public partial string SkipExtensions { get; set; } = AppSettings.DefaultSkipExtensions;
    [ObservableProperty] public partial bool SearchInsideArchives { get; set; } = true;
    [ObservableProperty] public partial string ArchiveExtensions { get; set; } = AppSettings.DefaultArchiveExtensions;

    private bool _suppressAdminWarning;
    public bool SuppressAdminWarning
    {
        get => _suppressAdminWarning;
        set => SetProperty(ref _suppressAdminWarning, value);
    }

    [ObservableProperty] public partial bool HasCompletedFirstRun { get; set; }

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
        // Archives
        ["zip"] = "Archives", ["gz"] = "Archives", ["tar"] = "Archives", ["7z"] = "Archives",
        ["rar"] = "Archives", ["bz2"] = "Archives", ["xz"] = "Archives", ["iso"] = "Archives",
        ["cab"] = "Archives", ["msi"] = "Archives", ["nupkg"] = "Archives", ["whl"] = "Archives",
        ["jar"] = "Archives", ["war"] = "Archives", ["ear"] = "Archives", ["apk"] = "Archives",
        ["aab"] = "Archives", ["aar"] = "Archives", ["appx"] = "Archives", ["msix"] = "Archives",
        ["appxbundle"] = "Archives", ["msixbundle"] = "Archives", ["vsix"] = "Archives",
        ["tgz"] = "Archives", ["tbz2"] = "Archives", ["txz"] = "Archives", ["zst"] = "Archives",
        ["zstd"] = "Archives", ["br"] = "Archives", ["lz4"] = "Archives", ["lzma"] = "Archives",
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
        ["pdf"] = "Documents", ["doc"] = "Documents", ["docx"] = "Documents",
        ["xls"] = "Documents", ["xlsx"] = "Documents", ["ppt"] = "Documents", ["pptx"] = "Documents",
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
        _ = PersistSettingsAsync();
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
        _ = PersistSettingsAsync();
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
    [ObservableProperty] public partial int MatchesFound { get; set; }
    [ObservableProperty] public partial int FilesSkipped { get; set; }
    [ObservableProperty] public partial int AccessDeniedCount { get; set; }
    [ObservableProperty] public partial bool Truncated { get; set; }
    [ObservableProperty] public partial bool Degraded { get; set; }
    [ObservableProperty] public partial string FilesPerSecondText { get; set; } = string.Empty;

    /// <summary>Disk-backed store for evicted results. Null before first search.</summary>
    public ResultStore? ActiveResultStore => _resultStore;

    public ObservableCollection<FileGroup> ResultGroups => _resultCollection.VisibleGroups;
    public ObservableCollection<string> RecentDirectories { get; } = [];
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
    partial void OnGroupByDirectoryChanged(bool value) => ApplySortAndFilter();
    partial void OnIncludeGlobsChanged(string value) => ApplySortAndFilter();
    partial void OnExcludeGlobsChanged(string value) => ApplySortAndFilter();
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
                ContextLines = ContextLines,
                SearchMode = (SearchMode)SearchModeIndex,
                IncludeGlobs = SplitCsv(IncludeGlobs),
                ExcludeGlobs = SplitCsv(ExcludeGlobs),
                MaxFileSizeBytes = MaxFileSizeBytes,
                MaxResults = MaxResults,
                SkipBinary = SkipBinary,
                SkipExtensions = ParseExtensionSet(SkipExtensions),
                SearchInsideArchives = SearchInsideArchives,
                ArchiveExtensions = ParseDottedExtensionSet(ArchiveExtensions),
                MaxDegreeOfParallelism = ResolveParallelism(ParallelismIndex),
                MaxProcessMemoryBytes = MemoryLimitMB > 0 ? (long)MemoryLimitMB * 1024 * 1024 : 0,
                MemoryPressurePercent = MemoryPressurePercent,
                SdkChannelBufferSize = SdkChannelBufferSize,
            };

            cts = new CancellationTokenSource();
            _cts = cts;
            var token = cts.Token;
            LogService.Instance.Info("Search", $"Starting search #{runId}: query='{Query}', dir='{Directory}', regex={UseRegex}, caseSensitive={CaseSensitive}, mode={SearchModeIndex}");

            // Yield to the UI message pump periodically so the app stays responsive
            // when the events channel is draining many buffered items synchronously.
            // Without this, the await foreach completes synchronously for thousands of
            // already-buffered items, starving the WinUI message pump and freezing the UI.
            long yieldTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
            long yieldIntervalTicks = System.Diagnostics.Stopwatch.Frequency / 60; // ~16ms (one frame)

            await foreach (var evt in _search.SearchAsync(options, token).ConfigureAwait(true))
            {
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                if (now - yieldTimestamp >= yieldIntervalTicks)
                {
                    await Task.Delay(1, token).ConfigureAwait(true);
                    yieldTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
                }

                if (!IsCurrentSearch(runId, cts))
                {
                    LogService.Instance.Info("Search", $"Ignoring stale search #{runId} event after a newer search started");
                    break;
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
                        AddMatch(m.Result);
                        break;
                    case SearchEvent.MatchBatch mb:
                        // Drain the whole batch under a single dispatcher tick. AddMatch is
                        // O(1) per result; doing them in a tight loop keeps allocations and
                        // PropertyChanged churn from each ResultGroups.Add to the absolute
                        // minimum. The list itself was produced by the discovery thread —
                        // we own it now and don't need a copy.
                        AddMatches(mb.Results);
                        break;
                    case SearchEvent.Progress p:
                        FilesScanned = p.Snapshot.FilesScanned;
                        TotalFiles = p.Snapshot.TotalFiles;
                        MatchesFound = p.Snapshot.MatchesFound;
                        FilesSkipped = p.Snapshot.FilesSkipped;
                        AccessDeniedCount = p.Snapshot.AccessDenied;
                        _bytesScanned = p.Snapshot.BytesScanned;
                        UpdateSkipBreakdown(p.Snapshot.SkipReasons);
                        StatusText = BuildProgressStatus(p.Snapshot);
                        UpdateFilesPerSecond();
                        break;
                    case SearchEvent.Error e:
                        ErrorText = e.Message;
                        break;
                    case SearchEvent.MemoryPressure mp:
                        StatusText = BuildMemoryPressureStatus(mp);
                        LogService.Instance.Info("ViewModel", $"Memory pressure event received — starting eviction ({_resultCollection.AllGroups.Count:N0} groups, {MatchesFound:N0} matches)");
                        var evictSw = System.Diagnostics.Stopwatch.StartNew();
                        int evictedCount = EvictAllResults();
                        evictSw.Stop();
                        LogService.Instance.Info("ViewModel", $"Eviction + acknowledge complete in {evictSw.ElapsedMilliseconds}ms (freed {evictedCount:N0})");
                        mp.AcknowledgeEviction(evictedCount);   // Signal workers they can resume
                        Degraded = true;
                        break;
                    case SearchEvent.MemoryPressureRelieved relieved:
                        Degraded = false;
                        StatusText = BuildCurrentSearchStatus();
                        LogService.Instance.Info("ViewModel", $"Memory pressure relieved — leaving memory-saving mode ({relieved.Diagnostics})");
                        break;
                    case SearchEvent.Completed c:
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

        // Force a gen-2 collection so the previous search's result graph
        // is reclaimed before the new search starts allocating.
        GC.Collect(2, GCCollectionMode.Forced, blocking: false);

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
        IsSearching = true;
        _bytesScanned = 0;
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
        var time = $"{elapsed.TotalSeconds:F2}s";
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
        bool resultAvailabilityChanged;
        if (Degraded && _resultStore is not null)
        {
            resultAvailabilityChanged = false;
            _resultStore.WriteBatch(writeOne => resultAvailabilityChanged = AddMatchCore(result, writeOne));
        }
        else
        {
            resultAvailabilityChanged = AddMatchCore(result, evictedResultWriter: null);
        }

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
        group.BeginLoadMetadata(action => _dispatcher.TryEnqueue(() => action()), _metadataCts.Token);
    }

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

    private static string BuildCompletionStatus(SearchSummary s, TimeSpan elapsed)
    {
        var time = $"{elapsed.TotalSeconds:F2}s";
        var rate = FormatThroughput(s.FilesScanned, s.BytesScanned, elapsed);
        if (s.Cancelled)
            return $"Cancelled — {s.TotalMatches:N0} matches in {s.FilesWithMatches:N0} files ({time}, {rate})";
        if (s.Truncated)
            return $"Truncated at {s.TotalMatches:N0} matches ({time}, {rate})";
        if (s.Degraded)
            return $"{s.TotalMatches:N0} matches in {s.FilesWithMatches:N0} files — some results paged to disk ({time}, {rate})";
        return $"{s.TotalMatches:N0} matches in {s.FilesWithMatches:N0} files ({time}, {rate})";
    }

    private static string FormatThroughput(int filesProcessed, long bytesScanned, TimeSpan elapsed)
    {
        double seconds = Math.Max(elapsed.TotalSeconds, 0.001);
        double mbPerSec = bytesScanned / (1024.0 * 1024.0) / seconds;
        return $"{filesProcessed / seconds:N1} files/sec, {mbPerSec:N0} MB/s";
    }

    private void UpdateFilesPerSecond()
    {
        if (_searchTimer is null || FilesScanned == 0)
        {
            FilesPerSecondText = string.Empty;
            return;
        }
        double seconds = Math.Max(_searchTimer.Elapsed.TotalSeconds, 0.001);
        double mbPerSec = _bytesScanned / (1024.0 * 1024.0) / seconds;
        FilesPerSecondText = $"{FilesScanned / seconds:N0} files/sec  {mbPerSec:N0} MB/s";
    }

    partial void OnResultFilterChanged(string value) => ApplySortAndFilter();
    partial void OnFileNameFilterChanged(string value) => ApplySortAndFilter();

    private void ApplySortAndFilter()
    {
        _resultCollection.ResultFilter = ResultFilter;
        _resultCollection.FileNameFilter = FileNameFilter;
        _resultCollection.IncludeGlobs = IncludeGlobs;
        _resultCollection.ExcludeGlobs = ExcludeGlobs;
        _resultCollection.SortModeIndex = SortModeIndex;
        _resultCollection.SortDirectionIndex = SortDirectionIndex;
        _resultCollection.GroupByDirectory = GroupByDirectory;
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
        _settings.ContextLines = ContextLines;
        _settings.PreviewContextLines = PreviewContextLines;
        _settings.IncludeGlobs = IncludeGlobs;
        _settings.ExcludeGlobs = ExcludeGlobs;
        _settings.MaxFileSizeBytes = MaxFileSizeBytes;
        _settings.MaxResults = MaxResults;
        _settings.EditorCommand = EditorCommand;
        _settings.PreviewModeIndex = PreviewModeIndex;
        _settings.PreviewWordWrap = PreviewWordWrap;
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
        _settings.SkipBinary = SkipBinary;
        _settings.SearchInsideArchives = SearchInsideArchives;
        _settings.ArchiveExtensions = ArchiveExtensions;
        _settings.SkipExtensions = SkipExtensions;
        _settings.SuppressAdminWarning = SuppressAdminWarning;
        _settings.HasCompletedFirstRun = HasCompletedFirstRun;

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
}
