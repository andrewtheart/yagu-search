using Yagu.Services.Telemetry;

namespace Yagu.Tests;

public class BugReportThrottleTests
{
    [Fact]
    public void ShouldOffer_FirstTime_IsTrue_ThenFalse()
    {
        var throttle = new BugReportThrottle();
        string sig = BugReportThrottle.Signature("Search", "System.InvalidOperationException", "boom");

        Assert.True(throttle.ShouldOffer(sig));
        Assert.False(throttle.ShouldOffer(sig));
        Assert.False(throttle.ShouldOffer(sig));
        Assert.Equal(1, throttle.OfferedCount);
    }

    [Fact]
    public void ShouldOffer_DistinctSignatures_EachOfferedOnce()
    {
        var throttle = new BugReportThrottle();
        string a = BugReportThrottle.Signature("Search", "TypeA", "m1");
        string b = BugReportThrottle.Signature("Preview", "TypeB", "m2");

        Assert.True(throttle.ShouldOffer(a));
        Assert.True(throttle.ShouldOffer(b));
        Assert.Equal(2, throttle.OfferedCount);
    }

    [Fact]
    public void ShouldOffer_RespectsMaxDistinctCeiling()
    {
        var throttle = new BugReportThrottle(maxDistinct: 2);

        Assert.True(throttle.ShouldOffer(BugReportThrottle.Signature("s", "t", "1")));
        Assert.True(throttle.ShouldOffer(BugReportThrottle.Signature("s", "t", "2")));
        // Ceiling reached: a brand-new signature is refused.
        Assert.False(throttle.ShouldOffer(BugReportThrottle.Signature("s", "t", "3")));
        Assert.Equal(2, throttle.OfferedCount);
    }

    [Fact]
    public void ShouldOffer_EmptySignature_IsFalse()
    {
        var throttle = new BugReportThrottle();
        Assert.False(throttle.ShouldOffer(string.Empty));
        Assert.False(throttle.ShouldOffer(null!));
        Assert.Equal(0, throttle.OfferedCount);
    }

    [Fact]
    public void Signature_IsStable_ForSameInputs()
    {
        string a = BugReportThrottle.Signature("Search", "TypeX", "same message");
        string b = BugReportThrottle.Signature("Search", "TypeX", "same message");
        Assert.Equal(a, b);
    }

    [Fact]
    public void Signature_Differs_WhenMessageDiffers()
    {
        string a = BugReportThrottle.Signature("Search", "TypeX", "message one");
        string b = BugReportThrottle.Signature("Search", "TypeX", "message two");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Signature_EmbedsSourceAndType_ForReadability()
    {
        string sig = BugReportThrottle.Signature("Search", "System.IOException", "x");
        Assert.StartsWith("Search:System.IOException:", sig);
    }
}
