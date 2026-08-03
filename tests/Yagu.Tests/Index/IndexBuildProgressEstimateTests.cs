using System.IO;
using System.Linq;
using Yagu.Services.Index;

namespace Yagu.Tests.Index;

/// <summary>Unit tests for the content-index build progress estimate (bytes crawled vs. drive used bytes).</summary>
public sealed class IndexBuildProgressEstimateTests
{
    [Theory]
    [InlineData(0, 1000, 0)]
    [InlineData(500, 1000, 50)]
    [InlineData(250, 1000, 25)]
    [InlineData(999, 1000, 99)]      // 99.9% floors to 99
    [InlineData(1000, 1000, 99)]     // 100% is capped to 99 (100 is only shown at completion)
    [InlineData(5000, 1000, 99)]     // over-run (subfolder finished, or estimate low) stays clamped
    public void Percent_ScalesBytesAgainstDriveUsage_AndCapsAt99(long crawled, long used, int expected)
        => Assert.Equal(expected, IndexBuildProgressEstimate.Percent(crawled, used));

    [Theory]
    [InlineData(500, 0)]        // no usable denominator
    [InlineData(500, -1)]       // negative denominator
    [InlineData(-1, 1000)]      // negative crawled
    public void Percent_ReturnsUnknownWhenInputsAreUnusable(long crawled, long used)
        => Assert.Equal(-1, IndexBuildProgressEstimate.Percent(crawled, used));

    [Fact]
    public void Percent_IsMonotonicAsCrawlProgresses()
    {
        int previous = -1;
        for (long crawled = 0; crawled <= 1000; crawled += 100)
        {
            int pct = IndexBuildProgressEstimate.Percent(crawled, 1000);
            Assert.True(pct >= previous, $"Percent must not decrease as bytes crawled grows (was {previous}, now {pct}).");
            previous = pct;
        }
    }

    [Fact]
    public void DriveUsedBytes_ReturnsUnknownForBlankOrNullRoot()
    {
        Assert.Equal(-1, IndexBuildProgressEstimate.DriveUsedBytes(null));
        Assert.Equal(-1, IndexBuildProgressEstimate.DriveUsedBytes(string.Empty));
    }

    [Fact]
    public void DriveUsedBytes_ReturnsPositiveForARealDrive()
    {
        // The drive hosting the test binary is real and ready, so it reports positive used bytes.
        long used = IndexBuildProgressEstimate.DriveUsedBytes(AppContext.BaseDirectory);
        Assert.True(used > 0, "A real, ready drive must report positive used bytes.");
    }

    [Fact]
    public void Percent_HugeCrawlThatOverflowsIntCast_StaysClampedInRange()
    {
        // A pathological crawl total makes ratio*100 overflow the int cast; the guards keep it in [0,99].
        int pct = IndexBuildProgressEstimate.Percent(long.MaxValue, 1);
        Assert.InRange(pct, 0, 99);
    }

    [Fact]
    public void DriveUsedBytes_UnreadyDrive_ReturnsUnknown()
    {
        char? unused = FindUnusedDriveLetter();
        Assert.True(unused is not null, "Expected at least one free drive letter on the test host.");
        Assert.Equal(-1, IndexBuildProgressEstimate.DriveUsedBytes($"{unused}:\\some\\path"));
    }

    [Fact]
    public void DriveUsedBytes_InvalidDriveName_ReturnsUnknown()
    {
        // A numeric "drive" is rejected by DriveInfo (or has no path root) → never throws out → -1.
        Assert.Equal(-1, IndexBuildProgressEstimate.DriveUsedBytes(@"1:\file"));
    }

    [Fact]
    public void DriveUsedBytes_DriveQueryFailure_ReturnsUnknown()
        => Assert.Equal(-1, IndexBuildProgressEstimate.DriveUsedBytes(
            @"C:\folder",
            static _ => throw new IOException("simulated drive query failure")));

    private static char? FindUnusedDriveLetter()
    {
        var used = DriveInfo.GetDrives()
            .Where(d => d.Name.Length > 0)
            .Select(d => char.ToUpperInvariant(d.Name[0]))
            .ToHashSet();
        for (char c = 'Z'; c >= 'D'; c--)
            if (!used.Contains(c))
                return c;
        return null;
    }
}
