using Yagu.Services;

namespace Yagu.Tests;

public class LogServiceTests : IDisposable
{
    private readonly string _logDir;
    private readonly string _logPath;

    public LogServiceTests()
    {
        _logDir = Path.Combine(Path.GetTempPath(), "qg-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_logDir);
        _logPath = Path.Combine(_logDir, "test.log");
    }

    public void Dispose()
    {
        try { Directory.Delete(_logDir, true); } catch { }
    }

    [Fact]
    public void DefaultLogPath_IsUnderAppData()
    {
        var path = LogService.DefaultLogPath();
        Assert.Contains("Yagu", path);
        Assert.EndsWith("yagu.log", path);
    }

    [Fact]
    public void Write_BelowLevel_IsIgnored()
    {
        using var svc = new LogService(_logPath);
        svc.Level = LogLevel.Critical;
        svc.Info("test", "should not appear");
        svc.Flush();
        if (File.Exists(_logPath))
            Assert.Empty(File.ReadAllText(_logPath).Trim());
    }

    [Fact]
    public void Write_AtLevel_IsWritten()
    {
        using var svc = new LogService(_logPath);
        svc.Level = LogLevel.Info;
        svc.Info("test", "hello");
        svc.Flush();
        var content = File.ReadAllText(_logPath);
        Assert.Contains("[INF]", content);
        Assert.Contains("[test]", content);
        Assert.Contains("hello", content);
    }

    [Fact]
    public void Critical_WrittenAtCriticalLevel()
    {
        using var svc = new LogService(_logPath);
        svc.Level = LogLevel.Critical;
        svc.Critical("src", "crit msg");
        svc.Flush();
        var content = File.ReadAllText(_logPath);
        Assert.Contains("[CRT]", content);
    }

    [Fact]
    public void Critical_WithException_IncludesExceptionText()
    {
        using var svc = new LogService(_logPath);
        svc.Level = LogLevel.Critical;
        svc.Critical("src", "crit msg", new InvalidOperationException("boom"));
        svc.Flush();
        var content = File.ReadAllText(_logPath);
        Assert.Contains("boom", content);
        Assert.Contains("Exception:", content);
    }

    [Fact]
    public void Warning_WrittenAtWarningLevel()
    {
        using var svc = new LogService(_logPath);
        svc.Level = LogLevel.Warning;
        svc.Warning("src", "warn msg");
        svc.Flush();
        var content = File.ReadAllText(_logPath);
        Assert.Contains("[WRN]", content);
    }

    [Fact]
    public void Warning_WithException_IncludesExceptionText()
    {
        using var svc = new LogService(_logPath);
        svc.Level = LogLevel.Warning;
        svc.Warning("src", "warn msg", new InvalidOperationException("oops"));
        svc.Flush();
        var content = File.ReadAllText(_logPath);
        Assert.Contains("oops", content);
    }

    [Fact]
    public void Verbose_WrittenAtVerboseLevel()
    {
        using var svc = new LogService(_logPath);
        svc.Level = LogLevel.Verbose;
        svc.Verbose("src", "verbose msg");
        svc.Flush();
        var content = File.ReadAllText(_logPath);
        Assert.Contains("[VRB]", content);
    }

    [Fact]
    public void Verbose_WithException_IncludesExceptionText()
    {
        using var svc = new LogService(_logPath);
        svc.Level = LogLevel.Verbose;
        svc.Verbose("src", "verbose msg", new ArgumentException("arg"));
        svc.Flush();
        var content = File.ReadAllText(_logPath);
        Assert.Contains("arg", content);
    }

    [Fact]
    public void Verbose_StringOnly_NoException()
    {
        using var svc = new LogService(_logPath);
        svc.Level = LogLevel.Verbose;
        svc.Verbose("src", "just a message");
        svc.Flush();
        var content = File.ReadAllText(_logPath);
        Assert.Contains("just a message", content);
        Assert.DoesNotContain("Exception:", content);
    }

    [Fact]
    public void Flush_EmptyQueue_NoFile()
    {
        using var svc = new LogService(_logPath);
        svc.Flush();
        // No file created when queue is empty
        Assert.False(File.Exists(_logPath));
    }

    [Fact]
    public void RotateIfNeeded_SmallFile_NoRotation()
    {
        File.WriteAllText(_logPath, "small");
        using var svc = new LogService(_logPath);
        svc.RotateIfNeeded(1000);
        Assert.True(File.Exists(_logPath));
        Assert.False(File.Exists(_logPath + ".old"));
    }

    [Fact]
    public void RotateIfNeeded_LargeFile_Rotates()
    {
        File.WriteAllText(_logPath, new string('x', 200));
        using var svc = new LogService(_logPath);
        svc.RotateIfNeeded(100);
        Assert.True(File.Exists(_logPath + ".old"));
    }

    [Fact]
    public void RotateIfNeeded_OldBackupDeleted()
    {
        var oldPath = _logPath + ".old";
        File.WriteAllText(oldPath, "previous backup");
        File.WriteAllText(_logPath, new string('x', 200));
        using var svc = new LogService(_logPath);
        svc.RotateIfNeeded(100);
        Assert.True(File.Exists(oldPath));
        Assert.Equal(new string('x', 200), File.ReadAllText(oldPath));
    }

    [Fact]
    public void RotateIfNeeded_FileDoesNotExist_NoOp()
    {
        using var svc = new LogService(_logPath);
        svc.RotateIfNeeded(); // should not throw
    }

    [Fact]
    public void Init_SetsLevelAndLogs()
    {
        // Init uses the singleton. Just verify it doesn't throw.
        LogService.Init(LogLevel.Info);
        Assert.Equal(LogLevel.Info, LogService.Instance.Level);
    }

    [Fact]
    public void Dispose_FlushesBeforeDispose()
    {
        var svc = new LogService(_logPath);
        svc.Level = LogLevel.Info;
        svc.Info("test", "before dispose");
        svc.Dispose();
        var content = File.ReadAllText(_logPath);
        Assert.Contains("before dispose", content);
    }

    [Fact]
    public void Dispose_CalledTwice_NoThrow()
    {
        var svc = new LogService(_logPath);
        svc.Dispose();
        svc.Dispose(); // should not throw
    }

    [Fact]
    public void Level_GetSet()
    {
        using var svc = new LogService(_logPath);
        svc.Level = LogLevel.Verbose;
        Assert.Equal(LogLevel.Verbose, svc.Level);
        svc.Level = LogLevel.Critical;
        Assert.Equal(LogLevel.Critical, svc.Level);
    }

    [Fact]
    public void NoneLevel_NothingWrittenToFile()
    {
        using var svc = new LogService(_logPath);
        svc.FileLevel = LogLevel.None;
        svc.ConsoleLevel = LogLevel.None;
        svc.Critical("test", "should not appear");
        svc.Warning("test", "should not appear");
        svc.Info("test", "should not appear");
        svc.Verbose("test", "should not appear");
        svc.Flush();
        Assert.False(File.Exists(_logPath));
    }

    [Fact]
    public void FileLevel_IndependentOfConsoleLevel()
    {
        using var svc = new LogService(_logPath);
        svc.FileLevel = LogLevel.Warning;
        svc.ConsoleLevel = LogLevel.None;
        svc.Warning("test", "file-warn");
        svc.Info("test", "file-info-should-not-appear");
        svc.Flush();
        var content = File.ReadAllText(_logPath);
        Assert.Contains("file-warn", content);
        Assert.DoesNotContain("file-info-should-not-appear", content);
    }

    [Fact]
    public void Level_SetsBothFileLevelAndConsoleLevel()
    {
        using var svc = new LogService(_logPath);
        svc.Level = LogLevel.Verbose;
        Assert.Equal(LogLevel.Verbose, svc.FileLevel);
        Assert.Equal(LogLevel.Verbose, svc.ConsoleLevel);
    }

    [Fact]
    public void Level_Getter_ReturnsFileLevel()
    {
        using var svc = new LogService(_logPath);
        svc.FileLevel = LogLevel.Info;
        svc.ConsoleLevel = LogLevel.Verbose;
        Assert.Equal(LogLevel.Info, svc.Level);
    }

    [Fact]
    public void Init_TwoArgs_SetsBothLevels()
    {
        var origFile = LogService.Instance.FileLevel;
        var origConsole = LogService.Instance.ConsoleLevel;
        try
        {
            LogService.Init(LogLevel.Warning, LogLevel.Verbose);
            Assert.Equal(LogLevel.Warning, LogService.Instance.FileLevel);
            Assert.Equal(LogLevel.Verbose, LogService.Instance.ConsoleLevel);
        }
        finally
        {
            LogService.Instance.FileLevel = origFile;
            LogService.Instance.ConsoleLevel = origConsole;
        }
    }
}

// ─── LogService: exercise catch paths ───────────────────────────────────

public class LogServiceExtraTests
{
    [Fact]
    public void RotateIfNeeded_NonexistentFile_DoesNotThrow()
    {
        var log = new LogService(Path.Combine(Path.GetTempPath(), "qg-log-nonexistent-" + Guid.NewGuid() + ".log"));
        log.RotateIfNeeded();
        log.Dispose();
    }

