using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Yagu.Helpers;

namespace Yagu;

public sealed partial class MainWindow
{
    private Storyboard? _searchActivityStoryboard;
    private bool _searchActivityStoryboardRunning;
    private bool _searchIndicatorSawScan;

    private void UpdateSearchActivityIndicator()
    {
        bool busy = ViewModel.IsSearching || ViewModel.IsTranslatingSemanticQuery || ViewModel.IsPreparingSearch;
        _searchIndicatorSawScan |= ViewModel.IsSearching;

        SearchActivityVisual visual = SearchActivityVisuals.Resolve(busy, _searchIndicatorSawScan);
        SearchActivityIcon.Glyph = visual.Glyph;
        SearchActivityIcon.Opacity = visual.Opacity;
        AutomationProperties.SetName(SearchActivityIndicator, visual.AutomationName);

        if (visual.Animate)
            StartSearchActivityAnimation();
        else
            StopSearchActivityAnimation();
    }

    private void StartSearchActivityAnimation()
    {
        if (_searchActivityStoryboardRunning)
            return;

        _searchActivityStoryboard ??= BuildSearchActivityStoryboard();
        SearchActivityTransform.X = 0;
        SearchActivityTransform.Y = 0;
        _searchActivityStoryboard.Begin();
        _searchActivityStoryboardRunning = true;
    }

    private void StopSearchActivityAnimation()
    {
        if (_searchActivityStoryboardRunning)
            _searchActivityStoryboard?.Stop();

        _searchActivityStoryboardRunning = false;
        SearchActivityTransform.X = 0;
        SearchActivityTransform.Y = 0;
    }

    private Storyboard BuildSearchActivityStoryboard()
    {
        var xAnimation = new DoubleAnimationUsingKeyFrames();
        var yAnimation = new DoubleAnimationUsingKeyFrames();

        foreach ((double seconds, double x, double y) in SearchActivityVisuals.FigureEightKeyframes)
            AddSearchActivityFrame(xAnimation, yAnimation, seconds, x, y);

        Storyboard.SetTarget(xAnimation, SearchActivityTransform);
        Storyboard.SetTargetProperty(xAnimation, nameof(TranslateTransform.X));
        Storyboard.SetTarget(yAnimation, SearchActivityTransform);
        Storyboard.SetTargetProperty(yAnimation, nameof(TranslateTransform.Y));

        var storyboard = new Storyboard
        {
            RepeatBehavior = RepeatBehavior.Forever,
        };
        storyboard.Children.Add(xAnimation);
        storyboard.Children.Add(yAnimation);
        return storyboard;
    }

    private static void AddSearchActivityFrame(
        DoubleAnimationUsingKeyFrames xAnimation,
        DoubleAnimationUsingKeyFrames yAnimation,
        double seconds,
        double x,
        double y)
    {
        KeyTime keyTime = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(seconds));
        xAnimation.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = keyTime, Value = x });
        yAnimation.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = keyTime, Value = y });
    }
}
