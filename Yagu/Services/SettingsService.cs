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
    public const string DefaultSkipExtensions = "exe;dll;pdb;obj;lib;so;dylib;png;jpg;jpeg;gif;bmp;ico;tif;tiff;webp;svg;mp3;mp4;avi;mov;wmv;flv;mkv;wav;ogg;flac;woff;woff2;ttf;eot;otf;pdf;doc;xls;ppt;com;scr;sys;drv;ocx;cpl;mui;winmd;pri;cat;res;resources;o;a;lo;la;ilk;iobj;ipdb;exp;pyc;pyo;class;dex;wasm;bin;dat;db;db3;sqlite;sqlite3;edb;mdb;accdb;ldb;sdf;cache;tmp;bak;etl;evtx;dmp;mdmp;hdmp;hprof;vhd;vhdx;vmdk;pak;usm;bundle;assets;m4a;webm;heic;heif;avif";
    public const string DefaultArchiveExtensions = "zip;jar;war;ear;nupkg;vsix;apk;aab;aar;appx;msix;appxbundle;msixbundle;docx;xlsx;pptx;odt;ods;odp;epub;whl;gz;tar;7z;rar;bz2;xz;iso;cab;msi;tgz;tbz2;txz;zst;zstd;br;lz4;lzma";
    public const string DefaultExcludeGlobs = "node_modules;bin;obj;.git";

    public string? LastDirectory { get; set; }
    public List<string> RecentDirectories { get; set; } = [];
    public List<string> SearchHistory { get; set; } = [];
    [JsonIgnore] public bool CaseSensitive { get; set; }
    [JsonIgnore] public bool UseRegex { get; set; }
    [JsonIgnore] public bool ExactMatch { get; set; } = true;
    [JsonIgnore] public bool ObeyGitignore { get; set; }
    public bool GitignoreTakesPrecedence { get; set; } = true;
    public int ContextLines { get; set; } = 3;
    public int PreviewContextLines { get; set; } = 10;
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
    public bool PreviewWordWrap { get; set; }
    public int LogLevelIndex { get; set; } = 1; // -1 = None, 0 = Critical, 1 = Warning, 2 = Info, 3 = Verbose (file logging)
    public int ConsoleLogLevelIndex { get; set; } = 1; // -1 = None, 0 = Critical, 1 = Warning, 2 = Info, 3 = Verbose
    public int FileListerBackendIndex { get; set; } // 0 = Auto, 1 = SDK, 2 = es.exe, 3 = Managed
    public int ParallelismIndex { get; set; } // 0 = Auto, 1 = 1, 2 = half cores, 3 = 2x cores, 4 = all cores
    public int LineTruncationLength { get; set; } = 500;
    public int MaxRecentItems { get; set; } = 20;
    /// <summary>Hard process memory cap in MB. 0 = auto cap based on physical RAM.</summary>
    public int MemoryLimitMB { get; set; }
    /// <summary>System-wide memory pressure threshold (0-100). Search evicts cached results and switches to memory-saving mode when total machine memory usage exceeds this %. 0 = disabled.</summary>
    public int MemoryPressurePercent { get; set; } = 75;
    /// <summary>Bounded channel buffer size for the Everything SDK streaming path. Higher values use more memory but can improve throughput.</summary>
    public int SdkChannelBufferSize { get; set; } = 4096;
    /// <summary>Current directory recursion depth. 0 = unlimited. This is intentionally session-only.</summary>
    [JsonIgnore] public int MaxSearchDepth { get; set; }
    /// <summary>Optional hard cap on stored matches per file. 0 = unlimited (default). Useful for capping pathological files (massive logs, generated dumps) that would otherwise dominate the heap.</summary>
    public int MaxMatchesPerFile { get; set; }
    /// <summary>Whether to skip binary files during content search. Default true.</summary>
    [JsonIgnore] public bool SkipBinary { get; set; } = true;
    /// <summary>When true, detect ZIP archives by file header and search text files inside them. Default true.</summary>
    [JsonIgnore] public bool SearchInsideArchives { get; set; }
    /// <summary>Semicolon-separated file extensions that are known ZIP-like containers (bypassed from skip-extensions when archive search is on). e.g. "zip;jar;docx;xlsx".</summary>
    [JsonIgnore] public string ArchiveExtensions { get; set; } = DefaultArchiveExtensions;
    /// <summary>Semicolon-separated file extensions to skip entirely (no binary check, no content read). e.g. "exe;dll;zip;png;jpg".</summary>
    [JsonIgnore] public string SkipExtensions { get; set; } = DefaultSkipExtensions;
    /// <summary>When true, do not show the non-admin access warning banner on startup.</summary>
    public bool SuppressAdminWarning { get; set; }
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
    /// <summary>When true, do not show the "another instance is already running" dialog on startup.</summary>
    public bool SuppressMultiInstanceWarning { get; set; }
    /// <summary>When true (default), automatically limit parallelism to 1 on HDD drives and warn the user. When false, no auto-limit or warning.</summary>
    public bool LimitParallelismOnHdd { get; set; } = true;
    /// <summary>When true, back up the file to .yagubak before saving in the built-in editor. Default true.</summary>
    public bool BackupBeforeSave { get; set; } = true;
    /// <summary>Default window focus behavior in launcher mode. 0 = Minimize to tray, 1 = Stay open, 2 = Always on top, 3 = Traditional window.</summary>
    public int WindowFocusBehavior { get; set; } = 3; // 3 = Traditional window (default)
    /// <summary>When true (default), closing the window docks to system tray instead of exiting.</summary>
    public bool CloseToTray { get; set; } = true;
    /// <summary>Whether the user has been informed that closing docks to the system tray.</summary>
    public bool HasShownCloseToTrayNotification { get; set; }
    /// <summary>Number of matches to auto-load when user scrolls to the end of a truncated section. 0 = disabled.</summary>
    public int PreviewAutoLoadMatches { get; set; } = 50;
    /// <summary>Built-in editor: maximum file size in MB. Files larger than this are blocked from opening.</summary>
    public int PreviewEditorMaxSizeMB { get; set; } = 32;
    /// <summary>Built-in editor: maximum total character count. Files with more characters are blocked.</summary>
    public int PreviewEditorMaxTextLength { get; set; } = 20_000_000;
    /// <summary>Built-in editor: maximum single-line length in characters. Files with a line longer than this are blocked.</summary>
    public int PreviewEditorMaxLineLength { get; set; } = 1_000_000;

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
            if (IsLegacyDefaultSkipExtensions(settings.SkipExtensions))
                settings.SkipExtensions = AppSettings.DefaultSkipExtensions;
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
            if (IsLegacyDefaultSkipExtensions(settings.SkipExtensions))
                settings.SkipExtensions = AppSettings.DefaultSkipExtensions;
            return settings;
        }
        catch (Exception ex) { LogService.Instance.Warning("Settings", $"Failed to load settings from {_path}", ex); return new AppSettings(); }
    }

    private static bool IsLegacyDefaultSkipExtensions(string skipExtensions) =>
        string.Equals(skipExtensions, AppSettings.LegacyDefaultSkipExtensions, StringComparison.OrdinalIgnoreCase);

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
