using System.Collections.Concurrent;

namespace Yagu.Services;

public enum LogLevel
{
    None = -1,
    Critical = 0,
    Warning = 1,
    Info = 2,
    Verbose = 3,
}

public sealed class LogService : IDisposable
{
    private static LogService? _instance;
    public static LogService Instance => _instance ??= new LogService();

    private readonly ConcurrentQueue<string> _queue = new();
    private readonly Timer _flushTimer;
    private readonly string _logPath;
    private LogLevel _fileLevel = LogLevel.Warning;
    private LogLevel _consoleLevel = LogLevel.Warning;
    private LogLevel? _installerFileFloor;
    private bool _disposed;

    /// <summary>Raised (best-effort, synchronously) for every <see cref="LogLevel.Critical"/> entry,
    /// regardless of whether file/console logging is enabled. Optional subsystems (telemetry, bug
    /// reporting) subscribe so LogService need not depend on them. Handlers MUST be fast and MUST NOT
    /// throw — exceptions are swallowed to keep logging crash-safe — and MUST NOT log at Critical level
    /// themselves (that would re-enter this event).</summary>
    public event Action<string, string, Exception?>? CriticalLogged;

    /// <summary>Log level for file output. The value is clamped UP to the installer override floor
    /// (if any) so a <c>/VERBOSELOG</c> install keeps verbose file logging even when something later
    /// applies the saved (lower) level — e.g. the view-model re-applying <c>LogLevelIndex</c> while the
    /// first window is built, which would otherwise silently revert the override before the issue we
    /// are trying to capture occurs.</summary>
    public LogLevel FileLevel
    {
        get => _fileLevel;
        set => _fileLevel = ClampToInstallerFloor(value);
    }

    /// <summary>Log level for console (stderr) output.</summary>
    public LogLevel ConsoleLevel
    {
        get => _consoleLevel;
        set => _consoleLevel = value;
    }

    /// <summary>
    /// Backward-compatible property. Getter returns <see cref="FileLevel"/>;
    /// setter sets both <see cref="FileLevel"/> and <see cref="ConsoleLevel"/>.
    /// </summary>
    public LogLevel Level
    {
        get => _fileLevel;
        set { FileLevel = value; ConsoleLevel = value; }
    }

    private LogLevel ClampToInstallerFloor(LogLevel value)
        => _installerFileFloor is { } floor && value < floor ? floor : value;

    /// <summary>Installer-set minimum file verbosity (the <c>/VERBOSELOG</c> floor). When set, the
    /// current <see cref="FileLevel"/> is immediately raised to meet it and can no longer be lowered
    /// below it. Internal so tests can exercise the floor without touching the real registry.</summary>
    internal LogLevel? InstallerFileFloor
    {
        get => _installerFileFloor;
        set { _installerFileFloor = value; _fileLevel = ClampToInstallerFloor(_fileLevel); }
    }

    public LogService() : this(DefaultLogPath()) { }

