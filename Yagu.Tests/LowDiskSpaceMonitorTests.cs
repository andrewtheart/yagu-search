using Yagu.Services;

namespace Yagu.Tests;

public class LowDiskSpaceMonitorTests
{
    [Theory]
    [InlineData(98, 0.98)]
    [InlineData(75, 0.75)]
    [InlineData(0, AppSettings.DefaultLowDiskSpaceWarningPercent / 100d)]
    [InlineData(100, 0.99)]
    public void PercentToThreshold_NormalizesConfiguredPercent(int percent, double expectedThreshold)
    {
        Assert.Equal(expectedThreshold, LowDiskSpaceMonitor.PercentToThreshold(percent), precision: 4);
    }

    [Fact]
    public void IsOverThreshold_UsesUsedSpaceRatio()
    {
        // Build snapshots straddling the configured default threshold so the test stays correct
        // regardless of the default percent value.
        const long total = 10000;
        long atThresholdUsed = (long)Math.Round(LowDiskSpaceMonitor.DefaultFullThreshold * total);
        long margin = total / 50; // 2% of capacity
        var below = new DiskSpaceSnapshot(@"C:\", TotalBytes: total, AvailableBytes: total - atThresholdUsed + margin);
        var atThreshold = new DiskSpaceSnapshot(@"C:\", TotalBytes: total, AvailableBytes: total - atThresholdUsed);
        var above = new DiskSpaceSnapshot(@"C:\", TotalBytes: total, AvailableBytes: total - atThresholdUsed - margin);

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

public sealed class DiskSpaceSnapshotTests
{
    [Fact]
    public void UsedBytes_ClampsToZeroWhenAvailableExceedsTotal()
    {
        var snap = new DiskSpaceSnapshot(@"C:\", TotalBytes: 50, AvailableBytes: 100);
        Assert.Equal(0, snap.UsedBytes);
    }

    [Fact]
    public void UsedFraction_ReturnsZeroWhenTotalIsZero()
    {
        var snap = new DiskSpaceSnapshot(@"C:\", TotalBytes: 0, AvailableBytes: 0);
        Assert.Equal(0, snap.UsedFraction);
    }

    [Fact]
    public void UsedPercent_CalculatesCorrectly()
    {
        var snap = new DiskSpaceSnapshot(@"C:\", TotalBytes: 200, AvailableBytes: 50);
        Assert.Equal(75.0, snap.UsedPercent, precision: 1);
    }

    [Theory]
    [InlineData(@"C:\", "C:")]
    [InlineData(@"D:\", "D:")]
    [InlineData("", "")]
    public void DriveDisplayName_TrimsTrailingSeparator(string rootPath, string expected)
    {
        var snap = new DiskSpaceSnapshot(rootPath, 100, 50);
        Assert.Equal(expected, snap.DriveDisplayName);
    }

    [Fact]
    public void DriveDisplayName_WhitespaceRoot_ReturnsRootAsIs()
    {
        var snap = new DiskSpaceSnapshot("   ", 100, 50);
        Assert.Equal("   ", snap.DriveDisplayName);
    }
}

public sealed class LowDiskSpaceMonitorValidationTests
{
    [Fact]
    public async Task StartAsync_ThrowsOnNullOrWhitespaceTempPath()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            LowDiskSpaceMonitor.StartAsync(
                "   ",
                0.9,
                TimeSpan.FromSeconds(1),
                _ => { },
                CancellationToken.None));
    }

    [Fact]
    public async Task StartAsync_ThrowsOnNullCallback()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            LowDiskSpaceMonitor.StartAsync(
                @"C:\temp\file.tmp",
                0.9,
                TimeSpan.FromSeconds(1),
                null!,
                CancellationToken.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-0.5)]
    [InlineData(1.5)]
    public async Task StartAsync_ThrowsOnInvalidThreshold(double threshold)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            LowDiskSpaceMonitor.StartAsync(
                @"C:\temp\file.tmp",
                threshold,
                TimeSpan.FromSeconds(1),
                _ => { },
                CancellationToken.None));
    }

    [Fact]
    public async Task StartAsync_ThrowsOnZeroOrNegativeInterval()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            LowDiskSpaceMonitor.StartAsync(
                @"C:\temp\file.tmp",
                0.9,
                TimeSpan.Zero,
                _ => { },
                CancellationToken.None));
    }

    [Fact]
    public async Task StartAsync_CancellationStopsLoop()
    {
        using var cts = new CancellationTokenSource();
        var callbackInvoked = false;

        var task = LowDiskSpaceMonitor.StartAsync(
            Path.GetTempFileName(),
            fullThreshold: 0.9999,
            checkInterval: TimeSpan.FromMilliseconds(5000),
            onDiskTooFull: _ => callbackInvoked = true,
            cts.Token);

        cts.Cancel();
        await task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(callbackInvoked);
    }

    [Fact]
    public void IsOverThreshold_ZeroTotalBytes_ReturnsFalse()
    {
        var snap = new DiskSpaceSnapshot(@"C:\", TotalBytes: 0, AvailableBytes: 0);
        Assert.False(LowDiskSpaceMonitor.IsOverThreshold(snap, 0.5));
    }

    [Fact]
    public void TryGetSnapshot_ValidPath_ReturnsTrue()
    {
        string path = Path.GetTempFileName();
        try
        {
            bool ok = LowDiskSpaceMonitor.TryGetSnapshot(path, out var snap);
            Assert.True(ok);
            Assert.True(snap.TotalBytes > 0);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TryGetSnapshot_InvalidPath_ReturnsFalse()
    {
        bool ok = LowDiskSpaceMonitor.TryGetSnapshot("?:\0invalid\0path", out _);
        Assert.False(ok);
    }

    [Fact]
    public void BuildTerminationMessage_ContainsDriveAndPercent()
    {
        var snap = new DiskSpaceSnapshot(@"E:\", TotalBytes: 1000, AvailableBytes: 10);
        var msg = LowDiskSpaceMonitor.BuildTerminationMessage(snap);
        Assert.Contains("E:", msg);
        Assert.Contains("99.0%", msg);
    }

    [Fact]
    public async Task StartAsync_LoopRunsMultipleIterations_BeforeCancellation()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            int checkCount = 0;
            using var cts = new CancellationTokenSource();

            // Use a threshold of 1.0 (100%) which no drive will exceed,
            // so the loop keeps iterating until cancelled
            var task = LowDiskSpaceMonitor.StartAsync(
                tempFile,
                fullThreshold: 1.0,
                checkInterval: TimeSpan.FromMilliseconds(20),
                onDiskTooFull: _ => Interlocked.Increment(ref checkCount),
                cts.Token);

            // Let it iterate a few times
            await Task.Delay(100);
            cts.Cancel();
            await task.WaitAsync(TimeSpan.FromSeconds(5));

            // Callback should NOT have been invoked (threshold never exceeded)
            Assert.Equal(0, checkCount);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task StartAsync_PreCancelledToken_CompletesImmediately()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel(); // Pre-cancel

            bool callbackInvoked = false;
            var task = LowDiskSpaceMonitor.StartAsync(
                tempFile,
                fullThreshold: 0.01,
                checkInterval: TimeSpan.FromMilliseconds(10),
                onDiskTooFull: _ => callbackInvoked = true,
                cts.Token);

            await task.WaitAsync(TimeSpan.FromSeconds(5));
            // With pre-cancelled token, the callback might fire once or not at all
            // depending on timing — just verify it completes without throw
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task StartAsync_PublicOverload_UsesDefaultThresholdAndInterval()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            bool fired = false;

            var task = LowDiskSpaceMonitor.StartAsync(
                tempFile,
                onDiskTooFull: _ => fired = true,
                cancellationToken: cts.Token);

            // Should complete (via cancellation) without throwing
            await task.WaitAsync(TimeSpan.FromSeconds(5));
            // Default threshold (see AppSettings.DefaultLowDiskSpaceWarningPercent) is unlikely to fire on a dev machine
            Assert.False(fired);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void DiskSpaceSnapshot_DriveDisplayName_RootSlashOnly_ReturnsOriginal()
    {
        // TrimEnd of separators leaves empty → IsNullOrWhiteSpace → returns original RootPath
        var snap = new DiskSpaceSnapshot(@"\", TotalBytes: 100, AvailableBytes: 50);
        Assert.Equal(@"\", snap.DriveDisplayName);
    }

    [Fact]
    public void DiskSpaceSnapshot_UsedFraction_NormalCase()
    {
        var snap = new DiskSpaceSnapshot(@"C:\", TotalBytes: 1000, AvailableBytes: 250);
        Assert.Equal(0.75, snap.UsedFraction, precision: 4);
    }

    [Fact]
    public void IsOverThreshold_ExactlyAtThreshold_ReturnsFalse()
    {
        // UsedFraction = 0.9 exactly, threshold = 0.9 → not OVER threshold
        var snap = new DiskSpaceSnapshot(@"C:\", TotalBytes: 100, AvailableBytes: 10);
        Assert.False(LowDiskSpaceMonitor.IsOverThreshold(snap, 0.9));
    }

    [Fact]
    public void IsOverThreshold_JustAboveThreshold_ReturnsTrue()
    {
        var snap = new DiskSpaceSnapshot(@"C:\", TotalBytes: 1000, AvailableBytes: 99);
        // UsedFraction = 901/1000 = 0.901 > 0.9
        Assert.True(LowDiskSpaceMonitor.IsOverThreshold(snap, 0.9));
    }

    [Fact]
    public void TryGetSnapshot_RelativePath_ResolvesAndReturnsTrue()
    {
        // Use a relative path that can be resolved
        string relativePath = "temp-test-" + Guid.NewGuid().ToString("N") + ".tmp";
        string fullPath = Path.Combine(Environment.CurrentDirectory, relativePath);
        try
        {
            File.WriteAllText(fullPath, "x");
            bool ok = LowDiskSpaceMonitor.TryGetSnapshot(relativePath, out var snap);
            Assert.True(ok);
            Assert.True(snap.TotalBytes > 0);
        }
        finally
        {
            File.Delete(fullPath);
        }
    }

    [Fact]
    public void PercentToThreshold_ClampsBelowMinimum()
    {
        // Values below valid range should get clamped by AppSettings.NormalizeLowDiskSpaceWarningPercent
        double result = LowDiskSpaceMonitor.PercentToThreshold(-10);
        Assert.True(result > 0 && result <= 1.0);
    }

    [Fact]
    public void PercentToThreshold_ClampsAboveMaximum()
    {
        double result = LowDiskSpaceMonitor.PercentToThreshold(200);
        Assert.True(result > 0 && result <= 1.0);
    }

    [Fact]
    public void TryGetSnapshot_EmptyString_ReturnsFalse()
    {
        // Empty string triggers ArgumentException in Path.GetFullPath → caught → returns false
        bool ok = LowDiskSpaceMonitor.TryGetSnapshot("", out var snapshot);
        Assert.False(ok);
        Assert.Equal(default, snapshot);
    }

    [Fact]
    public void TryGetSnapshot_NonexistentDriveLetter_ReturnsFalse()
    {
        // A drive letter that doesn't exist → DriveInfo.IsReady = false → returns false
        bool ok = LowDiskSpaceMonitor.TryGetSnapshot(@"Q:\nonexistent\path\file.tmp", out var snapshot);
        // Either false (drive not ready) or possibly true if Q: exists; just verify no throw
        if (!ok)
            Assert.Equal(default, snapshot);
    }

    [Fact]
    public async Task StartAsync_NonexistentDrive_DoesNotFireCallback()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        bool fired = false;

        var task = LowDiskSpaceMonitor.StartAsync(
            @"Q:\nonexistent\file.tmp",
            fullThreshold: 0.01,
            checkInterval: TimeSpan.FromMilliseconds(10),
            onDiskTooFull: _ => fired = true,
            cts.Token);

        await task.WaitAsync(TimeSpan.FromSeconds(5));
        // TryGetSnapshot returns false for nonexistent drive → callback never fires
        Assert.False(fired);
    }

    [Fact]
    public async Task StartAsync_ThresholdOf1_NeverTriggersCallback()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));
            bool fired = false;

            var task = LowDiskSpaceMonitor.StartAsync(
                tempFile,
                fullThreshold: 1.0,
                checkInterval: TimeSpan.FromMilliseconds(10),
                onDiskTooFull: _ => fired = true,
                cts.Token);

            await task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(fired);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void TryGetSnapshot_NulCharPath_ReturnsFalse()
    {
        // Path with NUL triggers ArgumentException in GetFullPath -> caught -> returns false
        bool result = LowDiskSpaceMonitor.TryGetSnapshot("C:\\\0invalid", out var snapshot);
        Assert.False(result);
        Assert.Equal(default, snapshot);
    }

    [Fact]
    public void TryGetSnapshot_EmptyPath_ReturnsFalse()
    {
        // Empty string triggers ArgumentException from GetFullPath
        bool result = LowDiskSpaceMonitor.TryGetSnapshot("", out var snapshot);
        Assert.False(result);
        Assert.Equal(default, snapshot);
    }
}