using Yagu.Helpers;

namespace Yagu.Tests;

using Drawer = Yagu.Helpers.ContinuationDrawerPrefocus.DrawerExtent;

/// <summary>
/// Look-ahead focusing of the preview panel's continuation drawers: a long file's preview rolls over
/// into sibling "continued" drawers, and the next one must already carry the "selected" background by
/// the time it scrolls into view.
/// </summary>
public sealed class ContinuationDrawerPrefocusTests
{
    private const double ViewportTop = 1000;
    private const double ViewportHeight = 500;
    private const double LookAhead = 1.0; // one viewport of look-ahead => probe reaches 2000 below

    private static int Select(IReadOnlyList<Drawer> siblings, bool scrollingDown = true, double viewportTop = ViewportTop)
        => ContinuationDrawerPrefocus.SelectDrawer(siblings, viewportTop, ViewportHeight, scrollingDown, LookAhead);

    [Fact]
    public void FocusesTheNextDrawerBeforeItEntersTheViewport()
    {
        // Drawer starts 300px below the viewport bottom (1500) — not visible yet, but inside the band.
        Assert.Equal(0, Select([new Drawer(1800, 4000)]));
    }

    [Fact]
    public void IgnoresADrawerStillBeyondTheLookAheadBand()
    {
        // Probe reaches 1500 + 500 = 2000; this drawer starts past it.
        Assert.Equal(-1, Select([new Drawer(2400, 4000)]));
    }

    [Fact]
    public void FocusesADrawerAlreadyPartlyVisible()
        => Assert.Equal(0, Select([new Drawer(1400, 3000)]));

    [Fact]
    public void SkipsDrawersAFastFlickAlreadyCarriedPast()
    {
        // The first two drawers ended above the viewport top; the third is the one being approached.
        Assert.Equal(2, Select([new Drawer(200, 400), new Drawer(400, 900), new Drawer(900, 3000)]));
    }

    [Fact]
    public void ScrollingUpFocusesTheDrawerAboveTheViewport()
    {
        // Probe reaches 1000 - 500 = 500; this drawer's bottom is inside the band.
        Assert.Equal(0, Select([new Drawer(100, 800)], scrollingDown: false));
    }

    [Fact]
    public void ScrollingUpIgnoresADrawerStillFarAbove()
        => Assert.Equal(-1, Select([new Drawer(0, 200)], scrollingDown: false));

    [Fact]
    public void ScrollingUpSkipsDrawersAlreadyBelowTheViewport()
        => Assert.Equal(1, Select([new Drawer(1600, 2000), new Drawer(700, 1400)], scrollingDown: false));

    [Fact]
    public void StopsAtAnUnmeasuredDrawerRatherThanGuessingItsPosition()
        => Assert.Equal(-1, Select([new Drawer(0, 0), new Drawer(1800, 4000)]));

    [Fact]
    public void ReturnsNoDrawerWhenThereAreNoSiblingsOrNoViewport()
    {
        Assert.Equal(-1, Select([]));
        Assert.Equal(-1, ContinuationDrawerPrefocus.SelectDrawer([new Drawer(1800, 4000)], ViewportTop, 0, true, LookAhead));
    }

    [Fact]
    public void ZeroLookAheadStillFocusesADrawerTouchingTheViewport()
    {
        Assert.Equal(0, ContinuationDrawerPrefocus.SelectDrawer([new Drawer(1400, 3000)], ViewportTop, ViewportHeight, true, 0));
        Assert.Equal(-1, ContinuationDrawerPrefocus.SelectDrawer([new Drawer(1600, 3000)], ViewportTop, ViewportHeight, true, 0));
    }

    // ── WinUI wiring (source-pinned: MainWindow is not compiled into this assembly) ──

    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string PreviewSectionsSource = File.ReadAllText(
        Path.Combine(RepoRoot, "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.PreviewSections.cs"));
    private static readonly string MatchNavSource = File.ReadAllText(
        Path.Combine(RepoRoot, "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.MatchNav.cs"));

    [Fact]
    public void PreviewScrollRunsThePrefocusPassBeforeTheAutoLoadPasses()
    {
        int scrollHandler = PreviewSectionsSource.IndexOf("private void OnPreviewScrollViewChanged", StringComparison.Ordinal);
        Assert.True(scrollHandler >= 0, "OnPreviewScrollViewChanged not found.");
        string handler = PreviewSectionsSource[scrollHandler..(scrollHandler + 800)];
        Assert.Contains("TryPrefocusApproachingContinuationDrawer();", handler);
    }

    [Fact]
    public void PrefocusNeverCrossesFilesOrFightsMatchNavigation()
    {
        int start = PreviewSectionsSource.IndexOf("private void TryPrefocusApproachingContinuationDrawer", StringComparison.Ordinal);
        Assert.True(start >= 0, "TryPrefocusApproachingContinuationDrawer not found.");
        string method = PreviewSectionsSource[start..(start + 3200)];

        // A programmatic match-navigation scroll already activated its target drawer.
        Assert.Contains("IsOverflowAutoLoadSuppressedForMatchNavigation() && !IsPreviewManualScrollActive()", method);
        // Walking stops at the first sibling belonging to a different file.
        Assert.Contains("!string.Equals(siblingPath, filePath, StringComparison.OrdinalIgnoreCase)", method);
        Assert.Contains("ContinuationDrawerPrefocus.SelectDrawer(", method);
        Assert.Contains("_prefocusedContinuationExpander = candidate;", method);
        Assert.Contains("HighlightActiveExpander();", method);

        // Explicit activation (click, match navigation, scroll-to-section) always wins over the pre-focus.
        int activate = MatchNavSource.IndexOf("private void ActivateSectionForBlock", StringComparison.Ordinal);
        Assert.True(activate >= 0, "ActivateSectionForBlock not found.");
        Assert.Contains("ClearContinuationDrawerPrefocus();", MatchNavSource[activate..(activate + 500)]);
    }

    [Fact]
    public void ANewContinuationDrawerInheritsTheFocusOfTheDrawerItContinues()
    {
        // The drawer is created at the instant the user reaches the end of the previous one, so the
        // look-ahead cannot run first — without this it is painted unselected for a frame.
        int start = PreviewSectionsSource.IndexOf("PreviewSectionsPanel.Children.Insert(index + 1, continuationExpander);", StringComparison.Ordinal);
        Assert.True(start >= 0, "Continuation drawer insertion not found.");
        Assert.Contains(
            "ApplyPreviewSectionContentBackground(\r\n                continuationExpander, IsPreviewSectionFocused(currentExpander, _activeSectionNav?.Block));",
            PreviewSectionsSource[start..(start + 700)]);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Yagu.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Cannot find repo root (Yagu.slnx)");
    }
}
