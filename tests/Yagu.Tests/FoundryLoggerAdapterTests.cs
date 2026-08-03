using Microsoft.Extensions.Logging;
using Yagu.Services;
using Yagu.Services.Ai;
using MsLogLevel = Microsoft.Extensions.Logging.LogLevel;
using YaguLogLevel = Yagu.Services.LogLevel;

namespace Yagu.Tests;

[Collection("LogServiceSingleton")]
public sealed class FoundryLoggerAdapterTests : IDisposable
{
    private readonly LogService _sink = LogService.Instance;
    private readonly YaguLogLevel _originalFileLevel;
    private readonly YaguLogLevel _originalConsoleLevel;

    public FoundryLoggerAdapterTests()
    {
        _originalFileLevel = _sink.FileLevel;
        _originalConsoleLevel = _sink.ConsoleLevel;
        _sink.FileLevel = YaguLogLevel.Verbose;
        _sink.ConsoleLevel = YaguLogLevel.None;
    }

    public void Dispose()
    {
        _sink.Flush();
        _sink.FileLevel = _originalFileLevel;
        _sink.ConsoleLevel = _originalConsoleLevel;
    }

    [Fact]
    public void BeginScope_ReturnsReusableNoopScope()
    {
        using var first = FoundryLoggerAdapter.Instance.BeginScope("first");
        using var second = FoundryLoggerAdapter.Instance.BeginScope("second");

        Assert.Same(first, second);
    }

    [Fact]
    public void IsEnabled_UsesSinkThresholdsAndAlwaysAllowsWarningsOrHigher()
    {
        _sink.FileLevel = YaguLogLevel.None;
        _sink.ConsoleLevel = YaguLogLevel.None;

        Assert.False(FoundryLoggerAdapter.Instance.IsEnabled(MsLogLevel.None));
        Assert.False(FoundryLoggerAdapter.Instance.IsEnabled(MsLogLevel.Trace));
        Assert.False(FoundryLoggerAdapter.Instance.IsEnabled(MsLogLevel.Debug));
        Assert.False(FoundryLoggerAdapter.Instance.IsEnabled(MsLogLevel.Information));
        Assert.True(FoundryLoggerAdapter.Instance.IsEnabled(MsLogLevel.Warning));
        Assert.True(FoundryLoggerAdapter.Instance.IsEnabled(MsLogLevel.Error));
        Assert.True(FoundryLoggerAdapter.Instance.IsEnabled(MsLogLevel.Critical));

        _sink.FileLevel = YaguLogLevel.Info;
        Assert.True(FoundryLoggerAdapter.Instance.IsEnabled(MsLogLevel.Information));

        _sink.FileLevel = YaguLogLevel.Verbose;
        Assert.True(FoundryLoggerAdapter.Instance.IsEnabled(MsLogLevel.Trace));
        Assert.True(FoundryLoggerAdapter.Instance.IsEnabled(MsLogLevel.Debug));
    }

    [Fact]
    public void Log_None_DoesNotInvokeFormatter()
    {
        bool formatted = false;

        FoundryLoggerAdapter.Instance.Log(MsLogLevel.None, default, "state", null, (state, error) =>
        {
            formatted = true;
            return state;
        });

        Assert.False(formatted);
    }

    [Fact]
    public void Log_EmptyMessageWithoutException_IsIgnored()
    {
        FoundryLoggerAdapter.Instance.Log(MsLogLevel.Warning, default, string.Empty, null,
            static (_, _) => string.Empty);

        _sink.Flush();
    }

    [Fact]
    public void Log_FormatterFailure_FallsBackToStateText()
    {
        string marker = "foundry-fallback-" + Guid.NewGuid().ToString("N");

        FoundryLoggerAdapter.Instance.Log(MsLogLevel.Information, default, marker, null,
            static (_, _) => throw new InvalidOperationException("formatter-failed"));
        _sink.Flush();

        Assert.Contains(marker, File.ReadAllText(_sink.LogFilePath));
    }

    [Fact]
    public void Log_FormatterFailureWithNullState_PreservesException()
    {
        string marker = "foundry-null-state-" + Guid.NewGuid().ToString("N");
        var exception = new InvalidOperationException(marker);

        FoundryLoggerAdapter.Instance.Log<string?>(MsLogLevel.Warning, default, null, exception,
            static (_, _) => throw new InvalidOperationException("formatter-failed"));
        _sink.Flush();

        Assert.Contains(marker, File.ReadAllText(_sink.LogFilePath));
    }

    [Theory]
    [InlineData((int)MsLogLevel.Trace, "VRB")]
    [InlineData((int)MsLogLevel.Debug, "VRB")]
    [InlineData((int)MsLogLevel.Information, "INF")]
    [InlineData((int)MsLogLevel.Warning, "WRN")]
    [InlineData((int)MsLogLevel.Error, "WRN")]
    [InlineData((int)MsLogLevel.Critical, "CRT")]
    [InlineData(999, "VRB")]
    public void Log_MapsEveryLevel(int microsoftLevel, string prefix)
    {
        string marker = "foundry-level-" + microsoftLevel + "-" + Guid.NewGuid().ToString("N");
        var exception = new InvalidOperationException("exception-" + marker);

        FoundryLoggerAdapter.Instance.Log((MsLogLevel)microsoftLevel, new EventId(4), marker, exception,
            static (state, _) => state);
        _sink.Flush();

        string content = File.ReadAllText(_sink.LogFilePath);
        Assert.Contains($"[{prefix}]", content);
        Assert.Contains("[Semantic.Foundry]", content);
        Assert.Contains(marker, content);
        if ((MsLogLevel)microsoftLevel != MsLogLevel.Information)
            Assert.Contains("exception-" + marker, content);
    }
}

[CollectionDefinition("LogServiceSingleton", DisableParallelization = true)]
public sealed class LogServiceSingletonCollection;