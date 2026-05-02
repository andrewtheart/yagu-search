using System.Text.Json;
using Yagu.Models;

namespace Yagu.Services;

public sealed class AppSettings
{
    public const string LegacyDefaultSkipExtensions = "exe;dll;pdb;obj;lib;so;dylib;zip;gz;tar;7z;rar;bz2;xz;iso;cab;msi;nupkg;whl;png;jpg;jpeg;gif;bmp;ico;tif;tiff;webp;svg;mp3;mp4;avi;mov;wmv;flv;mkv;wav;ogg;flac;woff;woff2;ttf;eot;otf;pdf;doc;docx;xls;xlsx;ppt;pptx";
    public const string DefaultSkipExtensions = LegacyDefaultSkipExtensions + ";com;scr;sys;drv;ocx;cpl;mui;winmd;pri;cat;res;resources;o;a;lo;la;ilk;iobj;ipdb;exp;pyc;pyo;class;dex;wasm;jar;war;ear;apk;aab;aar;appx;msix;appxbundle;msixbundle;vsix;tgz;tbz2;txz;zst;zstd;br;lz4;lzma;bin;dat;db;db3;sqlite;sqlite3;edb;mdb;accdb;ldb;sdf;cache;tmp;bak;etl;evtx;dmp;mdmp;hdmp;hprof;vhd;vhdx;vmdk;pak;usm;bundle;assets;m4a;webm;heic;heif;avif";
    public const string DefaultArchiveExtensions = "zip;jar;war;ear;nupkg;vsix;apk;aab;aar;appx;msix;appxbundle;msixbundle;docx;xlsx;pptx;odt;ods;odp;epub";

    public string? LastDirectory { get; set; }
    public List<string> RecentDirectories { get; set; } = [];
    public List<string> SearchHistory { get; set; } = [];
    public bool CaseSensitive { get; set; }
    public bool UseRegex { get; set; }
    public int ContextLines { get; set; } = 3;
    public int PreviewContextLines { get; set; } = 20;
    public string IncludeGlobs { get; set; } = string.Empty;
    public string ExcludeGlobs { get; set; } = "node_modules;bin;obj;.git";
    public long MaxFileSizeBytes { get; set; } = 100L * 1024 * 1024;
    public int MaxResults { get; set; } = 0;
    public string EditorCommand { get; set; } = EditorLauncher.DefaultCommand;
    public double SplitPanePosition { get; set; } = 0.5;
    public bool GlobalHotkeyEnabled { get; set; }
    public string GlobalHotkeyKey { get; set; } = HotkeyService.DefaultStartKey.ToString();
    public int PreviewModeIndex { get; set; } = 1; // 0 = Concatenated, 1 = Multi-highlight
    public bool PreviewWordWrap { get; set; }
    public int LogLevelIndex { get; set; } // 0 = Critical, 1 = Warning, 2 = Info, 3 = Verbose
    public int FileListerBackendIndex { get; set; } // 0 = Auto, 1 = SDK, 2 = es.exe, 3 = Managed
    public int ParallelismIndex { get; set; } // 0 = Auto, 1 = 1, 2 = half cores, 3 = 2x cores, 4 = all cores
    public int LineTruncationLength { get; set; } = 500;
    public int MaxRecentItems { get; set; } = 20;
    /// <summary>Hard process memory cap in MB. 0 = auto cap based on physical RAM.</summary>
    public int MemoryLimitMB { get; set; } = 4096;
    /// <summary>System-wide memory pressure threshold (0-100). Search evicts cached results and switches to memory-saving mode when total machine memory usage exceeds this %. 0 = disabled.</summary>
    public int MemoryPressurePercent { get; set; } = 80;
    /// <summary>Bounded channel buffer size for the Everything SDK streaming path. Higher values use more memory but can improve throughput.</summary>
    public int SdkChannelBufferSize { get; set; } = 4096;
    /// <summary>Whether to skip binary files during content search. Default true.</summary>
    public bool SkipBinary { get; set; } = true;
    /// <summary>When true, detect ZIP archives by file header and search text files inside them. Default true.</summary>
    public bool SearchInsideArchives { get; set; } = true;
    /// <summary>Semicolon-separated file extensions that are known ZIP-like containers (bypassed from skip-extensions when archive search is on). e.g. "zip;jar;docx;xlsx".</summary>
    public string ArchiveExtensions { get; set; } = DefaultArchiveExtensions;
    /// <summary>Semicolon-separated file extensions to skip entirely (no binary check, no content read). e.g. "exe;dll;zip;png;jpg".</summary>
    public string SkipExtensions { get; set; } = DefaultSkipExtensions;
    /// <summary>When true, do not show the non-admin access warning banner on startup.</summary>
    public bool SuppressAdminWarning { get; set; }
    /// <summary>Whether the first-run experience has been completed (context menu prompt, etc.).</summary>
    public bool HasCompletedFirstRun { get; set; }
    /// <summary>When true, do not show the "another instance is already running" dialog on startup.</summary>
    public bool SuppressMultiInstanceWarning { get; set; }

    public const int MaxRecent = 20; // kept for backward compat; prefer MaxRecentItems
}

public sealed class SettingsService
{
    private readonly string _path;
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

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
            var settings = JsonSerializer.Deserialize<AppSettings>(fs) ?? new AppSettings();
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
            var settings = await JsonSerializer.DeserializeAsync<AppSettings>(fs, cancellationToken: cancellationToken).ConfigureAwait(false) ?? new AppSettings();
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
            JsonSerializer.Serialize(fs, settings, JsonOpts);
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
            await JsonSerializer.SerializeAsync(fs, settings, JsonOpts, cancellationToken).ConfigureAwait(false);
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
