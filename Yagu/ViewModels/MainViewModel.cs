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

    [ObservableProperty] private bool _isEnabled;

    public SkipExtensionItem(string extension, string category, bool isEnabled)
    {
        Extension = extension;
        Category = category;
        _isEnabled = isEnabled;
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
    private System.Diagnostics.Stopwatch? _searchTimer;

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
        LogLevelIndex = _settings.LogLevelIndex;
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
        SkipExtensions = _settings.SkipExtensions;
        SuppressAdminWarning = _settings.SuppressAdminWarning;

        Helpers.LineTruncator.TruncatedLength = LineTruncationLength;

        foreach (var d in _settings.RecentDirectories) RecentDirectories.Add(d);
        foreach (var q in _settings.SearchHistory) SearchHistory.Add(q);

        SyncSkipExtensionItems();
    }

    [ObservableProperty] private string _directory = string.Empty;
    [ObservableProperty] private string _query = string.Empty;
    [ObservableProperty] private bool _caseSensitive;
    [ObservableProperty] private bool _useRegex;
    [ObservableProperty] private int _contextLines = 3;
    [ObservableProperty] private int _previewContextLines = 20;
    [ObservableProperty] private string _includeGlobs = string.Empty;
    [ObservableProperty] private string _excludeGlobs = "node_modules;bin;obj;.git";
    [ObservableProperty] private long _maxFileSizeBytes = 100L * 1024 * 1024;
    [ObservableProperty] private int _maxResults;
    [ObservableProperty] private string _editorCommand = EditorLauncher.DefaultCommand;
    [ObservableProperty] private string _resultFilter = string.Empty;
    [ObservableProperty] private string _fileNameFilter = string.Empty;
    [ObservableProperty] private int _searchModeIndex;
    [ObservableProperty] private int _sortModeIndex;
    [ObservableProperty] private int _sortDirectionIndex;
    [ObservableProperty] private bool _groupByDirectory;
    [ObservableProperty] private int _previewModeIndex = 1; // 0 = Concatenated, 1 = Multi-highlight
    [ObservableProperty] private bool _previewWordWrap;
    [ObservableProperty] private int _logLevelIndex; // 0 = Critical, 1 = Warning, 2 = Info, 3 = Verbose
    [ObservableProperty] private int _fileListerBackendIndex; // 0 = Auto, 1 = SDK, 2 = es.exe, 3 = Managed
    [ObservableProperty] private int _parallelismIndex; // 0 = Auto, 1 = 1 thread, 2 = half cores, 3 = 2x cores, 4 = all cores
    [ObservableProperty] private int _lineTruncationLength = 500;
    [ObservableProperty] private int _maxRecentItems = 20;
    [ObservableProperty] private bool _globalHotkeyEnabled;
    [ObservableProperty] private int _memoryLimitMB = 4096;
    [ObservableProperty] private int _memoryPressurePercent = 80;
    [ObservableProperty] private int _sdkChannelBufferSize = 4096;
    [ObservableProperty] private bool _skipBinary = true;
    [ObservableProperty] private string _skipExtensions = AppSettings.DefaultSkipExtensions;
    [ObservableProperty] private bool _searchInsideArchives = true;

    private bool _suppressAdminWarning;
    public bool SuppressAdminWarning
    {
        get => _suppressAdminWarning;
        set => SetProperty(ref _suppressAdminWarning, value);
    }

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
        PersistSettings();
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

    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private string? _errorText;
    [ObservableProperty] private string? _fallbackReason;
    [ObservableProperty] private int _filesScanned;
    [ObservableProperty] private int _totalFiles;
    [ObservableProperty] private int _matchesFound;
    [ObservableProperty] private int _filesSkipped;
    [ObservableProperty] private int _accessDeniedCount;
    [ObservableProperty] private bool _truncated;
    [ObservableProperty] private bool _degraded;
    [ObservableProperty] private string _filesPerSecondText = string.Empty;

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
    partial void OnLogLevelIndexChanged(int value)
    {
        LogService.Instance.Level = (LogLevel)value;
        LogService.Instance.Info("Settings", $"Log level changed to {(LogLevel)value}");
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
            PersistSettings();

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
                MaxDegreeOfParallelism = ResolveParallelism(ParallelismIndex),
                MaxProcessMemoryBytes = MemoryLimitMB > 0 ? (long)MemoryLimitMB * 1024 * 1024 : 0,
                MemoryPressurePercent = MemoryPressurePercent,
                SdkChannelBufferSize = SdkChannelBufferSize,
            };

            cts = new CancellationTokenSource();
            _cts = cts;
            var token = cts.Token;
            LogService.Instance.Info("Search", $"Starting search #{runId}: query='{Query}', dir='{Directory}', regex={UseRegex}, caseSensitive={CaseSensitive}, mode={SearchModeIndex}");

            await foreach (var evt in _search.SearchAsync(options, token).ConfigureAwait(true))
            {
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
                        StatusText = BuildCompletionStatus(c.Summary, completedElapsed);
                        ApplySortAndFilter();
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
        _resultCollection.Clear();
        FileMetadataCache.Clear();

        _resultStore?.Dispose();
        _resultStore = new ResultStore();

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
        var rate = FormatFilesPerSecond(FilesScanned, elapsed);
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
        bool resultAvailabilityChanged = false;
        if (Degraded && _resultStore is not null)
        {
            _resultStore.WriteBatch(writeOne =>
            {
                for (int resultIndex = 0; resultIndex < results.Count; resultIndex++)
                    resultAvailabilityChanged |= AddMatchCore(results[resultIndex], writeOne);
            });
        }
        else
        {
            for (int resultIndex = 0; resultIndex < results.Count; resultIndex++)
                resultAvailabilityChanged |= AddMatchCore(results[resultIndex], evictedResultWriter: null);
        }

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
        group.BeginLoadMetadata(action => _dispatcher.TryEnqueue(() => action()));
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
        var rate = FormatFilesPerSecond(s.FilesScanned, elapsed);
        if (s.Cancelled)
            return $"Cancelled — {s.TotalMatches:N0} matches in {s.FilesWithMatches:N0} files ({time}, {rate})";
        if (s.Truncated)
            return $"Truncated at {s.TotalMatches:N0} matches ({time}, {rate})";
        if (s.Degraded)
            return $"{s.TotalMatches:N0} matches in {s.FilesWithMatches:N0} files — some results paged to disk ({time}, {rate})";
        return $"{s.TotalMatches:N0} matches in {s.FilesWithMatches:N0} files ({time}, {rate})";
    }

    private static string FormatFilesPerSecond(int filesProcessed, TimeSpan elapsed)
    {
        double seconds = Math.Max(elapsed.TotalSeconds, 0.001);
        return $"{filesProcessed / seconds:N1} files/sec";
    }

    private void UpdateFilesPerSecond()
    {
        if (_searchTimer is null || FilesScanned == 0)
        {
            FilesPerSecondText = string.Empty;
            return;
        }
        double seconds = Math.Max(_searchTimer.Elapsed.TotalSeconds, 0.001);
        FilesPerSecondText = $"{FilesScanned / seconds:N0} files/sec";
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

    public void PersistSettings()
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
        _settings.LogLevelIndex = LogLevelIndex;
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
        _settings.SkipExtensions = SkipExtensions;
        _settings.SuppressAdminWarning = SuppressAdminWarning;

        Helpers.LineTruncator.TruncatedLength = LineTruncationLength;

        _settingsService.Save(_settings);
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

    private static int ResolveParallelism(int index)
    {
        return SearchOptions.ResolveContentSearchParallelism(index, Environment.ProcessorCount);
    }
}
