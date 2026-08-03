using Yagu.Helpers;
using Xunit;

namespace Yagu.Tests;

public sealed class SystemIdleDetectorTests
{
    [Theory]
    [InlineData(null, 5, false)]
    [InlineData(4, 5, false)]
    [InlineData(5, 5, true)]
    [InlineData(60, 5, true)]
    public void HasBeenIdleFor_UsesConfiguredThreshold(int? idleMinutes, int requiredMinutes, bool expected)
    {
        TimeSpan? idle = idleMinutes is { } value ? TimeSpan.FromMinutes(value) : null;
        Assert.Equal(expected, SystemIdleDetector.HasBeenIdleFor(idle, TimeSpan.FromMinutes(requiredMinutes)));
    }

    [Fact]
    public void TryGetIdleTime_NeverThrows_AndIsNonNegativeWhenAvailable()
    {
        TimeSpan? idle = SystemIdleDetector.TryGetIdleTime();
        Assert.True(idle is null || idle.Value >= TimeSpan.Zero);
    }
}
