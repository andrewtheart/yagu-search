using Yagu.Models;

namespace Yagu.Tests;

public sealed class SearchProgressTests
{
    [Fact]
    public void DisplayTracker_DoesNotMoveBackwardWhenEstimatedTotalGrows()
    {
        var tracker = new SearchProgressDisplayTracker();

        Assert.True(tracker.Update(filesProcessed: 80, totalFiles: 100, indeterminate: false));
        Assert.Equal(80, tracker.Percent);

        Assert.False(tracker.Update(filesProcessed: 90, totalFiles: 150, indeterminate: false));
        Assert.Equal(80, tracker.Percent);

        Assert.True(tracker.Update(filesProcessed: 135, totalFiles: 150, indeterminate: false));
        Assert.Equal(90, tracker.Percent);
    }

    [Fact]
    public void DisplayTracker_IgnoresIndeterminatePhaseAndResetsBetweenSearches()
    {
        var tracker = new SearchProgressDisplayTracker();

        // A backend without an upfront count reports processed/seen-so-far during discovery. Even 9/10
        // says nothing about whole-search completion and must not seed a visible 90% high-water mark.
        Assert.False(tracker.Update(filesProcessed: 9, totalFiles: 10, indeterminate: true));
        Assert.Equal(0, tracker.Percent);

        Assert.True(tracker.Update(filesProcessed: 100, totalFiles: 1_000, indeterminate: false));
        Assert.Equal(10, tracker.Percent);
        Assert.True(tracker.Reset());
        Assert.Equal(0, tracker.Percent);
        Assert.False(tracker.Reset());
    }
}