using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Yagu.Models;
using Yagu.Services;

namespace Yagu;

/// <summary>
/// Implements <c>--cli</c> mode: attaches to the parent console, parses command-line
/// arguments, loads settings (local <c>.yagu.json</c> → global AppData → CLI overrides),
/// and streams search results to stdout in ripgrep-compatible format while writing
/// warnings and the completion summary to stderr.
/// </summary>
internal static class CliRunner
{
    private const string LocalSettingsFileName = ".yagu.json";

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateFile(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        nint lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, nint hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetStdHandle(uint nStdHandle, nint hHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetStdHandle(uint nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(nint hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(nint hConsoleHandle, uint dwMode);

    [DllImport("kernel32.dll")]
    private static extern uint GetFileType(nint hFile);

    private const uint FILE_TYPE_CHAR = 0x0002; // interactive console handle

    private const uint GENERIC_WRITE    = 0x40000000;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING    = 3;
    private const uint STD_OUTPUT_HANDLE = unchecked((uint)-11);
    private const uint STD_ERROR_HANDLE  = unchecked((uint)-12);
    private const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;
    private const uint ENABLE_PROCESSED_OUTPUT            = 0x0001;

    // -----------------------------------------------------------------------
    // Public entry-point
    // -----------------------------------------------------------------------

    public static int Run(string[] rawArgs)
    {
        bool vtEnabled = EnsureConsole();

        var args = CliArgs.Parse(rawArgs);

        if (args.ShowHelp)
        {
            PrintHelp();
            return 0;
        }

        if (string.IsNullOrWhiteSpace(args.Directory))
        {
            WriteError("error: --directory is required when using --cli.");
            WriteError("usage: Yagu.exe --cli --directory <path> PATTERN [options]");
            WriteError("       Yagu.exe --cli --help");
            return 2;
        }

        if (!Directory.Exists(args.Directory))
        {
            WriteError($"error: directory does not exist: {args.Directory}");
            return 2;
        }

        if (string.IsNullOrWhiteSpace(args.Pattern))
        {
            WriteError("error: a search pattern is required (positional arg or --pattern).");
            WriteError("usage: Yagu.exe --cli --directory <path> PATTERN [options]");
            return 2;
        }

        WarnIfNotAdmin(args);

        if (vtEnabled)
            OfferEverythingSetupAsync().GetAwaiter().GetResult();

        var settings = LoadEffectiveSettings(args);

        // Apply CLI overrides to settings used outside of SearchOptions.
        if (args.LogLevelIndex.HasValue)         settings.LogLevelIndex = args.LogLevelIndex.Value;
        if (args.ConsoleLogLevelIndex.HasValue)  settings.ConsoleLogLevelIndex = args.ConsoleLogLevelIndex.Value;
        if (args.FileListerBackendIndex.HasValue) settings.FileListerBackendIndex = args.FileListerBackendIndex.Value;
        if (args.LineTruncationLength.HasValue)  settings.LineTruncationLength = args.LineTruncationLength.Value;
        if (args.PreviewContextLines.HasValue)   settings.PreviewContextLines = args.PreviewContextLines.Value;
        if (args.PreviewModeIndex.HasValue)      settings.PreviewModeIndex = args.PreviewModeIndex.Value;
        if (args.PreviewWordWrap.HasValue)       settings.PreviewWordWrap = args.PreviewWordWrap.Value;
        if (args.EditorCommand != null)          settings.EditorCommand = args.EditorCommand;

        // Configure the file-lister backend from settings (same as App() constructor).
        FileLister.Backend = (FileListerBackend)settings.FileListerBackendIndex;
        LogService.Init((LogLevel)settings.LogLevelIndex, (LogLevel)settings.ConsoleLogLevelIndex);

        var options = BuildSearchOptions(args, settings);

        return RunSearchAsync(options, args, vtEnabled).GetAwaiter().GetResult();
    }

    // -----------------------------------------------------------------------
    // Console attachment
    // -----------------------------------------------------------------------

    // Returns true when stdout is an interactive VT-capable terminal (colours enabled).
    private static bool EnsureConsole()
    {
        const uint ATTACH_PARENT_PROCESS = unchecked((uint)-1);

        // Ensure consistent UTF-8 output (no BOM) so non-ASCII chars survive piping.
        var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        Console.OutputEncoding = utf8NoBom;
        Console.InputEncoding  = utf8NoBom;

        // Snapshot whether stdout/stderr were already connected (piped/redirected
        // by the shell) BEFORE we do anything to the handles.
        nint hOutBefore = GetStdHandle(STD_OUTPUT_HANDLE);
        nint hErrBefore = GetStdHandle(STD_ERROR_HANDLE);
        // FILE_TYPE_CHAR means an interactive console device; anything else (pipe/disk) means redirected.
        bool outPiped = hOutBefore == 0 || hOutBefore == -1 || GetFileType(hOutBefore) != FILE_TYPE_CHAR;
        bool errPiped = hErrBefore == 0 || hErrBefore == -1 || GetFileType(hErrBefore) != FILE_TYPE_CHAR;

        if (!outPiped || !errPiped)
        {
            // Not piped/redirected — attach to the parent console so CONOUT$ is valid.
            if (!AttachConsole(ATTACH_PARENT_PROCESS))
                AllocConsole();
        }

        bool vtEnabled = false;

        // Reinitialise Console.Out — use the inherited pipe handle when piped,
        // or CONOUT$ for interactive terminal output.
        try
        {
            if (outPiped)
            {
                var s = Console.OpenStandardOutput();
                Console.SetOut(new StreamWriter(s, utf8NoBom, 4096, leaveOpen: true) { AutoFlush = true });
            }
            else
            {
                var h = CreateFile("CONOUT$", GENERIC_WRITE, FILE_SHARE_WRITE, 0, OPEN_EXISTING, 0, 0);
                if (h != -1 && h != 0)
                {
                    // Enable ANSI/VT escape-sequence processing on this console handle.
                    if (GetConsoleMode(h, out uint mode))
                        SetConsoleMode(h, mode | ENABLE_VIRTUAL_TERMINAL_PROCESSING | ENABLE_PROCESSED_OUTPUT);

                    SetStdHandle(STD_OUTPUT_HANDLE, h);
                    var fs = new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(h, ownsHandle: false), FileAccess.Write, 4096);
                    Console.SetOut(new StreamWriter(fs, utf8NoBom, 4096, leaveOpen: true) { AutoFlush = true });
                    vtEnabled = true;
                }
            }
        }
        catch { /* best-effort */ }

        try
        {
            if (errPiped)
            {
                var s = Console.OpenStandardError();
                Console.SetError(new StreamWriter(s, utf8NoBom, 4096, leaveOpen: true) { AutoFlush = true });
            }
            else
            {
                var h = CreateFile("CONOUT$", GENERIC_WRITE, FILE_SHARE_WRITE, 0, OPEN_EXISTING, 0, 0);
                if (h != -1 && h != 0)
                {
                    if (GetConsoleMode(h, out uint mode))
                        SetConsoleMode(h, mode | ENABLE_VIRTUAL_TERMINAL_PROCESSING | ENABLE_PROCESSED_OUTPUT);

                    SetStdHandle(STD_ERROR_HANDLE, h);
                    var fs = new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(h, ownsHandle: false), FileAccess.Write, 4096);
                    Console.SetError(new StreamWriter(fs, utf8NoBom, 4096, leaveOpen: true) { AutoFlush = true });
                }
            }
        }
        catch { /* best-effort */ }

        return vtEnabled;
    }

    // -----------------------------------------------------------------------
    // Everything Search: offer to start or install (once per machine, tracked by marker file)
    // -----------------------------------------------------------------------

    private static readonly string EverythingMarkerPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "Yagu", ".everything-prompted");

    private static async Task OfferEverythingSetupAsync()
    {
        // Only ask once — marker file records that we've already prompted.
        if (File.Exists(EverythingMarkerPath)) return;

        bool running  = Process.GetProcessesByName("Everything").Length > 0;

        // Everything is running — nothing to do (don't write the marker;
        // if Everything is later uninstalled we still want to ask again).
        if (running) return;

        // Check if Everything.exe can be located (meaning the full app is installed).
        var esPath        = FileLister.FindEsExe();
        var everythingExe = esPath != null ? FindEverythingExe(esPath) : null;

        // Also check standard install paths directly in case es.exe is a standalone tool.
        if (everythingExe == null)
        {
            foreach (var p in new[]
            {
                @"C:\Program Files\Everything\Everything.exe",
                @"C:\Program Files (x86)\Everything\Everything.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Everything", "Everything.exe"),
            })
            {
                if (File.Exists(p)) { everythingExe = p; break; }
            }
        }

        try
        {
            if (everythingExe != null)
            {
                // Full Everything app is installed but not running — offer to start.
                Console.Error.WriteLine();
                Console.Error.Write("Everything Search is installed but not running. Start it now for fast file discovery? [Y/n] ");
                var answer = Console.ReadLine();
                Console.Error.WriteLine();

                if (IsYes(answer))
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo { FileName = everythingExe, UseShellExecute = true });
                        await Task.Delay(1500); // brief wait so it begins indexing before the search
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"Could not start Everything: {ex.Message}");
                    }
                }
                return;
            }

            // Not installed — offer to download and install.
            Console.Error.WriteLine();
            Console.Error.Write("Everything Search by voidtools is not installed. Install it for significantly faster file discovery? [Y/n] ");
            var installAnswer = Console.ReadLine();
            Console.Error.WriteLine();

            if (!IsYes(installAnswer)) return;

            bool is64 = Environment.Is64BitOperatingSystem;
            string url      = is64
                ? "https://www.voidtools.com/Everything-1.4.1.1032.x64-Setup.exe"
                : "https://www.voidtools.com/Everything-1.4.1.1032.x86-Setup.exe";
            string tempPath = Path.Combine(Path.GetTempPath(),
                is64 ? "Everything-1.4.1.1032.x64-Setup.exe" : "Everything-1.4.1.1032.x86-Setup.exe");

            Console.Error.WriteLine("Downloading Everything Search installer...");

            try
            {
                using var http = new HttpClient();
                var data = await http.GetByteArrayAsync(new Uri(url));
                await File.WriteAllBytesAsync(tempPath, data);

                Console.Error.WriteLine("Running installer \u2014 please complete the setup wizard...");

                var proc = Process.Start(new ProcessStartInfo
                {
                    FileName       = tempPath,
                    Verb           = "runas",
                    UseShellExecute = true,
                });
                if (proc != null) await proc.WaitForExitAsync();

                var installedEsPath = FileLister.FindEsExe();
                if (installedEsPath != null && Process.GetProcessesByName("Everything").Length == 0)
                {
                    var postInstallExe = FindEverythingExe(installedEsPath);
                    if (postInstallExe != null)
                    {
                        try { Process.Start(new ProcessStartInfo { FileName = postInstallExe, UseShellExecute = true }); }
                        catch { /* ignore */ }
                        await Task.Delay(2000);
                    }
                }

                Console.Error.WriteLine("Everything installed. Proceeding with search...");
                Console.Error.WriteLine();
            }
            catch (System.ComponentModel.Win32Exception)
            {
                Console.Error.WriteLine("Installation was cancelled.");
                Console.Error.WriteLine();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Installation failed: {ex.Message}");
                Console.Error.WriteLine();
            }
        }
        finally
        {
            // Write marker after the first prompt so we never pester again.
            // (The "already running" early-return above skips the try block entirely,
            //  so this finally only runs when we actually showed a prompt.)
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(EverythingMarkerPath)!);
                await File.WriteAllTextAsync(EverythingMarkerPath, DateTime.UtcNow.ToString("o"));
            }
            catch { /* best-effort */ }
        }
    }

    private static bool IsYes(string? answer)
        => string.IsNullOrWhiteSpace(answer) || answer.Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase);

    private static string? FindEverythingExe(string esPath)
    {
        var dir = Path.GetDirectoryName(esPath);
        if (dir != null)
        {
            var candidate = Path.Combine(dir, "Everything.exe");
            if (File.Exists(candidate)) return candidate;
        }

        // Check registry-discovered install directories
        foreach (var registryDir in FileLister.GetEverythingInstallDirsFromRegistry())
        {
            var candidate = Path.Combine(registryDir, "Everything.exe");
            if (File.Exists(candidate)) return candidate;
        }

        foreach (var path in new[]
        {
            @"C:\Program Files\Everything\Everything.exe",
            @"C:\Program Files (x86)\Everything\Everything.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Everything", "Everything.exe"),
        })
        {
            if (File.Exists(path)) return path;
        }
        return null;
    }

    // -----------------------------------------------------------------------
    // Admin privilege warning (mirrors the UI InfoBar banner)
    // -----------------------------------------------------------------------

    private static void WarnIfNotAdmin(CliArgs args)
    {
        if (args.SuppressAdminWarning) return;

        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        if (!principal.IsInRole(WindowsBuiltInRole.Administrator))
        {
            WriteError("warning: Yagu is not running with administrator privileges.");
            WriteError("         Some files may not be readable. Run as administrator for full access.");
            WriteError(string.Empty);
        }
    }

    // -----------------------------------------------------------------------
    // Settings: .yagu.json in CWD → global AppData settings, then CLI overrides
    // -----------------------------------------------------------------------

    private static AppSettings LoadEffectiveSettings(CliArgs args)
    {
        // 1. Prefer a .yagu.json beside the current working directory.
        var cwdSettings = Path.Combine(Directory.GetCurrentDirectory(), LocalSettingsFileName);
        if (File.Exists(cwdSettings))
            return new SettingsService(cwdSettings).Load();

        // 2. Fall back to global AppData settings.
        return new SettingsService().Load();
    }

    // -----------------------------------------------------------------------
    // Build SearchOptions: merge base settings with CLI overrides
    // -----------------------------------------------------------------------

    private static SearchOptions BuildSearchOptions(CliArgs args, AppSettings s)
    {
        bool caseSensitive  = args.CaseSensitive ?? s.CaseSensitive;
        bool useRegex       = args.UseRegex       ?? s.UseRegex;
        int  contextLines   = args.ContextLines   ?? s.ContextLines;
        long minFileSize    = args.MinFileSizeBytes ?? s.DefaultMinFileSizeBytes;
        long maxFileSize    = args.MaxFileSizeBytes ?? s.DefaultMaxFileSizeBytes;
        bool skipBinary     = args.SkipBinary     ?? s.SkipBinary;
        int  parallelism    = args.Parallelism    ?? SearchOptions.ResolveContentSearchParallelism(s.ParallelismIndex, Environment.ProcessorCount);
        long memoryBytes    = args.MemoryLimitMB.HasValue
            ? (long)args.MemoryLimitMB.Value * 1024 * 1024
            : (long)s.MemoryLimitMB         * 1024 * 1024;
        int maxResults = args.MaxResults ?? s.MaxResults;
        int memoryPressure = args.MemoryPressurePercent ?? s.MemoryPressurePercent;
        int sdkBuffer = args.SdkChannelBufferSize ?? s.SdkChannelBufferSize;
        bool excludeAdminPaths = args.ExcludeAdminProtectedPaths ?? s.ExcludeAdminProtectedPaths;
        string adminSegments = args.AdminProtectedPathSegments ?? s.AdminProtectedPathSegments;
        bool searchArchives = args.SearchInsideArchives ?? s.SearchInsideArchives;
        string archiveExts = args.ArchiveExtensions ?? s.ArchiveExtensions;

        bool obeyGitignore = args.ObeyGitignore ?? s.ObeyGitignore;
        bool gitignorePrecedence = args.GitignoreTakesPrecedence ?? s.GitignoreTakesPrecedence;
        bool exactMatch = args.ExactMatch ?? s.ExactMatch;
        int maxMatchesPerFile = args.MaxMatchesPerFile ?? s.MaxMatchesPerFile;
        int maxSearchDepth = args.MaxSearchDepth ?? s.MaxSearchDepth;
        var includeMode = (FilterPatternMode)(args.IncludeFilterModeIndex ?? s.IncludeFilterModeIndex);
        var excludeMode = (FilterPatternMode)(args.ExcludeFilterModeIndex ?? s.ExcludeFilterModeIndex);

        var includeGlobs = args.IncludeGlobs.Count > 0
            ? (IReadOnlyList<string>)args.IncludeGlobs
            : SplitSemi(s.IncludeGlobs);

        var excludeGlobs = args.ExcludeGlobs.Count > 0
            ? (IReadOnlyList<string>)args.ExcludeGlobs
            : SplitSemi(s.ExcludeGlobs);

        var skipExtensions = args.SkipExtensions.Count > 0
            ? new HashSet<string>(args.SkipExtensions, StringComparer.OrdinalIgnoreCase)
            : ParseSkipExtensions(s.SkipExtensions);

        return new SearchOptions
        {
            Directory             = args.Directory!,
            Query                 = args.Pattern!,
            CaseSensitive         = caseSensitive,
            UseRegex              = useRegex,
            ExactMatch            = exactMatch,
            ContextLines          = contextLines,
            SearchMode            = args.SearchMode ?? SearchMode.Both,
            IncludeGlobs          = includeGlobs,
            ExcludeGlobs          = excludeGlobs,
            IncludeFilterMode     = includeMode,
            ExcludeFilterMode     = excludeMode,
            MinFileSizeBytes      = minFileSize,
            MaxFileSizeBytes      = maxFileSize,
            CreatedAfterDate      = args.CreatedAfter ?? s.DefaultCreatedAfterDate,
            CreatedBeforeDate     = args.CreatedBefore ?? s.DefaultCreatedBeforeDate,
            ModifiedAfterDate     = args.ModifiedAfter ?? s.DefaultModifiedAfterDate,
            ModifiedBeforeDate    = args.ModifiedBefore ?? s.DefaultModifiedBeforeDate,
            MaxResults            = Math.Min(maxResults, SearchOptions.MaxResultsCeiling),
            MaxMatchesPerFile     = maxMatchesPerFile,
            MaxSearchDepth        = maxSearchDepth,
            SkipBinary            = skipBinary,
            ObeyGitignore         = obeyGitignore,
            GitignoreTakesPrecedence = gitignorePrecedence,
            MaxDegreeOfParallelism = parallelism,
            MaxProcessMemoryBytes = memoryBytes,
            MemoryPressurePercent = memoryPressure,
            SkipExtensions        = skipExtensions,
            SdkChannelBufferSize  = sdkBuffer,
            SearchInsideArchives  = searchArchives,
            ArchiveExtensions     = SplitSemi(archiveExts).ToHashSet(StringComparer.OrdinalIgnoreCase),
            ExcludeAdminProtectedPaths = excludeAdminPaths,
            AdminProtectedPathSegments = Yagu.Services.FileLister.ParseAdminProtectedSegments(adminSegments),
        };
    }

    private static string[] SplitSemi(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        return value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static HashSet<string> ParseSkipExtensions(string raw)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(raw))
            foreach (var ext in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                set.Add(ext.TrimStart('.'));
        return set;
    }

    // -----------------------------------------------------------------------
    // Search + ripgrep-style output
    // -----------------------------------------------------------------------

    private static async Task<int> RunSearchAsync(SearchOptions options, CliArgs args, bool vtEnabled)
    {
        bool useColor = vtEnabled;

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var service  = new SearchService();
        var writer   = new RipgrepWriter(Console.Out, useColor);
        var progress = vtEnabled ? new ProgressLine(useColor) : null;

        try
        {
            await foreach (var ev in service.SearchAsync(options, cts.Token).ConfigureAwait(false))
            {
                switch (ev)
                {
                    case SearchEvent.Match m:
                        progress?.Hide();
                        writer.Add(m.Result);
                        progress?.Show();
                        break;

                    case SearchEvent.MatchBatch mb:
                        progress?.Hide();
                        foreach (var r in mb.Results)
                            writer.Add(r);
                        progress?.Show();
                        break;

                    case SearchEvent.Progress p:
                        progress?.Update(p.Snapshot);
                        break;

                    case SearchEvent.Error e:
                        progress?.Hide();
                        WriteError($"error: {e.Message}", useColor);
                        progress?.Show();
                        break;

                    case SearchEvent.MemoryPressure mp:
                        mp.AcknowledgeEviction(0);
                        progress?.Hide();
                        WriteError("warning: memory pressure detected; search continues in degraded mode.", useColor);
                        progress?.Show();
                        break;

                    case SearchEvent.Fallback f:
                        progress?.Hide();
                        WriteError($"info: file-lister fallback - {f.Reason}", useColor);
                        progress?.Show();
                        break;

                    case SearchEvent.Completed c:
                        progress?.Dismiss();
                        writer.Flush();
                        WriteCompletionSummary(c.Summary, useColor);
                        if (c.Summary.Cancelled) return 130;
                        return writer.TotalMatches > 0 ? 0 : 1;
                }
            }
        }
        catch (OperationCanceledException)
        {
            progress?.Dismiss();
            writer.Flush();
            WriteError("search cancelled.");
            return 130;
        }
        catch (IOException)
        {
            // Broken pipe — consumer closed the pipe before we finished.
            // This is normal when users pipe to head/Select-Object -First N.
            progress?.Dismiss();
            return writer.TotalMatches > 0 ? 0 : 1;
        }

        progress?.Dismiss();
        writer.Flush();
        return writer.TotalMatches > 0 ? 0 : 1;
    }

    private const string Orange = "\x1B[38;5;208m";
    private const string Reset  = "\x1B[0m";

    private static void WriteCompletionSummary(SearchSummary s, bool color)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"\nSearched {s.FilesScanned} file(s)");
        if (s.TotalFiles > 0 && s.TotalFiles != s.FilesScanned)
            sb.Append(CultureInfo.InvariantCulture, $" of {s.TotalFiles}");
        if (s.FilesSkipped > 0)
            sb.Append(CultureInfo.InvariantCulture, $", {s.FilesSkipped} skipped");
        sb.Append(CultureInfo.InvariantCulture, $" - {s.TotalMatches} match(es) in {s.FilesWithMatches} file(s)");
        sb.Append(CultureInfo.InvariantCulture, $" [{s.Elapsed.TotalSeconds:F2}s]");
        if (s.Truncated)  sb.Append(" [truncated]");
        if (s.Cancelled)  sb.Append(" [cancelled]");
        WriteError(sb.ToString(), color);

        var b = s.SkipReasons;
        if (b is not null && s.FilesSkipped > 0)
        {
            WriteError("Skipped breakdown:", color);
            if (b.GlobExcluded > 0)  WriteError($"  Glob exclusions:          {b.GlobExcluded,8:N0}", color);
            if (b.Binary > 0)        WriteError($"  Binary files:             {b.Binary,8:N0}", color);
            if (b.ByExtension > 0)   WriteError($"  Extension skips:          {b.ByExtension,8:N0}", color);
            if (b.TooLarge > 0)      WriteError($"  Too large:                {b.TooLarge,8:N0}", color);
            if (b.AccessDenied > 0)  WriteError($"  Access denied:            {b.AccessDenied,8:N0}", color);
            if (b.Directories > 0)   WriteError($"  Inaccessible dirs:        {b.Directories,8:N0}", color);
            if (b.IOError > 0)       WriteError($"  I/O errors:               {b.IOError,8:N0}", color);
            if (b.NotFound > 0)      WriteError($"  Not found:                {b.NotFound,8:N0}", color);
            if (b.Encoding > 0)      WriteError($"  Encoding errors:          {b.Encoding,8:N0}", color);
            if (b.Other > 0)         WriteError($"  Other:                    {b.Other,8:N0}", color);
        }
    }

    private static void WriteError(string msg, bool color = false)
        => Console.Error.WriteLine(color ? $"{Orange}{msg}{Reset}" : msg);

    // -----------------------------------------------------------------------
    // Help text
    // -----------------------------------------------------------------------

    private static void PrintHelp()
    {
        Console.Out.WriteLine("""
            Yagu CLI Mode - Yet Another Grep Utility

            USAGE:
              Yagu.exe --cli --directory <path> PATTERN [OPTIONS]

            REQUIRED:
              --directory <path>          Directory to search recursively.

            PATTERN (positional arg, or explicit flag):
              --pattern <pattern>         Search pattern (literal by default).

            MATCHING:
              -e, --regex                 Treat pattern as a regular expression.
                  --no-regex              Treat pattern as a literal string (default).
              -s, --case-sensitive        Case-sensitive match.
              -i, --ignore-case           Case-insensitive match (default).
              -C, --context <n>           Context lines around each match (default: 3).
                  --search-mode <mode>    both | content | filenames | filename-then-content  (default: both)
                  --exact-match           Match whole words only (default).
                  --no-exact-match        Allow substring matches.

            FILE FILTERING:
              -g, --glob <glob>           Include files matching GLOB (repeatable).
                  --exclude-glob <glob>   Exclude files/dirs matching GLOB (repeatable).
                  --include-regex         Interpret include patterns as regex (default: glob).
                  --include-glob          Interpret include patterns as glob (default).
                  --exclude-regex         Interpret exclude patterns as regex (default: glob).
                  --exclude-glob-mode     Interpret exclude patterns as glob (default).
                  --min-filesize <size>   Skip files smaller than SIZE (e.g. 1M, 10K, 1G).
                  --max-filesize <size>   Skip files larger than SIZE (e.g. 50M, 10K, 1G).
                  --binary                Include binary files in search.
                  --no-binary             Skip binary files (default).
                  --skip-extensions <e>   Semicolon-separated extensions to skip (e.g. exe;dll).
                  --created-after <date>  Only include files created on/after this date (ISO 8601).
                  --created-before <date> Only include files created on/before this date.
                  --modified-after <date> Only include files modified on/after this date.
                  --modified-before <date> Only include files modified on/before this date.

            GITIGNORE:
                  --obey-gitignore        Respect .gitignore exclusions.
                  --no-obey-gitignore     Ignore .gitignore files (default).
                  --gitignore-precedence  .gitignore wins over include filters (default when enabled).
                  --no-gitignore-precedence  Include filters win over .gitignore.

            PERFORMANCE:
                  --threads <n>           Worker threads (0 = auto).
                  --memory-limit <MB>     Process memory cap in megabytes.
                  --memory-pressure <n>   System memory pressure threshold 0-100 (0 = disabled).
                  --sdk-channel-buffer <n> Everything SDK channel buffer size.
                  --file-lister-backend <n> File lister: 0=Auto, 1=SDK, 2=es.exe, 3=Managed.
                  --max-matches-per-file <n> Cap matches per file (0 = unlimited).
                  --max-depth <n>         Max directory recursion depth (0 = unlimited).

            ARCHIVE SEARCH:
                  --search-archives       Search inside ZIP-like archives.
                  --no-search-archives    Do not search inside archives (default).
                  --archive-extensions <e> Semicolon-separated archive extensions.

            PREVIEW:
                  --preview-context <n>   Preview context lines (default: 20).
                  --preview-mode <n>      Preview mode: 0=Concatenated, 1=Multi-highlight.
                  --preview-word-wrap     Enable word wrap in preview.
                  --no-preview-word-wrap  Disable word wrap in preview.

            ADMIN / SECURITY:
                  --no-admin-warning      Suppress the non-administrator privilege warning.
                  --exclude-admin-paths   Skip admin-protected paths (default when non-admin).
                  --no-exclude-admin-paths Include admin-protected paths.
                  --admin-protected-paths <s> Semicolon-separated admin-protected path segments.

            LOGGING:
                  --log-level <n>         File log level: -1=None, 0=Critical, 1=Warning, 2=Info, 3=Verbose.
                  --console-log-level <n> Console log level (same scale as --log-level).

            MISC:
                  --max-results <n>       Stop after N matches (default: 50000).
                  --line-truncation <n>   Truncate printed lines to N characters (0 = no limit).
                  --editor-command <cmd>  Editor launch command (e.g. "code -g {file}:{line}").

            SETTINGS FILE:
              If .yagu.json exists in the current working directory it is used as the
              base configuration. CLI flags always override file-based settings.
              Falls back to the global AppData settings when no local file is present.

            EXIT CODES:
              0   One or more matches found.
              1   No matches found.
              2   Usage error.
              130 Cancelled (Ctrl+C).
            """);
    }
}

