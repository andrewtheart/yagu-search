using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace Yagu;

/// <summary>
/// First-run AI-model qualification flow: offers a one-time check that tests the models that fit this
/// PC and strongly suggests the fastest one that answers accurately, while still letting the user pick
/// another. Sequenced into the startup-modal chain so it never stacks on another first-run prompt.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>
    /// First-run only: when AI (Semantic) search is available and the one-time model check has not run,
    /// offer to test the compatible models and pick the best one. Refusing the check turns AI (Semantic)
    /// search off (it needs a validated model to be reliable) and shows how to opt back in from Settings;
    /// running the check (or skipping from the results) marks it complete.
    /// </summary>
    private async Task OfferSemanticModelQualificationIfNeededAsync()
    {
        if (!ViewModel.ShouldOfferSemanticModelQualification)
            return;
        // Don't stack on another startup prompt; not marked complete yet, so it retries next launch.
        if (YaguDialog.HasOpenOwnedWindow(_hwnd))
            return;

        // The main window's launch position is applied on a Low-priority dispatcher tick, so drain the
        // queue once before showing the offer. Otherwise (first run, non-launcher, no earlier prompt) this
        // modal could center over the window's pre-positioned location instead of the backing window.
        await YieldUntilWindowPositionedAsync();

        var intro = await YaguDialog.ShowAsync(
            _hwnd,
            new YaguDialogOptions
            {
                Title = "Set up AI (Semantic) search?",
                TitleGlyph = "\uE99A",
                TitleGlyphColor = Microsoft.UI.Colors.MediumPurple,
                Content = BuildSemanticQualificationIntroContent(),
                PrimaryButtonText = "Run the check",
                CloseButtonText = "Not now",
                DefaultButton = YaguDialogDefaultButton.Primary,
                RequestedTheme = RootGrid.ActualTheme,
                ShowTitleBar = false,
                Width = 560,
                Height = 360,
                MaxContentHeight = 240,
            });

        if (intro != YaguDialogResult.Primary)
        {
            // The user refused the check. AI (Semantic) search needs a model validated on this PC to be
            // reliable, so turn it off and mark the one-time check complete. Then tell them how to opt back
            // in — at their own risk — via a link to the AI settings tab.
            await ViewModel.DeclineAndDisableSemanticSearchAsync();
            await ShowSemanticSearchDisabledNoticeAsync();
            return;
        }

        // The qualification dialog is a non-YaguDialog owned window, so the YaguDialog-scoped suggestion
        // suppression doesn't cover it. Park the query/directory dropdowns shut (and bump the owned-modal
        // depth) for the dialog's whole lifetime so a windowed suggestion popup can't float above it.
        long previousQuerySuggestionSuppression = _suppressQuerySuggestionsUntilTick;
        _suppressQuerySuggestionsUntilTick = long.MaxValue;
        CollapseInputSuggestionDropdowns();
        _ownedModalWindowDepth++;

        SemanticModelQualificationDialogResult result;
        try
        {
            result = await SemanticModelQualificationDialog.ShowAsync(
                _hwnd,
                RootGrid.ActualTheme,
                (thresholds, progress, token) => ViewModel.RunSemanticModelQualificationAsync(thresholds, progress, token));
        }
        finally
        {
            _suppressQuerySuggestionsUntilTick = Math.Max(previousQuerySuggestionSuppression, Environment.TickCount64 + 1000);
            HideQuerySuggestions(QueryBox);
            _ownedModalWindowDepth = Math.Max(0, _ownedModalWindowDepth - 1);
        }

        if (result.Cancelled)
            return; // User cancelled the sweep; offer again next launch.

        if (result.SwitchToTraditional)
        {
            // The check could not validate a model that produces usable results on this PC. Don't silently
            // auto-pick one — turn AI (Semantic) search off, default the app to Traditional (literal)
            // search, and mark the one-time check complete. The dialog already explained why and offered a
            // link back to the AI settings tab.
            await ViewModel.DeclineAndDisableSemanticSearchAsync();
            if (result.OpenAiSettingsRequested)
                OpenSettingsToAiTab();
            return;
        }

        if (result.Accepted && result.Result is not null)
        {
            await ViewModel.ApplySemanticModelQualificationAsync(result.Result, accepted: true, result.ChosenAlias);
        }
        else if (result.Result is not null)
        {
            // Finished but skipped: record the recommendation and mark complete without switching models.
            await ViewModel.ApplySemanticModelQualificationAsync(result.Result, accepted: false);
        }
        else
        {
            // The sweep failed/produced nothing and the user skipped: mark complete so it isn't re-offered.
            await ViewModel.DeclineSemanticModelQualificationAsync();
        }
    }

    /// <summary>Body of the first-run "set up AI search" offer: what the check does and that it runs
    /// on-device and may take a few minutes.</summary>
    private static StackPanel BuildSemanticQualificationIntroContent()
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = "Yagu can test the AI models that fit this PC with a few sample searches and strongly suggest "
                 + "the fastest one that answers accurately. You can still pick a different model afterwards.",
            TextWrapping = TextWrapping.WrapWholeWords,
            FontSize = 14,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "The check runs entirely on your PC. It may download one or more models and take a few minutes; "
                 + "you can cancel at any time. You can also do this later from Settings.",
            TextWrapping = TextWrapping.WrapWholeWords,
            FontSize = 13,
            Opacity = 0.85,
        });
        return panel;
    }

    /// <summary>Shown after the user refuses the first-run check and AI search is turned off. Explains that
    /// it stays off and offers a clickable link to re-enable it and pick a model from the AI settings tab.</summary>
    private async Task ShowSemanticSearchDisabledNoticeAsync()
    {
        bool openAiSettings = false;
        YaguDialog? dialogRef = null;

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = "AI (Semantic) search has been turned off. Without a model that was tested on this PC it can be "
                 + "slow or unreliable, so it stays off until you choose to turn it back on.",
            TextWrapping = TextWrapping.WrapWholeWords,
            FontSize = 14,
        });

        var body = new TextBlock
        {
            TextWrapping = TextWrapping.WrapWholeWords,
            FontSize = 13,
            Opacity = 0.85,
        };
        body.Inlines.Add(new Run { Text = "At your own risk, you can turn it back on and pick a model any time in " });
        var link = new Hyperlink();
        link.Inlines.Add(new Run { Text = "AI settings" });
        link.Click += (_, _) =>
        {
            // Close the notice first, then open Settings so the modal doesn't sit in front of the tab.
            openAiSettings = true;
            dialogRef?.AcceptClose();
        };
        body.Inlines.Add(link);
        body.Inlines.Add(new Run { Text = "." });
        panel.Children.Add(body);

        await YaguDialog.ShowAsync(
            _hwnd,
            new YaguDialogOptions
            {
                Title = "AI search turned off",
                TitleGlyph = "\uE99A",
                TitleGlyphColor = Microsoft.UI.Colors.MediumPurple,
                Content = panel,
                PrimaryButtonText = "OK",
                DefaultButton = YaguDialogDefaultButton.Primary,
                RequestedTheme = RootGrid.ActualTheme,
                ShowTitleBar = false,
                Width = 560,
                Height = 320,
                MaxContentHeight = 220,
            },
            configure: d => dialogRef = d);

        if (openAiSettings)
            OpenSettingsToAiTab();
    }

    /// <summary>Opens the Settings window and selects the AI tab (resolved by header because tabs are
    /// sorted alphabetically, so the index isn't fixed).</summary>
    private void OpenSettingsToAiTab()
    {
        OpenSettingsTab();
        _settingsWindow?.SelectTabByHeader("AI");
    }

    /// <summary>Awaits a single Low-priority dispatcher pass so any pending deferred window positioning
    /// (enqueued at Low priority during startup) has run before an owner-centered modal is shown.</summary>
    private Task YieldUntilWindowPositionedAsync()
    {
        var tcs = new TaskCompletionSource();
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => tcs.TrySetResult()))
            tcs.TrySetResult();
        return tcs.Task;
    }
}
