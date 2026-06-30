using Yagu.Services.Telemetry;

namespace Yagu.Tests;

public class TelemetryScrubberTests
{
    [Fact]
    public void Scrub_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, TelemetryScrubber.Scrub(null));
        Assert.Equal(string.Empty, TelemetryScrubber.Scrub(string.Empty));
    }

    [Fact]
    public void Scrub_DriveLetterPath_RedactsButKeepsExtension()
    {
        string result = TelemetryScrubber.Scrub(@"Could not open C:\Users\jane\secret\report.txt for reading");
        Assert.DoesNotContain("jane", result);
        Assert.DoesNotContain("secret", result);
        Assert.Contains("<path>.txt", result);
        Assert.Contains("Could not open", result);
        Assert.Contains("for reading", result);
    }

    [Fact]
    public void Scrub_PathWithoutExtension_RedactsToPlaceholder()
    {
        string result = TelemetryScrubber.Scrub(@"Access denied to D:\Projects\Yagu\bin");
        Assert.Contains("<path>", result);
        Assert.DoesNotContain("Projects", result);
        Assert.DoesNotContain("Yagu", result);
    }

    [Fact]
    public void Scrub_UncPath_IsRedacted()
    {
        string result = TelemetryScrubber.Scrub(@"Timeout reading \\fileserver\share\data\q.log now");
        Assert.DoesNotContain("fileserver", result);
        Assert.DoesNotContain("share", result);
        Assert.Contains("<path>.log", result);
        Assert.Contains("Timeout reading", result);
    }

    [Fact]
    public void Scrub_ForwardSlashPath_IsRedacted()
    {
        string result = TelemetryScrubber.Scrub("opened C:/temp/cache/file.json");
        Assert.DoesNotContain("temp", result);
        Assert.Contains("<path>.json", result);
    }

    [Fact]
    public void Scrub_TextWithoutPaths_IsUnchanged()
    {
        const string text = "An index out of range occurred while sorting results.";
        Assert.Equal(text, TelemetryScrubber.Scrub(text));
    }

    [Fact]
    public void Describe_NullException_ReturnsEmptySummary()
    {
        TelemetryScrubber.ScrubbedException summary = TelemetryScrubber.Describe(null);
        Assert.Equal(string.Empty, summary.Type);
        Assert.Equal(string.Empty, summary.Message);
        Assert.Equal(string.Empty, summary.StackTrace);
    }

    [Fact]
    public void Describe_IncludesTypeChain_AndScrubsMessage()
    {
        var inner = new InvalidOperationException(@"bad state at C:\Users\bob\app\data.db");
        var outer = new ApplicationException("wrapper failure", inner);

        TelemetryScrubber.ScrubbedException summary = TelemetryScrubber.Describe(outer);

        Assert.Contains("ApplicationException", summary.Type);
        Assert.Contains("InvalidOperationException", summary.Type);
        Assert.Contains("->", summary.Type);
        Assert.Contains("wrapper failure", summary.Message);
        Assert.DoesNotContain("bob", summary.Message);
    }
}
