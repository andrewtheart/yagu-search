using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using QuickGrep.Models;
using QuickGrep.Services;

namespace QuickGrep.ViewModels;

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
    private readonly List<FileGroup> _allResultGroups = [];
    private ResultStore? _resultStore;

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
        SearchAsYouType = _settings.SearchAsYouType;
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
        MemoryLimitMB = _settings.MemoryLimitMB;
        MemoryPressurePercent = _settings.MemoryPressurePercent;
        SkipExtensions = _settings.SkipExtensions;

        Helpers.LineTruncator.TruncatedLength = LineTruncationLength;

        foreach (var d in _settings.RecentDirectories) RecentDirectories.Add(d);
        foreach (var q in _settings.SearchHistory) SearchHistory.Add(q);
    }

    [ObservableProperty] private string _directory = string.Empty;
    [ObservableProperty] private string _query = string.Empty;
    [ObservableProperty] private bool _caseSensitive;
    [ObservableProperty] private bool _useRegex;
    [ObservableProperty] private bool _searchAsYouType;
    [ObservableProperty] private int _contextLines = 3;
    [ObservableProperty] private int _previewContextLines = 20;
    [ObservableProperty] private string _includeGlobs = string.Empty;
    [ObservableProperty] private string _excludeGlobs = "node_modules;bin;obj;.git";
    [ObservableProperty] private long _maxFileSizeBytes = 50L * 1024 * 1024;
    [ObservableProperty] private int _maxResults = 50_000;
    [ObservableProperty] private string _editorCommand = EditorLauncher.DefaultCommand;
    [ObservableProperty] private string _resultFilter = string.Empty;
    [ObservableProperty] private string _fileNameFilter = string.Empty;
    [ObservableProperty] private int _searchModeIndex;
    [ObservableProperty] private int _sortModeIndex;
    [ObservableProperty] private int _sortDirectionIndex;
    [ObservableProperty] private bool _groupByDirectory;
    [ObservableProperty] private int _previewModeIndex; // 0 = Concatenated, 1 = Multi-highlight
    [ObservableProperty] private bool _previewWordWrap;
    [ObservableProperty] private int _logLevelIndex; // 0 = Critical, 1 = Warning, 2 = Info, 3 = Verbose
    [ObservableProperty] private int _fileListerBackendIndex; // 0 = Auto, 1 = SDK, 2 = es.exe, 3 = Managed
    [ObservableProperty] private int _parallelismIndex; // 0 = Auto, 1 = 1 thread, 2 = half cores, 3 = 2x cores
    [ObservableProperty] private int _lineTruncationLength = 500;
    [ObservableProperty] private int _maxRecentItems = 20;
    [ObservableProperty] private bool _globalHotkeyEnabled;
    [ObservableProperty] private int _memoryLimitMB = 4096;
    [ObservableProperty] private int _memoryPressurePercent = 80;
    [ObservableProperty] private string _skipExtensions = "exe;dll;pdb;obj;lib;so;dylib;zip;gz;tar;7z;rar;bz2;xz;iso;cab;msi;nupkg;whl;png;jpg;jpeg;gif;bmp;ico;tif;tiff;webp;svg;mp3;mp4;avi;mov;wmv;flv;mkv;wav;ogg;flac;woff;woff2;ttf;eot;otf;pdf;doc;docx;xls;xlsx;ppt;pptx";

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

    /// <summary>Disk-backed store for evicted results. Null before first search.</summary>
    public ResultStore? ActiveResultStore => _resultStore;

    public ObservableCollection<FileGroup> ResultGroups { get; } = [];
    public ObservableCollection<string> RecentDirectories { get; } = [];
    public ObservableCollection<string> SearchHistory { get; } = [];

    public bool HasResults => ResultGroups.Count > 0;
    public bool ShowEmptyState => !IsSearching && ResultGroups.Count == 0;
    public bool HasFallbackReason => !string.IsNullOrEmpty(FallbackReason);
    public bool HasErrorText => !string.IsNullOrEmpty(ErrorText);
    public int OtherSkippedCount => Math.Max(0, FilesSkipped - AccessDeniedCount);

    partial void OnFallbackReasonChanged(string? value) => OnPropertyChanged(nameof(HasFallbackReason));
    partial void OnErrorTextChanged(string? value) => OnPropertyChanged(nameof(HasErrorText));
    partial void OnFilesSkippedChanged(int value) => OnPropertyChanged(nameof(OtherSkippedCount));
    partial void OnAccessDeniedCountChanged(int value) => OnPropertyChanged(nameof(OtherSkippedCount));
    partial void OnSortModeIndexChanged(int value) => ApplySortAndFilter();
    partial void OnSortDirectionIndexChanged(int value) => ApplySortAndFilter();
    partial void OnGroupByDirectoryChanged(bool value) => ApplySortAndFilter();
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
                SkipBinary = true,
                SkipExtensions = ParseExtensionSet(SkipExtensions),
                MaxDegreeOfParallelism = ResolveParallelism(ParallelismIndex),
                MaxProcessMemoryBytes = MemoryLimitMB > 0 ? (long)MemoryLimitMB * 1024 * 1024 : 0,
                MemoryPressurePercent = MemoryPressurePercent,
            };

            cts = new CancellationTokenSource();
            _cts = cts;
            var token = cts.Token;
            LogService.Instance.Info("Search", $"Starting search #{runId}: query='{Query}', dir='{Directory}', regex={UseRegex}, caseSensitive={CaseSensitive}, mode={SearchModeIndex}");

            var groupIndex = new Dictionary<string, FileGroup>(StringComparer.OrdinalIgnoreCase);
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
                        AddMatch(groupIndex, m.Result);
                        break;
                    case SearchEvent.MatchBatch mb:
                        // Drain the whole batch under a single dispatcher tick. AddMatch is
                        // O(1) per result; doing them in a tight loop keeps allocations and
                        // PropertyChanged churn from each ResultGroups.Add to the absolute
                        // minimum. The list itself was produced by the discovery thread —
                        // we own it now and don't need a copy.
                        for (int i = 0; i < mb.Results.Count; i++)
                            AddMatch(groupIndex, mb.Results[i]);
                        break;
                    case SearchEvent.Progress p:
                        FilesScanned = p.Snapshot.FilesScanned;
                        TotalFiles = p.Snapshot.TotalFiles;
                        MatchesFound = p.Snapshot.MatchesFound;
                        FilesSkipped = p.Snapshot.FilesSkipped;
                        AccessDeniedCount = p.Snapshot.AccessDenied;
                        StatusText = BuildProgressStatus(p.Snapshot);
                        break;
                    case SearchEvent.Error e:
                        ErrorText = e.Message;
                        break;
                    case SearchEvent.MemoryPressure mp:
                        LogService.Instance.Info("ViewModel", $"Memory pressure event received — starting eviction ({_allResultGroups.Count:N0} groups, {MatchesFound:N0} matches)");
                        var evictSw = System.Diagnostics.Stopwatch.StartNew();
                        int evictedCount = EvictAllResults();
                        evictSw.Stop();
                        LogService.Instance.Info("ViewModel", $"Eviction + acknowledge complete in {evictSw.ElapsedMilliseconds}ms (freed {evictedCount:N0})");
                        mp.AcknowledgeEviction(evictedCount);   // Signal workers they can resume
                        Degraded = true;
                        StatusText = $"Searching (memory-saving mode)… {MatchesFound:N0} matches so far";
                        break;
                    case SearchEvent.Completed c:
                        FilesScanned = c.Summary.FilesScanned;
                        TotalFiles = c.Summary.TotalFiles;
                        MatchesFound = c.Summary.TotalMatches;
                        FilesSkipped = c.Summary.FilesSkipped;
                        AccessDeniedCount = c.Summary.SkipReasons?.AccessDenied ?? 0;
                        Truncated = c.Summary.Truncated;
                        Degraded = c.Summary.Degraded;
                        StatusText = BuildCompletionStatus(c.Summary);
                        ApplySortAndFilter();
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (cts is not null && IsCurrentSearch(runId, cts))
            {
                StatusText = "Cancelled";
                LogService.Instance.Info("Search", $"Search #{runId} cancelled by user");
            }
        }
        catch (Exception ex)
        {
            if (cts is not null && IsCurrentSearch(runId, cts))
            {
                ErrorText = $"Search failed: {ex.Message}";
                LogService.Instance.Critical("Search", $"Search #{runId} failed", ex);
            }
        }
        finally
        {
            if (cts is not null && IsCurrentSearch(runId, cts))
            {
                IsSearching = false;
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
        ResultGroups.Clear();
        _allResultGroups.Clear();
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
        Truncated = false;
        Degraded = false;
        IsSearching = true;
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

    private void AddMatch(Dictionary<string, FileGroup> index, SearchResult r)
    {
        // FilePath comes from FileLister and is already a full path on Windows.
        // Avoiding Path.GetFullPath here removes a per-match string allocation +
        // PInvoke that was running on the UI dispatcher.
        var path = r.FilePath;
        bool watched = QuickGrep.Services.FileWatchDiagnostics.IsWatched(path);
        if (watched)
            QuickGrep.Services.FileWatchDiagnostics.Checkpoint(path, "UI-ADDMATCH-ENTER", -1, $"line={r.LineNumber} groups={_allResultGroups.Count}");
        if (!index.TryGetValue(path, out var group))
        {
            bool wasEmpty = _allResultGroups.Count == 0;
            group = new FileGroup(path);
            // Load metadata on a worker thread — the FileInfo syscall on the UI
            // dispatcher was a measurable stall on searches with thousands of
            // distinct files.
            group.BeginLoadMetadata(action => _dispatcher.TryEnqueue(() => action()));
            index[path] = group;
            _allResultGroups.Add(group);
            if (MatchesFilter(group))
            {
                ResultGroups.Add(group);
            }
            if (wasEmpty)
            {
                OnPropertyChanged(nameof(HasResults));
                OnPropertyChanged(nameof(ShowEmptyState));
            }
        }
        group.Add(r);
        if (watched)
            QuickGrep.Services.FileWatchDiagnostics.Checkpoint(path, "UI-ADDMATCH-EXIT", -1, $"groupCount={group.Count} visibleGroups={ResultGroups.Count}");
        // MatchesFound is updated via throttled Progress / Completed events to avoid
        // pumping a PropertyChanged for every single result on huge searches.
    }

    /// <summary>Evict all in-memory results to the disk-backed store to free memory.</summary>
    /// <returns>The number of results actually evicted.</returns>
    private int EvictAllResults()
    {
        if (_resultStore is null) return 0;
        int evicted = 0;
        // Single locked batch + one Flush at the end — was 50k+ Flush() syscalls
        // on the UI thread when each result called ResultStore.Write individually.
        _resultStore.WriteBatch(writeOne =>
        {
            foreach (var group in _allResultGroups)
            {
                foreach (var result in group)
                {
                    if (!result.IsEvicted)
                    {
                        result.EvictWith(writeOne);
                        evicted++;
                    }
                }
            }
        });
        LogService.Instance.Info("ViewModel", $"Evicted {evicted:N0} results to disk ({_resultStore.EvictedCount:N0} total on disk)");
        // GC is now triggered by the worker threads after the eviction signal,
        // keeping the UI thread responsive.
        return evicted;
    }

    /// <summary>Hydrate an evicted result from disk so its full data is available.</summary>
    public void HydrateResult(SearchResult result)
    {
        if (result.IsEvicted && _resultStore is not null)
            result.Hydrate(_resultStore);
    }

    private static string BuildCompletionStatus(SearchSummary s)
    {
        var time = $"{s.Elapsed.TotalSeconds:F2}s";
        if (s.Cancelled)
            return $"Cancelled — {s.TotalMatches:N0} matches in {s.FilesWithMatches:N0} files ({time})";
        if (s.Truncated)
            return $"Truncated at {s.TotalMatches:N0} matches ({time})";
        if (s.Degraded)
            return $"{s.TotalMatches:N0} matches in {s.FilesWithMatches:N0} files — some results paged to disk ({time})";
        return $"{s.TotalMatches:N0} matches in {s.FilesWithMatches:N0} files ({time})";
    }

    partial void OnResultFilterChanged(string value) => ApplySortAndFilter();
    partial void OnFileNameFilterChanged(string value) => ApplySortAndFilter();

    private void ApplySortAndFilter()
    {
        var filtered = _allResultGroups.Where(g => MatchesFilter(g));
        bool asc = SortDirectionIndex == 1;

        IOrderedEnumerable<FileGroup> sorted;

        if (GroupByDirectory)
        {
            var byDir = filtered.OrderBy(g => g.DirectoryName, StringComparer.OrdinalIgnoreCase);
            sorted = SortModeIndex switch
            {
                1 => asc ? byDir.ThenBy(g => g.LastModified) : byDir.ThenByDescending(g => g.LastModified),
                2 => asc ? byDir.ThenBy(g => g.FileSize) : byDir.ThenByDescending(g => g.FileSize),
                _ => asc ? byDir.ThenBy(g => g.MatchCount) : byDir.ThenByDescending(g => g.MatchCount),
            };
        }
        else
        {
            sorted = SortModeIndex switch
            {
                1 => asc ? filtered.OrderBy(g => g.LastModified) : filtered.OrderByDescending(g => g.LastModified),
                2 => asc ? filtered.OrderBy(g => g.FileSize) : filtered.OrderByDescending(g => g.FileSize),
                _ => asc ? filtered.OrderBy(g => g.MatchCount) : filtered.OrderByDescending(g => g.MatchCount),
            };
        }

        var sortedList = sorted.ToList();

        // Set directory group headers
        foreach (var g in _allResultGroups)
            g.GroupHeaderText = null;

        if (GroupByDirectory)
        {
            string? lastDir = null;
            foreach (var g in sortedList)
            {
                if (!string.Equals(g.DirectoryName, lastDir, StringComparison.OrdinalIgnoreCase))
                {
                    g.GroupHeaderText = g.DirectoryName;
                    lastDir = g.DirectoryName;
                }
            }
        }

        ResultGroups.Clear();
        foreach (var g in sortedList)
            ResultGroups.Add(g);

        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    private bool MatchesFilter(FileGroup group)
    {
        // File name filter (left panel)
        if (!string.IsNullOrWhiteSpace(FileNameFilter))
        {
            if (!group.FileName.Contains(FileNameFilter, StringComparison.OrdinalIgnoreCase)
                && !group.FilePath.Contains(FileNameFilter, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        // Content/result filter
        if (string.IsNullOrWhiteSpace(ResultFilter)) return true;
        var filter = ResultFilter;
        if (group.FilePath.Contains(filter, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var r in group)
        {
            if (r.MatchLine.Contains(filter, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
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
        _settings.SearchAsYouType = SearchAsYouType;
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
        _settings.MemoryLimitMB = MemoryLimitMB;
        _settings.MemoryPressurePercent = MemoryPressurePercent;
        _settings.SkipExtensions = SkipExtensions;

        Helpers.LineTruncator.TruncatedLength = LineTruncationLength;

        _settingsService.Save(_settings);
        LogService.Instance.Info("Settings", "Settings persisted");
        LogService.Instance.Flush();
    }

    public List<SearchResult> GetAllSelectedResults()
    {
        var results = new List<SearchResult>();
        foreach (var g in ResultGroups)
        {
            foreach (var r in g)
            {
                if (r.IsSelected) results.Add(r);
            }
        }
        return results;
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
            : s.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static HashSet<string> ParseExtensionSet(string s) =>
        string.IsNullOrWhiteSpace(s)
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(
                s.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                 .Select(e => e.TrimStart('.', '*')),
                StringComparer.OrdinalIgnoreCase);

    private static int ResolveParallelism(int index)
    {
        int cores = Math.Max(1, Environment.ProcessorCount);
        return index switch
        {
            1 => 1,                       // sequential (single-threaded)
            2 => Math.Max(1, cores / 2),  // half cores (HDD-friendly)
            3 => cores * 2,               // 2x cores (I/O heavy)
            _ => 0,                       // 0 = Auto (SearchService chooses the safe cap)
        };
    }
}
