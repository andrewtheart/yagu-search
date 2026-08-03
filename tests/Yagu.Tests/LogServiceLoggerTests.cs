using Microsoft.Extensions.Logging;
using Yagu.Services;
using Yagu.Services.Logging;
using MsLogLevel = Microsoft.Extensions.Logging.LogLevel;
using YaguLogLevel = Yagu.Services.LogLevel;

namespace Yagu.Tests;

public sealed class LogServiceLoggerTests : IDisposable
{
    private readonly string _logPath = Path.Combine(
        Path.GetTempPath(), "yagu-structured-log-" + Guid.NewGuid().ToString("N") + ".log");

    public void Dispose()
    {
        try { File.Delete(_logPath); } catch { }
    }

    [Fact]
    public void Constructor_RejectsNullDependencies()
    {
        using var sink = new LogService(_logPath);

        Assert.Throws<ArgumentNullException>(() => new LogServiceLogger(null!, sink));
        Assert.Throws<ArgumentNullException>(() => new LogServiceLogger("category", null!));
    }

    [Fact]
    public void BeginScope_ReturnsReusableNoopScope()
    {
        using var sink = new LogService(_logPath);
        var logger = new LogServiceLogger("category", sink);

        using var first = logger.BeginScope("first");
        using var second = logger.BeginScope("second");

        Assert.Same(first, second);
    }

    [Theory]
    [InlineData((int)MsLogLevel.None, YaguLogLevel.Verbose, false)]
    [InlineData((int)MsLogLevel.Trace, YaguLogLevel.Verbose, true)]
    [InlineData((int)MsLogLevel.Debug, YaguLogLevel.Verbose, true)]
    [InlineData((int)MsLogLevel.Information, YaguLogLevel.Info, true)]
    [InlineData((int)MsLogLevel.Warning, YaguLogLevel.Warning, true)]
    [InlineData((int)MsLogLevel.Error, YaguLogLevel.Warning, true)]
    [InlineData((int)MsLogLevel.Critical, YaguLogLevel.Critical, true)]
    [InlineData(999, YaguLogLevel.Verbose, true)]
    public void IsEnabled_MapsEveryLevel(int microsoftLevel, YaguLogLevel sinkLevel, bool expected)
    {
        using var sink = new LogService(_logPath)
        {
            FileLevel = sinkLevel,
            ConsoleLevel = YaguLogLevel.None,
        };
        var logger = new LogServiceLogger("category", sink);

        Assert.Equal(expected, logger.IsEnabled((MsLogLevel)microsoftLevel));
    }

    [Theory]
    [InlineData((int)MsLogLevel.Trace, "VRB")]
    [InlineData((int)MsLogLevel.Debug, "VRB")]
    [InlineData((int)MsLogLevel.Information, "INF")]
    [InlineData((int)MsLogLevel.Warning, "WRN")]
    [InlineData((int)MsLogLevel.Error, "WRN")]
    [InlineData((int)MsLogLevel.Critical, "CRT")]
    [InlineData(999, "VRB")]
    public void Log_MapsLevelAndPreservesCategoryMessageAndException(int microsoftLevel, string prefix)
    {
        using var sink = new LogService(_logPath)
        {
            FileLevel = YaguLogLevel.Verbose,
            ConsoleLevel = YaguLogLevel.None,
        };
        var logger = new LogServiceLogger("structured", sink);
        var error = new InvalidOperationException("mapped-exception");

        logger.Log((MsLogLevel)microsoftLevel, new EventId(7), "state", error,
            static (state, _) => "rendered-" + state);
        sink.Flush();

        string content = File.ReadAllText(_logPath);
        Assert.Contains($"[{prefix}]", content);
        Assert.Contains("[structured]", content);
        Assert.Contains("rendered-state", content);
        Assert.Contains("mapped-exception", content);
    }

    [Fact]
    public void Log_None_DoesNotInvokeFormatterOrWrite()
    {
        using var sink = new LogService(_logPath)
        {
            FileLevel = YaguLogLevel.Verbose,
            ConsoleLevel = YaguLogLevel.None,
        };
        var logger = new LogServiceLogger("category", sink);
        bool formatted = false;

        logger.Log(MsLogLevel.None, default, "state", null, (state, error) =>
        {
            formatted = true;
            return state;
        });
        sink.Flush();

        Assert.False(formatted);
        Assert.False(File.Exists(_logPath));
    }

    [Fact]
    public void Log_DisabledLevel_DoesNotInvokeFormatterOrWrite()
    {
        using var sink = new LogService(_logPath)
        {
            FileLevel = YaguLogLevel.Warning,
            ConsoleLevel = YaguLogLevel.None,
        };
        var logger = new LogServiceLogger("category", sink);
        bool formatted = false;

        logger.Log(MsLogLevel.Information, default, "state", null, (state, error) =>
        {
            formatted = true;
            return state;
        });
        sink.Flush();

        Assert.False(formatted);
        Assert.False(File.Exists(_logPath));
    }

    [Fact]
    public void Log_NullFormatter_Throws()
    {
        using var sink = new LogService(_logPath);
        var logger = new LogServiceLogger("category", sink);

        Assert.Throws<ArgumentNullException>(() =>
            logger.Log<string>(MsLogLevel.Information, default, "state", null, null!));
    }

    [Fact]
    public void Critical_RaisesSinkEvent()
    {
        using var sink = new LogService(_logPath)
        {
            FileLevel = YaguLogLevel.None,
            ConsoleLevel = YaguLogLevel.Critical,
        };
        var logger = new LogServiceLogger("critical-source", sink);
        (string Source, string Message, Exception? Error)? observed = null;
        sink.CriticalLogged += (source, message, error) => observed = (source, message, error);
        var exception = new InvalidOperationException("critical-error");

        logger.Log(MsLogLevel.Critical, default, "state", exception,
            static (_, _) => "critical-message");

        Assert.NotNull(observed);
        Assert.Equal("critical-source", observed.Value.Source);
        Assert.Equal("critical-message", observed.Value.Message);
        Assert.Same(exception, observed.Value.Error);
    }

    [Fact]
    public void YaguLog_For_CachesByCategoryAndRejectsNull()
    {
        ILogger first = YaguLog.For("cache-category");
        ILogger second = YaguLog.For("cache-category");
        ILogger other = YaguLog.For("other-category");

        Assert.Same(first, second);
        Assert.NotSame(first, other);
        Assert.Throws<ArgumentNullException>(() => YaguLog.For(null!));
    }
}