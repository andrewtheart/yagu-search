using Yagu.Services;

namespace Yagu.Tests;

public sealed class ExcludedMethodTests_SearchService
{
    [Fact]
    public void CollectForMemoryPressureIfDue_DoesNotThrow()
    {
        // Just exercise the method - it calls GC.Collect internally
        SearchService.CollectForMemoryPressureIfDue(TimeSpan.FromHours(1));
    }

    [Fact]
    public void IsMemoryPressureHigh_ReturnsBoolean()
    {
        // Exercise with no cap — should not throw
        bool result = SearchService.IsMemoryPressureHigh(0, 0);
        // With a zero cap and zero pressure%, the default path returns false
        // (effectiveCap becomes long.MaxValue so WS < cap)
        Assert.False(result);
    }

    [Fact]
    public void IsMemoryPressureHigh_WithTinyCap_ReturnsTrue()
    {
        // 1 byte cap should always be exceeded
        bool result = SearchService.IsMemoryPressureHigh(1, 0);
        Assert.True(result);
    }

    [Fact]
    public void IsMemoryPressureRelieved_ReturnsBoolean()
    {
        // With no cap, pressure is always relieved
        bool result = SearchService.IsMemoryPressureRelieved(0, 0);
        Assert.True(result);
    }

    [Fact]
    public void TryGetSystemMemoryLoadPercent_ReturnsResult()
    {
        bool success = SearchService.TryGetSystemMemoryLoadPercent(out uint load);
        // On Windows, this should succeed
        Assert.True(success);
        Assert.InRange(load, 1u, 100u);
    }

    [Fact]
    public void GetMemoryDiagnostics_ReturnsNonEmptyString()
    {
        string diag = SearchService.GetMemoryDiagnostics();
        Assert.False(string.IsNullOrEmpty(diag));
        Assert.Contains("MB", diag);
    }

    [Fact]
    public void AutoProcessMemoryCap_ReturnsPositiveValue()
    {
        long cap = SearchService.AutoProcessMemoryCap();
        Assert.True(cap >= 2L * 1024 * 1024 * 1024); // at least 2 GB
    }
}
