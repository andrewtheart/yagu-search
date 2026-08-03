using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Yagu.Helpers;

namespace Yagu;

/// <summary>
/// Tray "Quick search" popup and the cross-process search-request receiver. Both funnel through
/// <see cref="ApplyExternalSearchRequest"/> so a search invoked from the tray or forwarded from a
/// second launch (e.g. the Explorer "Search with Yagu" context menu) is applied to this single
/// running instance the same way.
/// </summary>
public sealed partial class MainWindow
{
    private SearchRequestListener? _searchRequestListener;
    private bool _quickSearchDialogOpen;

    /// <summary>Starts listening for forwarded search requests (WM_COPYDATA from a second launch).
    /// Created once the window handle is available so a listener exists before the app docks to tray.</summary>
    private void InitializeSearchRequestListener()
    {
        if (_searchRequestListener is not null) return;
        try
        {
            _searchRequestListener = new SearchRequestListener();
            _searchRequestListener.RequestReceived += OnSearchRequestReceived;
        }
        catch
        {
            // A missing listener only degrades to plain window activation for forwarded requests —
            // never fatal, so a failure here must not block window startup.
            _searchRequestListener = null;
        }
    }

    private void DisposeSearchRequestListener()
    {
        if (_searchRequestListener is null) return;
        _searchRequestListener.RequestReceived -= OnSearchRequestReceived;
        _searchRequestListener.Dispose();
        _searchRequestListener = null;
    }

    // Raised on the UI thread by the message-only listener window; ApplyExternalSearchRequest still
    // marshals defensively in case a future caller invokes it from another thread.
    private void OnSearchRequestReceived(SearchRequest request) => ApplyExternalSearchRequest(request);

    /// <summary>Points this running instance at the requested directory and/or query, brings the
    /// window to the foreground (restoring it from the tray if docked), and optionally runs the
    /// search. A supplied directory overrides any pinned startup directory.</summary>
    internal void ApplyExternalSearchRequest(SearchRequest request)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => ApplyExternalSearchRequest(request));
            return;
        }

        // Bring Yagu up first so the applied directory/query is visible to the user.
        RestoreWindowFromTray();

        if (request.Directory is not null)
        {
            var dir = request.Directory.Trim();
            if (dir.Length == 0)
            {
                // An explicit blank directory means "search all drives" — clear any pinned/previous value.
                ViewModel.Directory = string.Empty;
            }
            else if (System.IO.Directory.Exists(dir))
            {
                // Overrides the pinned startup directory (and whatever the box currently holds).
                ViewModel.Directory = dir;
            }
            else
            {
                // Surfaces the standard "path does not exist" message rather than silently ignoring it.
                ViewModel.SetDirectoryFromArgs(dir);
            }
        }

        if (request.Query is not null)
            ViewModel.Query = request.Query;

        if (request.RunSearch && !string.IsNullOrWhiteSpace(ViewModel.Query))
            _ = StartSearchFromUiAsync();
    }

    /// <summary>Shows the tray "Quick search" popup: a small dialog with a search-term box and a
    /// directory box. Clicking Search (or pressing Enter) runs the search in this running instance.</summary>
    internal async Task ShowTrayQuickSearchAsync()
    {
        if (_hwnd == IntPtr.Zero) return;

        // Bring the window forward so the dialog has a visible, focused owner.
        RestoreWindowFromTray();

        // Never stack this popup on top of itself or another owned modal.
        if (_quickSearchDialogOpen || YaguDialog.HasOpenOwnedWindow(_hwnd)) return;

        var queryBox = new TextBox
        {
            PlaceholderText = "e.g. TODO, error, *.cs",
            Text = ViewModel.Query ?? string.Empty,
        };
        var directoryBox = new TextBox
        {
            PlaceholderText = "Leave blank to search all drives",
            Text = ViewModel.Directory ?? string.Empty,
        };

        var content = new StackPanel { Spacing = 6 };
        content.Children.Add(new TextBlock { Text = "Search term" });
        content.Children.Add(queryBox);
        content.Children.Add(new TextBlock { Text = "Directory", Margin = new Thickness(0, 6, 0, 0) });
        content.Children.Add(directoryBox);

        _quickSearchDialogOpen = true;
        try
        {
            var result = await YaguDialog.ShowAsync(
                _hwnd,
                new YaguDialogOptions
                {
                    Title = "Quick search",
                    TitleGlyph = "\uE721", // Search
                    Content = content,
                    PrimaryButtonText = "Search",
                    CloseButtonText = "Cancel",
                    DefaultButton = YaguDialogDefaultButton.Primary,
                    RequestedTheme = RootGrid.ActualTheme,
                    ShowTitleBar = false,
                    ShowTopRightCloseButton = true,
                    Width = 460,
                    Height = 280,
                },
                dialog =>
                {
                    // Enter in either field submits the search; focus starts in the search-term box.
                    void SubmitOnEnter(object _, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
                    {
                        if (e.Key == Windows.System.VirtualKey.Enter)
                        {
                            e.Handled = true;
                            dialog.AcceptPrimary();
                        }
                    }
                    queryBox.KeyDown += SubmitOnEnter;
                    directoryBox.KeyDown += SubmitOnEnter;
                    queryBox.Loaded += (_, _) => queryBox.Focus(FocusState.Programmatic);
                });

            if (result == YaguDialogResult.Primary)
            {
                ApplyExternalSearchRequest(new SearchRequest(
                    Directory: directoryBox.Text,
                    Query: queryBox.Text,
                    RunSearch: true));
            }
        }
        finally
        {
            _quickSearchDialogOpen = false;
        }
    }
}