    public LogService(string logPath)
    {
        _logPath = logPath;
        _flushTimer = new Timer(async _ => await FlushAsync(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    public static string DefaultLogPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Yagu", "yagu.log");

    public static void Init(LogLevel level)
    {
        Instance.FileLevel = level;
        Instance.Info("LogService", $"Logging initialized at level {level}");
    }

    public static void Init(LogLevel fileLevel, LogLevel consoleLevel)
    {
        Instance.FileLevel = fileLevel;
        Instance.ConsoleLevel = consoleLevel;
        Instance.Info("LogService", $"Logging initialized — file: {fileLevel}, console: {consoleLevel}");
    }

    /// <summary>HKCU sub-key under which the Yagu installer publishes install-time overrides.</summary>
    internal const string InstallerRegistrySubKey = @"Software\Yagu";

    /// <summary>Value name (under <see cref="InstallerRegistrySubKey"/>) holding an install-time
    /// log-level override. The installer writes "Verbose" here when run with <c>/VERBOSELOG</c>.</summary>
    internal const string LogLevelOverrideValueName = "LogLevelOverride";

    /// <summary>Parses an installer log-level override token (case-insensitive) into a
    /// <see cref="LogLevel"/>. Returns null for null/blank/unrecognized tokens so callers fall back to
    /// the saved setting.</summary>
    internal static LogLevel? ParseLogLevelToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        return token.Trim().ToLowerInvariant() switch
        {
            "none" => LogLevel.None,
            "critical" => LogLevel.Critical,
            "warning" => LogLevel.Warning,
            "info" => LogLevel.Info,
            "verbose" => LogLevel.Verbose,
            _ => null,
        };
    }

    /// <summary>Reads the installer's log-level override from
    /// <c>HKCU\Software\Yagu\LogLevelOverride</c>, if present. The Yagu installer writes this (e.g.
    /// "Verbose" for <c>/VERBOSELOG</c>) so logging can be forced from the FIRST launch without touching
    /// the in-app settings — which are unreachable behind startup modals. Returns null when the value is
    /// absent, unreadable, or unrecognized.</summary>
    public static LogLevel? ReadInstallerLogLevelOverride()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(InstallerRegistrySubKey);
            return ParseLogLevelToken(key?.GetValue(LogLevelOverrideValueName) as string);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Initializes logging from the saved settings levels, letting an installer-set override
    /// (<c>HKCU\Software\Yagu\LogLevelOverride</c>) win for the FILE log. Used at startup by both the GUI
    /// (<c>App</c>) and the CLI (<c>CliRunner</c>): a <c>/VERBOSELOG</c> install produces verbose
    /// <c>yagu.log</c> output from the very first run, before any settings UI is reachable. The override
    /// is installed as a sticky FLOOR so a later <see cref="FileLevel"/> assignment (the view-model
    /// re-applying the saved level during startup) cannot lower it. The console level is left untouched
    /// so CLI output stays clean.</summary>
    public static void InitFromSettings(LogLevel fileLevel, LogLevel consoleLevel)
    {
        var overrideLevel = ReadInstallerLogLevelOverride();
        Instance.InstallerFileFloor = overrideLevel;
        Instance.FileLevel = fileLevel;
        Instance.ConsoleLevel = consoleLevel;
        Instance.Info("LogService",
            $"Logging initialized — file: {Instance.FileLevel}, console: {consoleLevel}"
            + (overrideLevel is null ? string.Empty : $" (installer override: file \u2265 {overrideLevel})"));
    }

    public void Critical(string source, string message, Exception? ex = null)
        => Write(LogLevel.Critical, source, message, ex);

    public void Warning(string source, string message, Exception? ex = null)
        => Write(LogLevel.Warning, source, message, ex);

    public void Info(string source, string message)
        => Write(LogLevel.Info, source, message, null);

    public bool IsInfoEnabled => (_fileLevel != LogLevel.None && LogLevel.Info <= _fileLevel)
                              || (_consoleLevel != LogLevel.None && LogLevel.Info <= _consoleLevel);

    public bool IsVerboseEnabled => (_fileLevel != LogLevel.None && LogLevel.Verbose <= _fileLevel)
                                 || (_consoleLevel != LogLevel.None && LogLevel.Verbose <= _consoleLevel);

    public void Verbose(string source, string message)
        => Write(LogLevel.Verbose, source, message, null);

    public void Verbose(string source, string message, Exception? ex)
        => Write(LogLevel.Verbose, source, message, ex);

    private void Write(LogLevel level, string source, string message, Exception? ex)
    {
        if (level == LogLevel.Critical)
        {
            var handler = CriticalLogged;
            if (handler != null)
            {
                try { handler(source, message, ex); }
                catch { /* never let a telemetry/bug-report handler break logging */ }
            }
        }

        bool toFile = _fileLevel != LogLevel.None && level <= _fileLevel;
        bool toConsole = _consoleLevel != LogLevel.None && level <= _consoleLevel;
        if (!toFile && !toConsole) return;

        var prefix = level switch
        {
            LogLevel.Critical => "CRT",
            LogLevel.Warning => "WRN",
            LogLevel.Info => "INF",
            LogLevel.Verbose => "VRB",
            _ => "???",
        };
        var line = $"[{DateTime.UtcNow:O}] [{prefix}] [{source}] {message}";
        if (ex != null) line += $"\n  Exception: {ex}";

        if (toFile) _queue.Enqueue(line);
        if (toConsole) try { Console.Error.WriteLine(line); } catch { /* ignore if no console */ }
    }

    public void Flush()
    {
        if (_queue.IsEmpty) return;
        try
        {
            var dir = Path.GetDirectoryName(_logPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            using var sw = new StreamWriter(
                new FileStream(_logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 4096));
            while (_queue.TryDequeue(out var line))
                sw.WriteLine(line);
        }
        catch { /* last-resort: don't crash on logging failure */ }
    }

    public async Task FlushAsync()
    {
        if (_queue.IsEmpty) return;
        try
        {
            var dir = Path.GetDirectoryName(_logPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            await using var sw = new StreamWriter(
                new FileStream(_logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 4096, FileOptions.Asynchronous));
            while (_queue.TryDequeue(out var line))
                await sw.WriteLineAsync(line);
        }
        catch { /* last-resort: don't crash on logging failure */ }
    }

    public void RotateIfNeeded(long maxBytes = 5 * 1024 * 1024)
    {
        try
        {
            if (!File.Exists(_logPath)) return;
            var fi = new FileInfo(_logPath);
            if (fi.Length <= maxBytes) return;
            var backup = _logPath + ".old";
            if (File.Exists(backup)) File.Delete(backup);
            File.Move(_logPath, backup);
        }
        catch { }
    }

    /// <summary>
    /// Clears the current log file, truncating it to empty and discarding any buffered lines that have
    /// not yet been flushed (so the periodic flush timer cannot immediately re-populate it right after).
    /// Best-effort and crash-safe: returns <c>false</c> when the file could not be truncated (e.g. it is
    /// locked by another process). Log entries produced after this call are written normally.
    /// </summary>
    public bool Clear()
    {
        try
        {
            // Discard buffered-but-unwritten lines first so a pending flush doesn't resurrect old content.
            while (_queue.TryDequeue(out _)) { }

            var dir = Path.GetDirectoryName(_logPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // FileMode.Create truncates an existing file (or creates an empty one). ShareReadWrite so an
            // open viewer (Notepad/tail) doesn't block the truncate.
            using (new FileStream(_logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite, 4096)) { }
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _flushTimer.Dispose();
        Flush();
    }
}
