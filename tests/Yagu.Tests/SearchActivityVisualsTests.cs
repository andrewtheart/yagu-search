using Yagu.Helpers;

namespace Yagu.Tests;

/// <summary>
/// Unit tests for <see cref="SearchActivityVisuals"/> — the glyph/opacity/accessible-name state machine
/// and the figure-eight keyframe table behind the bottom-left search activity indicator.
/// </summary>
public sealed class SearchActivityVisualsTests
{
    [Fact]
    public void Resolve_Busy_ShowsTheAnimatedMagnifier()
    {
        SearchActivityVisual visual = SearchActivityVisuals.Resolve(busy: true, sawScan: false);

        Assert.Equal(SearchActivityVisuals.SearchGlyph, visual.Glyph);
        Assert.Equal(0.82, visual.Opacity);
        Assert.Equal("Search in progress", visual.AutomationName);
        Assert.True(visual.Animate);
    }

    [Fact]
    public void Resolve_BusyAfterAPreviousSearch_StillReportsInProgress()
    {
        // Busy wins over the sticky "saw a scan" flag, otherwise a re-run would announce "complete".
        SearchActivityVisual visual = SearchActivityVisuals.Resolve(busy: true, sawScan: true);

        Assert.Equal(SearchActivityVisuals.SearchGlyph, visual.Glyph);
        Assert.Equal("Search in progress", visual.AutomationName);
        Assert.True(visual.Animate);
    }

    [Fact]
    public void Resolve_IdleBeforeAnySearch_ShowsTheDimmedReadyMagnifier()
    {
        SearchActivityVisual visual = SearchActivityVisuals.Resolve(busy: false, sawScan: false);

        Assert.Equal(SearchActivityVisuals.SearchGlyph, visual.Glyph);
        Assert.Equal(0.72, visual.Opacity);
        Assert.Equal("Ready to search", visual.AutomationName);
        Assert.False(visual.Animate);
    }

    [Fact]
    public void Resolve_IdleAfterASearch_ShowsTheCompletionGlyphAtFullOpacity()
    {
        SearchActivityVisual visual = SearchActivityVisuals.Resolve(busy: false, sawScan: true);

        Assert.Equal(SearchActivityVisuals.CompletedGlyph, visual.Glyph);
        Assert.Equal(1.0, visual.Opacity);
        Assert.Equal("Search complete", visual.AutomationName);
        Assert.False(visual.Animate);
    }

    [Fact]
    public void Resolve_AnimatesOnlyWhileBusy()
    {
        Assert.True(SearchActivityVisuals.Resolve(busy: true, sawScan: false).Animate);
        Assert.True(SearchActivityVisuals.Resolve(busy: true, sawScan: true).Animate);
        Assert.False(SearchActivityVisuals.Resolve(busy: false, sawScan: false).Animate);
        Assert.False(SearchActivityVisuals.Resolve(busy: false, sawScan: true).Animate);
    }

    [Fact]
    public void Resolve_EveryIdleAndBusyStatePairs_AUniqueAccessibleName()
    {
        string[] names =
        [
            SearchActivityVisuals.Resolve(busy: true, sawScan: false).AutomationName,
            SearchActivityVisuals.Resolve(busy: false, sawScan: false).AutomationName,
            SearchActivityVisuals.Resolve(busy: false, sawScan: true).AutomationName,
        ];

        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
        Assert.All(names, name => Assert.False(string.IsNullOrWhiteSpace(name)));
    }

    [Fact]
    public void FigureEightKeyframes_AdvanceInStrictlyIncreasingTime()
    {
        var frames = SearchActivityVisuals.FigureEightKeyframes;

        Assert.NotEmpty(frames);
        for (int i = 1; i < frames.Count; i++)
            Assert.True(frames[i].Seconds > frames[i - 1].Seconds, $"frame {i} does not advance in time");
    }

    [Fact]
    public void FigureEightKeyframes_StartAndEndAtRest_SoTheLoopRepeatsSeamlessly()
    {
        var frames = SearchActivityVisuals.FigureEightKeyframes;

        Assert.Equal((0.0, 0.0, 0.0), frames[0]);
        Assert.Equal((0.0, 0.0), (frames[^1].X, frames[^1].Y));
    }

    [Fact]
    public void FigureEightKeyframes_CrossTheOriginAtTheMidpoint()
    {
        var frames = SearchActivityVisuals.FigureEightKeyframes;
        double midpoint = frames[^1].Seconds / 2;

        (double Seconds, double X, double Y) middle = frames.Single(f => f.Seconds == midpoint);

        Assert.Equal((0.0, 0.0), (middle.X, middle.Y));
    }

    [Fact]
    public void FigureEightKeyframes_LoopRightThenLeft()
    {
        var frames = SearchActivityVisuals.FigureEightKeyframes;
        double midpoint = frames[^1].Seconds / 2;

        Assert.All(frames.Where(f => f.Seconds > 0 && f.Seconds < midpoint), f => Assert.True(f.X > 0, "first lobe must stay right of centre"));
        Assert.All(frames.Where(f => f.Seconds > midpoint && f.Seconds < frames[^1].Seconds), f => Assert.True(f.X < 0, "second lobe must stay left of centre"));
    }

    [Fact]
    public void FigureEightKeyframes_SecondLobeMirrorsTheFirstThroughTheOrigin()
    {
        var frames = SearchActivityVisuals.FigureEightKeyframes;
        double midpoint = frames[^1].Seconds / 2;

        foreach ((double Seconds, double X, double Y) frame in frames.Where(f => f.Seconds < midpoint))
        {
            (double Seconds, double X, double Y) mirrored = frames.Single(f => Math.Abs(f.Seconds - (frame.Seconds + midpoint)) < 1e-9);

            Assert.Equal(-frame.X, mirrored.X, 9);
            Assert.Equal(-frame.Y, mirrored.Y, 9);
        }
    }

    [Fact]
    public void FigureEightKeyframes_StayInsideTheTwentySixPixelIndicatorCell()
    {
        // The XAML cell is 26 px wide and 16 px tall around a centred 12 px glyph, so travel beyond
        // 7 px horizontally (or 2 px vertically) would push the glyph into the neighbouring status text.
        var frames = SearchActivityVisuals.FigureEightKeyframes;

        Assert.Equal(5.5, frames.Max(f => Math.Abs(f.X)));
        Assert.Equal(2.2, frames.Max(f => Math.Abs(f.Y)));
        Assert.All(frames, f => Assert.True(Math.Abs(f.X) <= 7.0 && Math.Abs(f.Y) <= 2.5));
    }

    [Fact]
    public void FigureEightKeyframes_UseTheSameGlyphAsTheIdleIndicator()
    {
        // The animation translates the resting magnifier; it never swaps in the completion glyph.
        Assert.NotEqual(SearchActivityVisuals.SearchGlyph, SearchActivityVisuals.CompletedGlyph);
        Assert.Equal(SearchActivityVisuals.SearchGlyph, SearchActivityVisuals.Resolve(busy: true, sawScan: true).Glyph);
    }
}
