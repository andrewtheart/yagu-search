using Yagu.Services;

namespace Yagu.Tests;

public class LogServiceDefaultTests : IDisposable
{
    private readonly string _logPath;

    public LogServiceDefaultTests()
    {
        var dir = Path.Combine(Path.GetTempPath(), "yagu-logdefault-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _logPath = Path.Combine(dir, "test.log");
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_logPath)!, true); } catch { }
    }

    [Fact]
    public void DefaultFileLevel_IsWarning()
    {
        using var svc = new LogService(_logPath);
        Assert.Equal(LogLevel.Warning, svc.FileLevel);
    }

    [Fact]
    public void DefaultConsoleLevel_IsWarning()
    {
        using var svc = new LogService(_logPath);
        Assert.Equal(LogLevel.Warning, svc.ConsoleLevel);
    }

    [Fact]
    public void Default_WarningIsLogged()
    {
        using var svc = new LogService(_logPath);
        // Don't set Level — use default
        svc.Warning("src", "should appear");
        svc.Flush();
        var content = File.ReadAllText(_logPath);
        Assert.Contains("[WRN]", content);
        Assert.Contains("should appear", content);
    }

    [Fact]
    public void Default_InfoIsNotLogged()
    {
        using var svc = new LogService(_logPath);
        // Default is Warning, so Info should be suppressed
        svc.Info("src", "should not appear");
        svc.Flush();
        if (File.Exists(_logPath))
            Assert.Empty(File.ReadAllText(_logPath).Trim());
    }

    [Fact]
    public void Default_CriticalIsLogged()
    {
        using var svc = new LogService(_logPath);
        svc.Critical("src", "critical msg");
        svc.Flush();
        var content = File.ReadAllText(_logPath);
        Assert.Contains("[CRT]", content);
    }
}

public class AppSettingsDefaultTests
{
    [Fact]
    public void DefaultLogLevelIndex_IsWarning()
    {
        var settings = new AppSettings();
        Assert.Equal(1, settings.LogLevelIndex); // 1 = Warning
    }

    [Fact]
    public void DefaultConsoleLogLevelIndex_IsWarning()
    {
        var settings = new AppSettings();
        Assert.Equal(1, settings.ConsoleLogLevelIndex); // 1 = Warning
    }

    [Fact]
    public void FreshLoad_HasWarningDefaults()
    {
        var svc = new SettingsService(Path.Combine(Path.GetTempPath(), "nonexistent-" + Guid.NewGuid() + ".json"));
        var settings = svc.Load();
        Assert.Equal(1, settings.LogLevelIndex);
        Assert.Equal(1, settings.ConsoleLogLevelIndex);
    }
}