// ---------------------------------------------------------------------------
// Ripgrep-compatible output writer
// ---------------------------------------------------------------------------

/// <summary>
/// Writes search results to a <see cref="TextWriter"/> (stdout) in the same
/// format that ripgrep uses:
/// <list type="bullet">
///   <item>File path header, bold when ANSI is supported.</item>
///   <item><c>LINE:</c> prefix for match lines.</item>
///   <item><c>LINE-</c> prefix for context lines.</item>
///   <item><c>--</c> separator between non-adjacent match groups in the same file.</item>
///   <item>Blank line between files.</item>
/// </list>
/// </summary>
// ---------------------------------------------------------------------------
// Persistent progress indicator (stderr, interactive only)
// ---------------------------------------------------------------------------

internal sealed class ProgressLine
{
    private static readonly char[] Spinner = ['|', '/', '-', '\\'];
    private int    _frame;
    private string _current = "";
    private bool   _visible;
    private readonly bool _color;

    private const string Dim   = "\x1B[2m";
    private const string Reset = "\x1B[0m";
    private const string Clear = "\r\x1B[K";

    public ProgressLine(bool color) => _color = color;

    /// <summary>Called on each SearchEvent.Progress — advances spinner and redraws.</summary>
    public void Update(SearchProgress p)
    {
        _frame   = (_frame + 1) % Spinner.Length;
        _current = Build(p);
        Draw();
    }

