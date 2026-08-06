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

    /// <summary>Shows the Yagu-styled tray context menu at the cursor. Its inline Quick search panel
    /// expands in place, so scope, query, options and Traditional/Semantic mode are all set from the
    /// menu itself instead of a separate dialog.</summary>
    internal void ShowTrayContextMenu(int cursorX, int cursorY)
    {
        TrayMenuWindow.ShowAt(
            cursorX,
            cursorY,
            RootGrid.ActualTheme,
            new TrayMenuActions
            {
                OpenReset = () => DispatcherQueue.TryEnqueue(async () => await ResetToLauncherModeAsync()),
                OpenExisting = () => DispatcherQueue.TryEnqueue(RestoreWindowFromTray),
                CloseApp = () => RequestApplicationExit(Services.Index.IndexingCloseTrigger.UserExit),
                ReadCurrentSearch = () => new TrayQuickSearchRequest(
                    ViewModel.Directory ?? string.Empty,
                    ViewModel.Query ?? string.Empty,
                    ViewModel.UseRegex,
                    ViewModel.CaseSensitive,
                    ViewModel.Multiline,
                    ViewModel.ExactMatch,
                    ViewModel.IsSemanticQueryMode),
                RunQuickSearch = request => DispatcherQueue.TryEnqueue(() => ApplyTrayQuickSearch(request)),
            });
    }

    /// <summary>Applies the tray panel's options to this instance, then runs the search.</summary>
    private void ApplyTrayQuickSearch(TrayQuickSearchRequest request)
    {
        ViewModel.IsSemanticQueryMode = request.Semantic;
        if (!request.Semantic)
        {
            // Multiline is regex-only in the search box, so keep the same coupling here.
            ViewModel.UseRegex = request.UseRegex || request.Multiline;
            ViewModel.CaseSensitive = request.CaseSensitive;
            ViewModel.Multiline = request.Multiline;
            ViewModel.ExactMatch = request.ExactMatch;
        }

        ApplyExternalSearchRequest(new SearchRequest(
            Directory: request.Directory,
            Query: request.Query,
            RunSearch: true));
    }
}
