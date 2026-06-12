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
    private bool _disposed;

    /// <summary>Log level for file output.</summary>
    public LogLevel FileLevel
    {
        get => _fileLevel;
        set => _fileLevel = value;
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
        set { _fileLevel = value; _consoleLevel = value; }
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _flushTimer.Dispose();
        Flush();
    }
}