    /// <summary>Clear the line so stdout match output doesn't overlap.</summary>
    public void Hide()
    {
        if (_visible)
        {
            Console.Error.Write(Clear);
            _visible = false;
        }
    }

    /// <summary>Redraw the last progress line after match output.</summary>
    public void Show()
    {
        if (_current.Length > 0) Draw();
    }

    /// <summary>Clear permanently (search ended).</summary>
    public void Dismiss() => Hide();

    private void Draw()
    {
        Console.Error.Write(Clear + (_color ? Dim + _current + Reset : _current));
        _visible = true;
    }

    private string Build(SearchProgress p)
    {
        char spin = Spinner[_frame];
        // Only show X/Y when TotalFiles is a genuine pre-known total (e.g. from Everything SDK).
        // When no pre-known total exists, TotalFiles == discoveredTotal == FilesScanned,
        // so the X/Y format would show 50K/50K which looks wrong — fall back to "Searching..."
        bool knownTotal = p.TotalFiles > p.FilesScanned;
        return knownTotal
            ? $"{spin} Searching {p.FilesScanned:N0} / {p.TotalFiles:N0} files  ·  {p.MatchesFound:N0} match(es)"
            : $"{spin} Searching... {p.FilesScanned:N0} files  ·  {p.MatchesFound:N0} match(es)";
    }
}

