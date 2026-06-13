using Yagu.Services;

namespace Yagu.Tests;

public class LowDiskSpaceMonitorTests
{
    [Theory]
    [InlineData(98, 0.98)]
    [InlineData(75, 0.75)]
    [InlineData(0, 0.98)]
    [InlineData(100, 0.99)]
    public void PercentToThreshold_NormalizesConfiguredPercent(int percent, double expectedThreshold)
    {
        Assert.Equal(expectedThreshold, LowDiskSpaceMonitor.PercentToThreshold(percent), precision: 4);
    }

    [Fact]
    public void IsOverThreshold_UsesUsedSpaceRatio()
    {
        var below = new DiskSpaceSnapshot(@"C:\", TotalBytes: 100, AvailableBytes: 3);
        var atThreshold = new DiskSpaceSnapshot(@"C:\", TotalBytes: 100, AvailableBytes: 2);
        var above = new DiskSpaceSnapshot(@"C:\", TotalBytes: 100, AvailableBytes: 1);

        Assert.False(LowDiskSpaceMonitor.IsOverThreshold(below, LowDiskSpaceMonitor.DefaultFullThreshold));
        Assert.False(LowDiskSpaceMonitor.IsOverThreshold(atThreshold, LowDiskSpaceMonitor.DefaultFullThreshold));
        Assert.True(LowDiskSpaceMonitor.IsOverThreshold(above, LowDiskSpaceMonitor.DefaultFullThreshold));
    }

    [Fact]
    public void BuildTerminationMessage_NamesTempDriveAndLowDiskSpace()
    {
        var snapshot = new DiskSpaceSnapshot(@"D:\", TotalBytes: 1_000, AvailableBytes: 10);

        var message = LowDiskSpaceMonitor.BuildTerminationMessage(snapshot);

        Assert.Contains("Search terminated due to low disk space", message);
        Assert.Contains("temp-file drive D:", message);
        Assert.Contains("99.0% full", message);
        Assert.Contains("choose another search result temp-file drive", message);
    }

    [Fact]
    public async Task StartAsync_InvokesCallbackWhenPathDriveIsTooFull()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var tempPath = Path.Combine(Path.GetTempPath(), "yagu-low-disk-test.tmp");
        var callbackSeen = new TaskCompletionSource<DiskSpaceSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);

        await LowDiskSpaceMonitor.StartAsync(
            tempPath,
            fullThreshold: 0.000001,
            checkInterval: TimeSpan.FromMilliseconds(10),
            onDiskTooFull: snapshot => callbackSeen.TrySetResult(snapshot),
            cts.Token);

        var snapshot = await callbackSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(snapshot.TotalBytes > 0);
        Assert.True(snapshot.AvailableBytes >= 0);
    }
}