    [Fact]
    public void RotateIfNeeded_SmallFile_NoRotation()
    {
        var path = Path.Combine(Path.GetTempPath(), "qg-log-small-" + Guid.NewGuid() + ".log");
        try
        {
            File.WriteAllText(path, "small content");
            var log = new LogService(path);
            log.RotateIfNeeded();
            Assert.True(File.Exists(path));
            Assert.False(File.Exists(path + ".old"));
            log.Dispose();
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".old")) File.Delete(path + ".old");
        }
    }

    [Fact]
    public void RotateIfNeeded_LargeFile_Rotates()
    {
        var path = Path.Combine(Path.GetTempPath(), "qg-log-large-" + Guid.NewGuid() + ".log");
        try
        {
            File.WriteAllBytes(path, new byte[6 * 1024 * 1024]);
            var log = new LogService(path);
            log.RotateIfNeeded();
            Assert.False(File.Exists(path));
            Assert.True(File.Exists(path + ".old"));
            log.Dispose();
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".old")) File.Delete(path + ".old");
        }
    }

    [Fact]
    public void Write_AllLogLevels()
    {
        var path = Path.Combine(Path.GetTempPath(), "qg-log-all-" + Guid.NewGuid() + ".log");
        try
        {
            var log = new LogService(path);
            log.Level = LogLevel.Verbose;
            log.Critical("test", "critical msg", new Exception("crit-ex"));
            log.Warning("test", "warning msg", new Exception("warn-ex"));
            log.Info("test", "info msg");
            log.Verbose("test", "verbose msg");
            log.Verbose("test", "verbose with ex", new Exception("verb-ex"));
            log.Flush();

            var content = File.ReadAllText(path);
            Assert.Contains("CRT", content);
            Assert.Contains("WRN", content);
            Assert.Contains("INF", content);
            Assert.Contains("VRB", content);
            Assert.Contains("crit-ex", content);
            log.Dispose();
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Flush_EmptyQueue_DoesNotThrow()
    {
        var path = Path.Combine(Path.GetTempPath(), "qg-log-empty-" + Guid.NewGuid() + ".log");
        var log = new LogService(path);
        log.Flush();
        log.Dispose();
    }

    [Fact]
    public void Dispose_FlushesRemainingMessages()
    {
        var path = Path.Combine(Path.GetTempPath(), "qg-log-dispose-" + Guid.NewGuid() + ".log");
        try
        {
            var log = new LogService(path);
            log.Level = LogLevel.Info;
            log.Info("test", "should be flushed on dispose");
            log.Dispose();
            log.Dispose(); // double dispose is safe

            var content = File.ReadAllText(path);
            Assert.Contains("should be flushed on dispose", content);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Init_SetsLevel()
    {
        var original = LogService.Instance.Level;
        try
        {
            LogService.Init(LogLevel.Verbose);
            Assert.Equal(LogLevel.Verbose, LogService.Instance.Level);
        }
        finally
        {
            LogService.Instance.Level = original;
        }
    }

    [Fact]
    public void DefaultLogPath_ContainsYagu()
    {
        var path = LogService.DefaultLogPath();
        Assert.Contains("Yagu", path);
        Assert.EndsWith(".log", path);
    }
}

// ─── LogService: unknown log level + rotate overwrite ───────────────────

public class LogServiceEdgeCaseTests
{
    [Fact]
    public void Write_UnknownLevel_HandledGracefully()
    {
        var path = Path.Combine(Path.GetTempPath(), "qg-log-edge-" + Guid.NewGuid() + ".log");
        try
        {
            var log = new LogService(path);
            log.Level = (LogLevel)99;
            log.Critical("test", "msg");
            log.Flush();
            var content = File.ReadAllText(path);
            Assert.Contains("CRT", content);
            log.Dispose();
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void RotateIfNeeded_OverwritesExistingBackup()
    {
        var path = Path.Combine(Path.GetTempPath(), "qg-log-rotate-" + Guid.NewGuid() + ".log");
        try
        {
            File.WriteAllText(path + ".old", "old backup");
            File.WriteAllBytes(path, new byte[6 * 1024 * 1024]);
            var log = new LogService(path);
            log.RotateIfNeeded();
            Assert.False(File.Exists(path));
            Assert.True(File.Exists(path + ".old"));
            Assert.True(new FileInfo(path + ".old").Length > 5 * 1024 * 1024);
            log.Dispose();
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".old")) File.Delete(path + ".old");
        }
    }
}

public class FileWatchDiagnosticsCoverageTests
{
    /// <summary>
    /// Clear shared static state before and after each test so the opt-in watch
    /// set never leaks between tests.
    /// </summary>
    private static void RunWithCleanState(Action action)
    {
        FileWatchDiagnostics.Clear();
        try
        {
            action();
        }
        finally
        {
            FileWatchDiagnostics.Clear();
        }
    }

    [Fact]
    public void RegisteredPattern_IsWatched()
    {
        RunWithCleanState(() =>
        {
            FileWatchDiagnostics.Add("suspect-file.dat");
            Assert.True(FileWatchDiagnostics.IsWatched(@"C:\data\suspect-file.dat"));
        });
    }

    [Fact]
    public void RegisteredPattern_MatchIsCaseInsensitive()
    {
        RunWithCleanState(() =>
        {
            FileWatchDiagnostics.Add("suspect-file.dat");
            Assert.True(FileWatchDiagnostics.IsWatched(@"C:\DATA\SUSPECT-FILE.DAT"));
        });
    }

    [Fact]
    public void IsWatched_ReturnsFalse_ForUnknownFile()
    {
        Assert.False(FileWatchDiagnostics.IsWatched(@"C:\normal\file.txt"));
    }

    [Fact]
    public void Add_And_IsWatched()
    {
        RunWithCleanState(() =>
        {
            FileWatchDiagnostics.Add("mypattern.dat");
            Assert.True(FileWatchDiagnostics.IsWatched(@"C:\some\path\mypattern.dat"));
        });
    }

    [Fact]
    public void Clear_RemovesAllPatterns()
    {
        RunWithCleanState(() =>
        {
            FileWatchDiagnostics.Add("pattern1");
            FileWatchDiagnostics.Clear();
            Assert.False(FileWatchDiagnostics.IsWatched("pattern1"));
        });
    }

    [Fact]
    public void IsWatched_EmptyPatterns_ReturnsFalse()
    {
        RunWithCleanState(() =>
        {
            // After Clear, patterns is empty
            Assert.False(FileWatchDiagnostics.IsWatched("anything"));
        });
    }

    [Fact]
    public void Checkpoint_DoesNotThrow()
    {
        // Just exercises the code path; Checkpoint logs via LogService
        FileWatchDiagnostics.Checkpoint(@"C:\test\file.txt", "test-phase", 100, "extra info");
        FileWatchDiagnostics.Checkpoint(@"C:\test\file.txt", "test-phase"); // no optional params
    }
}

// ─── LogService: RotateIfNeeded ─────────────────────────────────────

public class LogServiceRotateTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"qg-rotate-{Guid.NewGuid()}.log");

    public void Dispose()
    {
        try { File.Delete(_path); } catch { }
        try { File.Delete(_path + ".old"); } catch { }
    }

    [Fact]
    public void RotateIfNeeded_NoFile_DoesNotThrow()
    {
        var log = new LogService(_path);
        log.RotateIfNeeded();
        log.Dispose();
    }

    [Fact]
    public void RotateIfNeeded_SmallFile_DoesNotRotate()
    {
        File.WriteAllText(_path, "small");
        var log = new LogService(_path);
        log.RotateIfNeeded(1024);
        log.Dispose();
        Assert.True(File.Exists(_path));
        Assert.False(File.Exists(_path + ".old"));
    }

    [Fact]
    public void RotateIfNeeded_LargeFile_Rotates()
    {
        File.WriteAllBytes(_path, new byte[2000]);
        var log = new LogService(_path);
        log.RotateIfNeeded(1000);
        log.Dispose();
        Assert.True(File.Exists(_path + ".old"));
    }

    [Fact]
    public void RotateIfNeeded_OldBackupExists_ReplacesIt()
    {
        File.WriteAllText(_path + ".old", "old backup");
        File.WriteAllBytes(_path, new byte[2000]);
        var log = new LogService(_path);
        log.RotateIfNeeded(1000);
        log.Dispose();
        Assert.True(File.Exists(_path + ".old"));
        Assert.Equal(2000, new FileInfo(_path + ".old").Length);
    }

    [Fact]
    public void Write_FiltersByLevel()
    {
        var log = new LogService(_path);
        log.Level = LogLevel.Warning;
        log.Info("test", "should be filtered");
        log.Warning("test", "should pass");
        log.Flush();
        log.Dispose();

        if (File.Exists(_path))
        {
            var content = File.ReadAllText(_path);
            Assert.DoesNotContain("should be filtered", content);
            Assert.Contains("should pass", content);
        }
    }
}

// ─── LogService: catch block / edge case coverage ───────────────────────

public class LogServiceCatchBlockTests
{
    [Fact]
    public void Flush_InvalidPath_CatchesSilently()
    {
        // Path with invalid characters triggers IOException in StreamWriter ctor
        var badPath = Path.Combine("Z:\\", new string('x', 300), "yagu.log");
        var log = new LogService(badPath);
        log.Level = LogLevel.Verbose;
        log.Info("test", "will fail to flush");
        // Flush should not throw — the catch block absorbs the error
        log.Flush();
        log.Dispose();
    }

    [Fact]
    public void RotateIfNeeded_LockedFile_CatchesSilently()
    {
        var path = Path.Combine(Path.GetTempPath(), $"qg-locked-{Guid.NewGuid()}.log");
        try
        {
            // Create a large file and lock it
            File.WriteAllBytes(path, new byte[2000]);
            using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            var log = new LogService(path);
            // RotateIfNeeded should not throw when File.Move fails due to lock
            log.RotateIfNeeded(1000);
            log.Dispose();
        }
        finally
        {
            try { File.Delete(path); } catch { }
            try { File.Delete(path + ".old"); } catch { }
        }
    }
}

// ─── LogService: IsInfoEnabled / IsVerboseEnabled ───────────────────────

public class LogServiceLevelPropertyTests : IDisposable
{
    private readonly LogService _log;
    public LogServiceLevelPropertyTests()
    {
        var dir = Path.Combine(Path.GetTempPath(), "yagu-loglevel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _log = new LogService(Path.Combine(dir, "test.log"));
    }
    public void Dispose() => _log.Dispose();

    [Fact]
    public void IsInfoEnabled_WhenFileLevelIsInfo_ReturnsTrue()
    {
        _log.FileLevel = LogLevel.Info;
        _log.ConsoleLevel = LogLevel.None;
        Assert.True(_log.IsInfoEnabled);
    }

    [Fact]
    public void IsInfoEnabled_WhenConsoleLevelIsInfo_ReturnsTrue()
    {
        _log.FileLevel = LogLevel.None;
        _log.ConsoleLevel = LogLevel.Info;
        Assert.True(_log.IsInfoEnabled);
    }

    [Fact]
    public void IsInfoEnabled_WhenBothNone_ReturnsFalse()
    {
        _log.FileLevel = LogLevel.None;
        _log.ConsoleLevel = LogLevel.None;
        Assert.False(_log.IsInfoEnabled);
    }

    [Fact]
    public void IsInfoEnabled_WhenBothWarningOnly_ReturnsFalse()
    {
        _log.FileLevel = LogLevel.Warning;
        _log.ConsoleLevel = LogLevel.Warning;
        Assert.False(_log.IsInfoEnabled);
    }

    [Fact]
    public void IsVerboseEnabled_WhenFileLevelIsVerbose_ReturnsTrue()
    {
        _log.FileLevel = LogLevel.Verbose;
        _log.ConsoleLevel = LogLevel.None;
        Assert.True(_log.IsVerboseEnabled);
    }

    [Fact]
    public void IsVerboseEnabled_WhenConsoleLevelIsVerbose_ReturnsTrue()
    {
        _log.FileLevel = LogLevel.None;
        _log.ConsoleLevel = LogLevel.Verbose;
        Assert.True(_log.IsVerboseEnabled);
    }

    [Fact]
    public void IsVerboseEnabled_WhenBothNone_ReturnsFalse()
    {
        _log.FileLevel = LogLevel.None;
        _log.ConsoleLevel = LogLevel.None;
        Assert.False(_log.IsVerboseEnabled);
    }

    [Fact]
    public void IsVerboseEnabled_WhenBothInfo_ReturnsFalse()
    {
        _log.FileLevel = LogLevel.Info;
        _log.ConsoleLevel = LogLevel.Info;
        Assert.False(_log.IsVerboseEnabled);
    }
}