internal sealed class RipgrepWriter
{
    // ANSI escape sequences — matches ripgrep defaults
    private const string BoldMagenta = "\x1B[1;35m";  // file path header
    private const string BoldGreen   = "\x1B[1;32m";  // match line numbers
    private const string BoldBlue    = "\x1B[1;34m";  // context line numbers + separator
    private const string BoldRed     = "\x1B[1;31m";  // matched text within the line
    private const string Reset       = "\x1B[0m";

    private readonly TextWriter _out;
    private readonly bool _color;

    private string? _currentFile;
    private int     _lastLine;       // last line number written for the current file
    private bool    _wroteMatchInFile;

    public int TotalMatches { get; private set; }

    public RipgrepWriter(TextWriter @out, bool color)
    {
        _out   = @out;
        _color = color;
    }

    public void Add(SearchResult result)
    {
        bool sameFile = string.Equals(_currentFile, result.FilePath, StringComparison.OrdinalIgnoreCase);

        // Dedup: multiple matches on the same line produce separate SearchResults;
        // ripgrep prints each line once, so skip any that repeat a line already written.
        if (sameFile && result.LineNumber > 0 && result.LineNumber == _lastLine)
            return;

        TotalMatches++;

        // ---- File header ------------------------------------------------
        if (!sameFile)
        {
            if (_currentFile is not null)
                _out.WriteLine(); // blank line between files

            _out.WriteLine(_color
                ? $"{BoldMagenta}{result.FilePath}{Reset}"
                : result.FilePath);

            _currentFile       = result.FilePath;
            _lastLine          = 0;
            _wroteMatchInFile  = false;
        }

        // ---- Context before ---------------------------------------------
        var before = result.NumberedBefore;
        if (before.Count > 0)
        {
            int firstCtx = before[0].LineNum;
            if (_wroteMatchInFile && firstCtx > _lastLine + 1)
                WriteSep();

            foreach (var ctx in before)
            {
                if (ctx.LineNum > _lastLine)
                {
                    WriteCtx(ctx.LineNum, ctx.Text);
                    _lastLine = ctx.LineNum;
                }
            }
        }
        else if (_wroteMatchInFile && result.LineNumber > _lastLine + 1)
        {
            WriteSep();
        }

        // ---- Match line -------------------------------------------------
        WriteMatch(result.LineNumber, result.MatchLine, result.MatchStartColumn, result.MatchLength);
        _lastLine         = result.LineNumber;
        _wroteMatchInFile = true;

        // ---- Context after ----------------------------------------------
        foreach (var ctx in result.NumberedAfter)
        {
            WriteCtx(ctx.LineNum, ctx.Text);
            _lastLine = ctx.LineNum;
        }
    }

