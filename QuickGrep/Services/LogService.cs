using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;

namespace QuickGrep.Services;

public enum LogLevel
{
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
    private LogLevel _level = LogLevel.Critical;
    private bool _disposed;

    public LogLevel Level
    {
        get => _level;
        set => _level = value;
    }

    public LogService() : this(DefaultLogPath()) { }

    public LogService(string logPath)
    {
        _logPath = logPath;
        _flushTimer = new Timer(_ => Flush(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    public static string DefaultLogPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "QuickGrep", "quickgrep.log");

    public static void Init(LogLevel level)
    {
        Instance.Level = level;
        Instance.Info("LogService", $"Logging initialized at level {level}");
    }

    public void Critical(string source, string message, Exception? ex = null)
        => Write(LogLevel.Critical, source, message, ex);

    public void Warning(string source, string message, Exception? ex = null)
        => Write(LogLevel.Warning, source, message, ex);

    public void Info(string source, string message)
        => Write(LogLevel.Info, source, message, null);

    public void Verbose(string source, string message)
        => Write(LogLevel.Verbose, source, message, null);

    public void Verbose(string source, string message, Exception? ex)
        => Write(LogLevel.Verbose, source, message, ex);

    [ExcludeFromCodeCoverage]
    private void Write(LogLevel level, string source, string message, Exception? ex)
    {
        if (level > _level) return;
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
        _queue.Enqueue(line);
    }

    [ExcludeFromCodeCoverage]
    public void Flush()
    {
        if (_queue.IsEmpty) return;
        try
        {
            var dir = Path.GetDirectoryName(_logPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            using var sw = new StreamWriter(_logPath, append: true);
            while (_queue.TryDequeue(out var line))
                sw.WriteLine(line);
        }
        catch { /* last-resort: don't crash on logging failure */ }
    }

    [ExcludeFromCodeCoverage]
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
