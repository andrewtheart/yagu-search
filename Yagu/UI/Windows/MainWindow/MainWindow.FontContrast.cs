using Microsoft.UI.Xaml;
using Yagu.Helpers;
using Yagu.Services;
using Yagu.ViewModels;

namespace Yagu;

public sealed partial class MainWindow
{
    private bool _fontContrastCheckQueued;

    private void QueueFontContrastCheck()
    {
        if (_fontContrastCheckQueued)
            return;

        _fontContrastCheckQueued = true;
        DispatcherQueue.TryEnqueue(async () =>
        {
            _fontContrastCheckQueued = false;
            await ShowFontContrastWarningIfNeededAsync();
        });
    }

    private async Task ShowFontContrastWarningIfNeededAsync()
    {
        if (_settingsWindow is not null)
            return;

        long previousQuerySuggestionSuppression = _suppressQuerySuggestionsUntilTick;
        _suppressQuerySuggestionsUntilTick = long.MaxValue;
        HideQuerySuggestions(QueryBox);

        _ownedModalWindowDepth++;
        try
        {
            await FontContrastWarningDialog.ShowIfNeededAsync(_hwnd, ViewModel, ResolveFontContrastTheme());
        }
        finally
        {
            _suppressQuerySuggestionsUntilTick = Math.Max(previousQuerySuggestionSuppression, Environment.TickCount64 + 1000);
            HideQuerySuggestions(QueryBox);
            _ownedModalWindowDepth = Math.Max(0, _ownedModalWindowDepth - 1);
        }
    }

    private FontContrastTheme ResolveFontContrastTheme()
        => AppThemeService.ResolveEffectiveTheme(RootGrid, ViewModel.ThemeModeIndex) == ElementTheme.Light
            ? FontContrastTheme.Light
            : FontContrastTheme.Dark;

    private static bool IsFontContrastRelevantProperty(string? propertyName)
        => propertyName is nameof(MainViewModel.ThemeModeIndex)
            or nameof(MainViewModel.SelectedPreviewContentBackgroundColor)
            or nameof(MainViewModel.UnselectedPreviewContentBackgroundColor)
            or nameof(MainViewModel.PreviewGutterContextColor)
            or nameof(MainViewModel.PreviewGutterMatchColor)
            or nameof(MainViewModel.PreviewEditorGutterColor)
            or nameof(MainViewModel.PreviewMatchTextColor)
            or nameof(MainViewModel.PreviewMatchLineColor)
            or nameof(MainViewModel.ResultListMatchHighlightColor);
}