    public void Flush() => _out.Flush();

    // ---- Private helpers -----------------------------------------------

    private void WriteMatch(int line, string text, int matchStart, int matchLength)
    {
        if (_color)
        {
            string highlighted = HighlightMatch(text, matchStart, matchLength);
            _out.WriteLine($"{BoldGreen}{line}{Reset}:{highlighted}");
        }
        else
        {
            _out.WriteLine($"{line}:{text}");
        }
    }

    private void WriteCtx(int line, string text)
    {
        if (_color)
            _out.WriteLine($"{BoldBlue}{line}{Reset}-{text}");
        else
            _out.WriteLine($"{line}-{text}");
    }

    private void WriteSep()
    {
        _out.WriteLine(_color ? $"{BoldBlue}--{Reset}" : "--");
    }

    private string HighlightMatch(string text, int start, int length)
    {
        // Guard against out-of-range offsets (e.g. evicted results or filename matches).
        if (!_color || length <= 0 || start < 0 || start >= text.Length)
            return text;
        int end = Math.Min(start + length, text.Length);
        return text[..start] + BoldRed + text[start..end] + Reset + text[end..];
    }
}

// ---------------------------------------------------------------------------
// CLI argument parser
// ---------------------------------------------------------------------------

/// <summary>Parsed command-line arguments for <c>--cli</c> mode.</summary>
internal sealed class CliArgs
{
    private static readonly char[] s_extensionSeparators = [';', ','];

