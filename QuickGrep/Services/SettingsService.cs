using System.Text.Json;
using QuickGrep.Models;

namespace QuickGrep.Services;

public sealed class AppSettings
{
    public string? LastDirectory { get; set; }
    public List<string> RecentDirectories { get; set; } = [];
    public List<string> SearchHistory { get; set; } = [];
    public bool CaseSensitive { get; set; }
    public bool UseRegex { get; set; }
    public bool SearchAsYouType { get; set; }
    public int ContextLines { get; set; } = 3;
    public int PreviewContextLines { get; set; } = 20;
    public string IncludeGlobs { get; set; } = string.Empty;
    public string ExcludeGlobs { get; set; } = "node_modules;bin;obj;.git";
    public long MaxFileSizeBytes { get; set; } = 50L * 1024 * 1024;
    public int MaxResults { get; set; } = 100_000;
    public string EditorCommand { get; set; } = EditorLauncher.DefaultCommand;
    public double SplitPanePosition { get; set; } = 0.5;
    public bool GlobalHotkeyEnabled { get; set; }
    public string GlobalHotkeyKey { get; set; } = HotkeyService.DefaultStartKey.ToString();
    public int PreviewModeIndex { get; set; } // 0 = Concatenated, 1 = Multi-highlight
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
    /// <summary>Semicolon-separated file extensions to skip entirely (no binary check, no content read). e.g. "exe;dll;zip;png;jpg".</summary>
    public string SkipExtensions { get; set; } = "exe;dll;pdb;obj;lib;so;dylib;zip;gz;tar;7z;rar;bz2;xz;iso;cab;msi;nupkg;whl;png;jpg;jpeg;gif;bmp;ico;tif;tiff;webp;svg;mp3;mp4;avi;mov;wmv;flv;mkv;wav;ogg;flac;woff;woff2;ttf;eot;otf;pdf;doc;docx;xls;xlsx;ppt;pptx";

    public const int MaxRecent = 20; // kept for backward compat; prefer MaxRecentItems
}

public sealed class SettingsService
{
    private readonly string _path;
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public SettingsService() : this(DefaultPath()) { }

    public SettingsService(string path) { _path = path; }

    public static string DefaultPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuickGrep", "settings.json");

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
            return settings;
        }
        catch (Exception ex) { LogService.Instance.Warning("Settings", $"Failed to load settings from {_path}", ex); return new AppSettings(); }
    }

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

    public static void PushRecent(List<string> list, string value, int max = AppSettings.MaxRecent)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        list.RemoveAll(s => string.Equals(s, value, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, value);
        while (list.Count > max) list.RemoveAt(list.Count - 1);
    }
}