    public string?          Directory    { get; private set; }
    public string?          Pattern      { get; private set; }
    public bool?            CaseSensitive { get; private set; }
    public bool?            UseRegex     { get; private set; }
    public int?             ContextLines { get; private set; }
    public List<string>     IncludeGlobs { get; } = [];
    public List<string>     ExcludeGlobs { get; } = [];
    public long?            MinFileSizeBytes { get; private set; }
    public long?            MaxFileSizeBytes { get; private set; }
    public int?             MaxResults   { get; private set; }
    public bool?            SkipBinary   { get; private set; }
    public List<string>     SkipExtensions { get; } = [];
    public SearchMode?      SearchMode   { get; private set; }
    public int?             Parallelism  { get; private set; }
    public int?             MemoryLimitMB { get; private set; }
    public int?             MemoryPressurePercent { get; private set; }
    public int?             SdkChannelBufferSize { get; private set; }
    public int?             LineTruncationLength { get; private set; }
    public int?             PreviewContextLines { get; private set; }
    public int?             PreviewModeIndex { get; private set; }
    public bool?            PreviewWordWrap { get; private set; }
    public int?             LogLevelIndex { get; private set; }
    public int?             ConsoleLogLevelIndex { get; private set; }
    public int?             FileListerBackendIndex { get; private set; }
    public bool?            SearchInsideArchives { get; private set; }
    public string?          ArchiveExtensions { get; private set; }
    public string?          EditorCommand { get; private set; }
    public bool?            ExcludeAdminProtectedPaths { get; private set; }
    public string?          AdminProtectedPathSegments { get; private set; }
    public bool?            ObeyGitignore { get; private set; }
    public bool?            GitignoreTakesPrecedence { get; private set; }
    public int?             IncludeFilterModeIndex { get; private set; }
    public int?             ExcludeFilterModeIndex { get; private set; }
    public int?             MaxMatchesPerFile { get; private set; }
    public int?             MaxSearchDepth { get; private set; }
    public bool?            ExactMatch { get; private set; }
    public DateTimeOffset?  CreatedAfter { get; private set; }
    public DateTimeOffset?  CreatedBefore { get; private set; }
    public DateTimeOffset?  ModifiedAfter { get; private set; }
    public DateTimeOffset?  ModifiedBefore { get; private set; }
    public bool             SuppressAdminWarning { get; private set; }
    public bool             ShowHelp     { get; private set; }

    private CliArgs() { }

    public static CliArgs Parse(string[] raw)
    {
        var a = new CliArgs();
        int i = 0;
        while (i < raw.Length)
        {
            var tok = raw[i];

            if (Eq(tok, "--cli"))                          { i++; continue; }
            if (Eq(tok, "--help", "-h", "-?"))             { a.ShowHelp = true; i++; continue; }
            if (Eq(tok, "--case-sensitive", "-s"))         { a.CaseSensitive = true; i++; continue; }
            if (Eq(tok, "--ignore-case", "-i"))            { a.CaseSensitive = false; i++; continue; }
            if (Eq(tok, "--regex", "-e"))                  { a.UseRegex = true; i++; continue; }
            if (Eq(tok, "--no-regex"))                     { a.UseRegex = false; i++; continue; }
            if (Eq(tok, "--no-binary"))                    { a.SkipBinary = true; i++; continue; }
            if (Eq(tok, "--binary"))                       { a.SkipBinary = false; i++; continue; }
            if (Eq(tok, "--no-admin-warning"))             { a.SuppressAdminWarning = true; i++; continue; }
            if (Eq(tok, "--preview-word-wrap"))              { a.PreviewWordWrap = true; i++; continue; }
            if (Eq(tok, "--no-preview-word-wrap"))           { a.PreviewWordWrap = false; i++; continue; }
            if (Eq(tok, "--search-archives"))                { a.SearchInsideArchives = true; i++; continue; }
            if (Eq(tok, "--no-search-archives"))             { a.SearchInsideArchives = false; i++; continue; }
            if (Eq(tok, "--exclude-admin-paths"))            { a.ExcludeAdminProtectedPaths = true; i++; continue; }
            if (Eq(tok, "--no-exclude-admin-paths"))         { a.ExcludeAdminProtectedPaths = false; i++; continue; }
            if (Eq(tok, "--obey-gitignore", "--gitignore"))  { a.ObeyGitignore = true; i++; continue; }
            if (Eq(tok, "--no-obey-gitignore", "--no-gitignore")) { a.ObeyGitignore = false; i++; continue; }
            if (Eq(tok, "--gitignore-precedence"))           { a.GitignoreTakesPrecedence = true; i++; continue; }
            if (Eq(tok, "--no-gitignore-precedence"))        { a.GitignoreTakesPrecedence = false; i++; continue; }
            if (Eq(tok, "--exact-match"))                    { a.ExactMatch = true; i++; continue; }
            if (Eq(tok, "--no-exact-match", "--substring"))  { a.ExactMatch = false; i++; continue; }
            if (Eq(tok, "--include-regex"))                  { a.IncludeFilterModeIndex = 1; i++; continue; }
            if (Eq(tok, "--include-glob"))                   { a.IncludeFilterModeIndex = 0; i++; continue; }
            if (Eq(tok, "--exclude-regex"))                  { a.ExcludeFilterModeIndex = 1; i++; continue; }
            if (Eq(tok, "--exclude-glob-mode"))              { a.ExcludeFilterModeIndex = 0; i++; continue; }

            string? v;
            if (TryGetVal(raw, ref i, out v, "--directory", "--dir"))
                { a.Directory = v.Trim('"'); continue; }
            if (TryGetVal(raw, ref i, out v, "--pattern", "-p"))
                { a.Pattern = v; continue; }
            if (TryGetVal(raw, ref i, out v, "--glob", "-g"))
                { a.IncludeGlobs.Add(v); continue; }
            if (TryGetVal(raw, ref i, out v, "--exclude-glob", "--exclude"))
                { a.ExcludeGlobs.Add(v); continue; }
            if (TryGetVal(raw, ref i, out v, "--skip-extensions"))
            {
                foreach (var ext in v.Split(s_extensionSeparators,
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    a.SkipExtensions.Add(ext.TrimStart('.'));
                continue;
            }
            if (TryGetVal(raw, ref i, out v, "--min-filesize"))
                { a.MinFileSizeBytes = ParseFileSize(v); continue; }
            if (TryGetVal(raw, ref i, out v, "--max-filesize"))
                { a.MaxFileSizeBytes = ParseFileSize(v); continue; }
            if (TryGetVal(raw, ref i, out v, "--search-mode"))
            {
                a.SearchMode = v.ToLowerInvariant() switch
                {
                    "content"                        => Models.SearchMode.Content,
                    "filenames" or "filename" or "files" => Models.SearchMode.FileNames,
                    "filename-then-content" or "filenames-then-content" or "file-name-then-content" or "names-then-content"
                                                     => Models.SearchMode.FileNameThenContent,
                    _                                => Models.SearchMode.Both,
                };
                continue;
            }

            if (TryGetInt(raw, ref i, out int n, "--context", "-C"))    { a.ContextLines  = n; continue; }
            if (TryGetInt(raw, ref i, out n, "--max-results"))           { a.MaxResults    = n; continue; }
            if (TryGetInt(raw, ref i, out n, "--threads", "--parallelism")) { a.Parallelism = n; continue; }
            if (TryGetInt(raw, ref i, out n, "--memory-limit"))          { a.MemoryLimitMB = n; continue; }
            if (TryGetInt(raw, ref i, out n, "--memory-pressure"))       { a.MemoryPressurePercent = n; continue; }
            if (TryGetInt(raw, ref i, out n, "--sdk-channel-buffer"))    { a.SdkChannelBufferSize = n; continue; }
            if (TryGetInt(raw, ref i, out n, "--line-truncation"))       { a.LineTruncationLength = n; continue; }
            if (TryGetInt(raw, ref i, out n, "--preview-context"))       { a.PreviewContextLines = n; continue; }
            if (TryGetInt(raw, ref i, out n, "--preview-mode"))          { a.PreviewModeIndex = n; continue; }
            if (TryGetInt(raw, ref i, out n, "--log-level"))             { a.LogLevelIndex = n; continue; }
            if (TryGetInt(raw, ref i, out n, "--console-log-level"))     { a.ConsoleLogLevelIndex = n; continue; }
            if (TryGetInt(raw, ref i, out n, "--file-lister-backend"))   { a.FileListerBackendIndex = n; continue; }
            if (TryGetInt(raw, ref i, out n, "--max-matches-per-file"))  { a.MaxMatchesPerFile = n; continue; }
            if (TryGetInt(raw, ref i, out n, "--max-depth"))             { a.MaxSearchDepth = n; continue; }
            if (TryGetVal(raw, ref i, out v, "--archive-extensions"))    { a.ArchiveExtensions = v; continue; }
            if (TryGetVal(raw, ref i, out v, "--editor-command"))        { a.EditorCommand = v; continue; }
            if (TryGetVal(raw, ref i, out v, "--admin-protected-paths")) { a.AdminProtectedPathSegments = v; continue; }
            if (TryGetVal(raw, ref i, out v, "--created-after"))         { if (DateTimeOffset.TryParse(v, out var d)) a.CreatedAfter = d; continue; }
            if (TryGetVal(raw, ref i, out v, "--created-before"))        { if (DateTimeOffset.TryParse(v, out var d)) a.CreatedBefore = d; continue; }
            if (TryGetVal(raw, ref i, out v, "--modified-after"))        { if (DateTimeOffset.TryParse(v, out var d)) a.ModifiedAfter = d; continue; }
            if (TryGetVal(raw, ref i, out v, "--modified-before"))       { if (DateTimeOffset.TryParse(v, out var d)) a.ModifiedBefore = d; continue; }

            // Positional: first non-flag is the pattern
            if (!tok.StartsWith('-') && a.Pattern is null)
                { a.Pattern = tok; i++; continue; }

            // Unknown flag — warn and skip
            Console.Error.WriteLine($"warning: unknown flag '{tok}' ignored.");
            i++;
        }
        return a;
    }

    // ---- Helpers -------------------------------------------------------

    private static bool Eq(string tok, params string[] candidates) =>
        candidates.Any(c => string.Equals(tok, c, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Tries to consume a value for <paramref name="flags"/> from <paramref name="args"/>
    /// at position <paramref name="i"/>.  Supports both <c>--flag value</c> and
    /// <c>--flag=value</c> forms.  Advances <paramref name="i"/> on success.
    /// </summary>
    private static bool TryGetVal(string[] args, ref int i, out string value, params string[] flags)
    {
        var tok = args[i];
        foreach (var flag in flags)
        {
            // --flag=value
            var prefix = flag + "=";
            if (tok.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                value = tok[prefix.Length..];
                i++;
                return true;
            }
            // --flag value
            if (string.Equals(tok, flag, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                value = args[i + 1];
                i += 2;
                return true;
            }
        }
        value = string.Empty;
        return false;
    }

    private static bool TryGetInt(string[] args, ref int i, out int value, params string[] flags)
    {
        int saved = i;
        if (TryGetVal(args, ref i, out var s, flags) && int.TryParse(s, out value))
            return true;
        i = saved;   // restore on parse failure so the caller can emit a warning
        value = 0;
        return false;
    }

    private static long ParseFileSize(string s)
    {
        s = s.Trim();
        if (string.IsNullOrEmpty(s)) return 0;
        long mul = 1;
        if      (s.EndsWith("G", StringComparison.OrdinalIgnoreCase)) { mul = 1024L * 1024 * 1024; s = s[..^1]; }
        else if (s.EndsWith("M", StringComparison.OrdinalIgnoreCase)) { mul = 1024L * 1024;         s = s[..^1]; }
        else if (s.EndsWith("K", StringComparison.OrdinalIgnoreCase)) { mul = 1024L;                s = s[..^1]; }
        else if (s.EndsWith("B", StringComparison.OrdinalIgnoreCase)) {                             s = s[..^1]; }
        return long.TryParse(s, out var n) ? n * mul : 0;
    }
}
