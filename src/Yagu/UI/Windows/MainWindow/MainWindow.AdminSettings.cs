using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Windows.ApplicationModel.DataTransfer;
namespace Yagu;

/// <summary>
/// Admin notices, settings/help windows, HDD warning prompts, and drag/drop.
/// </summary>
public sealed partial class MainWindow
{
    // Settings tabs are sorted alphabetically at build time (see SettingsWindow.SortTabsAlphabetically):
    // AI(0), Developer Options(1), Display(2), Editor(3), Interaction(4), OCR(5), Performance(6),
    // Search Defaults(7), Search Limits(8), Shortcuts & History(9), Terminal Emulator(10), Window(11).
    // Keep these in sync when adding/renaming tabs.
    private const int SettingsPerformanceTabIndex = 6;
    private const int SettingsDisplayTabIndex = 2;

    private async void OnAdminLearnMore(object sender, RoutedEventArgs e)
    {
        var segments = Yagu.Services.FileLister.ParseAdminProtectedSegments(ViewModel.AdminProtectedPathSegments);
        if (segments.Count == 0) segments.AddRange(Yagu.Services.FileLister.DefaultAdminProtectedPathSegments);
        await AdminProtectedPathsDialog.ShowAsync(_hwnd, segments);
    }

    private async void OnObeyGitignoreToggled(object sender, RoutedEventArgs e)
    {
        // Only show the dialog when the toggle is being turned on (not during initial load).
        if (!_isLoaded) return;
        if (sender is ToggleSwitch ts && !ts.IsOn) return;
        if (!HasUserDefinedIncludeFilterText()) return;

        // If the user previously saved a precedence preference (via "Don't ask again" or the
        // Search Defaults setting), honor it silently instead of prompting again.
        if (ViewModel.GitignorePrecedencePreference is bool saved)
        {
            ViewModel.GitignoreTakesPrecedence = saved;
            return;
        }

        var contentPanel = new StackPanel { Spacing = 12, MinWidth = 360 };
        contentPanel.Children.Add(new TextBlock
        {
            Text = "When a file is both matched by your Include filter and excluded by .gitignore, which one should take precedence?\n\n" +
                   "Yes, .gitignore wins - the file is skipped, even though it matches your Include filter.\n\n" +
                   "No, Include filter wins - the file is searched, even though .gitignore would exclude it.\n\n" +
                   "This choice only applies to files where the two conflict.",
            TextWrapping = TextWrapping.Wrap,
        });
        var dontAskAgain = new CheckBox { Content = "Don't ask again" };
        contentPanel.Children.Add(dontAskAgain);

        var result = await YaguDialog.ShowAsync(
            _hwnd,
            new YaguDialogOptions
            {
                Title = ".gitignore vs Include filter",
                TitleGlyph = "\uE71C", // Filter
                Content = contentPanel,
                PrimaryButtonText = "Yes, .gitignore wins",
                SecondaryButtonText = "No, Include filter wins",
                CloseButtonText = null,
                DefaultButton = YaguDialogDefaultButton.Primary,
                ShowTitleBar = false,
                Width = 620,
                Height = 360,
            });

        bool gitignoreWins = result != YaguDialogResult.Secondary;
        ViewModel.GitignoreTakesPrecedence = gitignoreWins;

        if (dontAskAgain.IsChecked == true)
        {
            ViewModel.GitignorePrecedencePreference = gitignoreWins;
            await ViewModel.PersistSettingsAsync();
        }
    }

    private bool HasUserDefinedIncludeFilterText()
    {
        if (IncludeFilterBox is null)
            return !string.IsNullOrWhiteSpace(ViewModel.IncludeGlobs);

        string text = IncludeFilterBox.Text?.Trim() ?? string.Empty;
        return text.Length > 0 && !IsFilterExampleText(IncludeFilterBox);
    }

    private SettingsWindow? _settingsWindow;

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        OpenSettingsTab();
    }

    private void OpenSettingsTab(int? tabIndex = null)
    {
        // If a settings window is already open, activate it instead of creating a new one.
        if (_settingsWindow is not null)
        {
            try
            {
                _settingsWindow.Activate();
                _settingsWindow.BringInFrontOfMainWindow();
                if (tabIndex.HasValue)
                    _settingsWindow.SelectTab(tabIndex.Value);
                return;
            }
            catch { _settingsWindow = null; }
        }

        _settingsWindow = new SettingsWindow(ViewModel, _hotkeyService, _hwnd, ApplyWordWrap, ApplyPreviewSectionBackgrounds, OpenHelpWindow, SuppressLauncherHideToTrayForOwnedWindowClose);
        _settingsWindow.Closed += (_, _) =>
        {
            SuppressLauncherHideToTrayForOwnedWindowClose();
            _settingsWindow = null;
            Activate();
        };
        _settingsWindow.Activate();
        _settingsWindow.BringInFrontOfMainWindow();
        if (tabIndex.HasValue)
            _settingsWindow.SelectTab(tabIndex.Value);
    }

    /// <summary>
    /// Tracks which disks have already shown the HDD parallelism warning this session.
    /// Keyed by drive root (e.g. "C:\"). Reset only when the app restarts so the dialog
    /// shows at most once per disk per Yagu session.
    /// </summary>
    private readonly HashSet<string> _hddWarningShownDrives = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Checks if the search directory is on a rotational HDD and, if LimitParallelismOnHdd is enabled,
    /// forces parallelism to 1. Shows a warning dialog unless SuppressHddParallelismWarnings is set.
    /// Returns false if the user cancels.
    /// </summary>
    private async Task<bool> CheckHddAndWarnAsync()
    {
        if (!ViewModel.LimitParallelismOnHdd) return true;

        var roots = ViewModel.ResolveTargetRoots();
        if (roots.Count == 0) return true;

        var hddRoots = roots.Where(r => Helpers.DiskTypeDetector.IsHardDisk(r)).ToList();
        if (hddRoots.Count == 0) return true;

        bool singleRoot = roots.Count == 1;
        if (singleRoot)
        {
            // Single explicit directory on an HDD: force parallelism to 1 (sequential) for this
            // session only. This temporary override is NOT written back to the persisted
            // ParallelismIndex setting, so the user's saved preference is preserved across restarts.
            // (All-drives runs apply per-drive parallelism in StartSearchAsync instead, so the
            // non-HDD drives keep the configured parallelism.)
            ViewModel.SetSessionParallelismOverride(1);
        }

        // Parallelism limiting above always applies while LimitParallelismOnHdd is on. The warning
        // dialog is a separate, independently suppressible notice: when the user has opted out of the
        // warning, still limit parallelism but skip the dialog. Changing the parallelism behavior
        // itself requires the Settings page.
        if (ViewModel.SuppressHddParallelismWarnings) return true;

        // Only warn once per HDD-drive-set per session; subsequent searches on the same disks
        // still limit parallelism but skip the dialog until the app is restarted.
        var driveKey = string.Join(";", hddRoots
            .Select(GetHddWarningDriveKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase));
        if (!_hddWarningShownDrives.Add(driveKey)) return true;

        var contentPanel = new StackPanel { Spacing = 8, MinWidth = 360 };
        contentPanel.Children.Add(new TextBlock
        {
            Text = singleRoot
                ? "The selected search directory is on a rotational hard disk (HDD). " +
                  "Parallelism has been set to 1 thread to avoid excessive disk thrashing."
                : $"{hddRoots.Count} of the drives being searched are on rotational hard disks (HDD). " +
                  "Parallelism is limited to 1 thread on those drives to avoid excessive disk thrashing; " +
                  "the other drives use your configured parallelism.",
            TextWrapping = TextWrapping.Wrap,
        });

        // Per-search parallelism override for the HDD drive(s). Defaults to the limited "1 thread"
        // value; choosing a higher value applies to this search only (consumed by StartSearchAsync)
        // and never changes the persisted setting.
        contentPanel.Children.Add(new TextBlock
        {
            Text = "Override parallelism for the HDD on this search:",
            FontSize = 12,
            Opacity = 0.9,
        });
        int processorCount = Environment.ProcessorCount;
        var parallelismOverride = new ComboBox { MinWidth = 260, HorizontalAlignment = HorizontalAlignment.Left };
        parallelismOverride.Items.Add($"Safe cap (up to {Math.Min(16, processorCount)})");
        parallelismOverride.Items.Add("1 thread (sequential, HDD safe)");
        parallelismOverride.Items.Add($"Half cores ({Math.Max(1, processorCount / 2)})");
        parallelismOverride.Items.Add($"2\u00d7 cores ({processorCount * 2}, I/O heavy)");
        parallelismOverride.Items.Add($"All cores ({Math.Max(1, processorCount)})");
        parallelismOverride.SelectedIndex = 1; // default to the limited, HDD-safe 1-thread value
        contentPanel.Children.Add(parallelismOverride);
        contentPanel.Children.Add(new TextBlock
        {
            Text = "Applies to this search only and doesn't change your saved setting. Higher values can thrash a rotational disk.",
            FontSize = 11,
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap,
        });

        var secondBlock = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = 0.8,
        };
        secondBlock.Inlines.Add(new Run { Text = "You can change the default for all searches or disable this warning in " });
        var settingsLink = new Hyperlink();
        settingsLink.Inlines.Add(new Run { Text = "Settings \u2192 Performance" });
        var openSettingsRequested = false;
        YaguDialog? hddDialog = null;
        settingsLink.Click += (_, _) =>
        {
            // The dialog is modal (the owner window is disabled while it is shown), so opening
            // the Settings window now would leave it behind the disabled owner. Close the dialog
            // first, then open Settings -> Performance after it has fully dismissed.
            openSettingsRequested = true;
            hddDialog?.AcceptClose();
        };
        secondBlock.Inlines.Add(settingsLink);
        secondBlock.Inlines.Add(new Run { Text = "." });
        contentPanel.Children.Add(secondBlock);

        // Lets the user stop seeing this warning without opening Settings. This ONLY suppresses the
        // dialog — parallelism is still limited on HDDs while LimitParallelismOnHdd is on. Changing the
        // parallelism behavior itself requires Settings -> Performance.
        var dontWarnAgain = new CheckBox
        {
            Content = "Don't warn me about HDDs again",
            IsChecked = false,
        };
        contentPanel.Children.Add(dontWarnAgain);

        var result = await YaguDialog.ShowAsync(
            _hwnd,
            new YaguDialogOptions
            {
                Title = "HDD detected - parallelism limited",
                TitleGlyph = "\uE7BA",
                TitleGlyphColor = Microsoft.UI.Colors.Gold,
                Content = contentPanel,
                PrimaryButtonText = "Continue search",
                CloseButtonText = "Cancel",
                DefaultButton = YaguDialogDefaultButton.Primary,
                ShowTitleBar = false,
                Width = 560,
                Height = 470,
                MaxContentHeight = 360,
            },
            dlg => hddDialog = dlg);

        // Apply the "don't warn again" preference however the dialog was dismissed: persist
        // SuppressHddParallelismWarnings = true so future searches still limit parallelism on HDDs but
        // skip this dialog. It does NOT change the parallelism behavior.
        if (dontWarnAgain.IsChecked == true)
        {
            ViewModel.SuppressHddParallelismWarnings = true;
            await ViewModel.PersistSettingsAsync();
        }

        if (openSettingsRequested)
        {
            OpenSettingsTab(SettingsPerformanceTabIndex);
            return false;
        }

        // Continuing the search: apply the chosen per-search HDD parallelism (one-shot, not persisted).
        if (result == YaguDialogResult.Primary)
            ViewModel.SetHddParallelismOverrideForNextSearch(parallelismOverride.SelectedIndex);

        return result == YaguDialogResult.Primary;
    }

    /// <summary>
    /// Shows a modal notice (no title bar) when the active search was terminated because the result
    /// temp-file drive became too full. Includes a link to Settings &#8594; Performance where the user
    /// can adjust the temp-drive full warning threshold. Invoked on the UI thread via the
    /// <see cref="MainViewModel.SearchTerminatedByLowDiskSpace"/> event.
    /// </summary>
    private async Task ShowLowDiskSpaceTerminationDialogAsync(string message)
    {
        try
        {
            var contentPanel = new StackPanel { Spacing = 8, MinWidth = 360 };
            contentPanel.Children.Add(new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
            });

            var linkBlock = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.8 };
            linkBlock.Inlines.Add(new Run { Text = "You can change the temp-drive full warning threshold in " });
            var settingsLink = new Hyperlink();
            settingsLink.Inlines.Add(new Run { Text = "Settings \u2192 Performance" });
            var openSettingsRequested = false;
            YaguDialog? dialog = null;
            settingsLink.Click += (_, _) =>
            {
                // The dialog is modal (the owner window is disabled while it is shown), so close it
                // first, then open Settings -> Performance after it has fully dismissed.
                openSettingsRequested = true;
                dialog?.AcceptClose();
            };
            linkBlock.Inlines.Add(settingsLink);
            linkBlock.Inlines.Add(new Run { Text = "." });
            contentPanel.Children.Add(linkBlock);

            await YaguDialog.ShowAsync(
                _hwnd,
                new YaguDialogOptions
                {
                    Title = "Search canceled - low disk space",
                    TitleGlyph = "\uE7BA", // Warning
                    Content = contentPanel,
                    PrimaryButtonText = "OK",
                    CloseButtonText = null,
                    DefaultButton = YaguDialogDefaultButton.Primary,
                    RequestedTheme = RootGrid.ActualTheme,
                    ShowTitleBar = false,
                    Width = 560,
                    Height = 320,
                    MaxContentHeight = 220,
                },
                dlg => dialog = dlg);

            if (openSettingsRequested)
                OpenSettingsTab(SettingsPerformanceTabIndex);
        }
        catch (Exception ex)
        {
            Yagu.Services.LogService.Instance.Warning("Search", $"Low disk-space notice dialog failed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Warns before searching when the query names a file whose extension is currently excluded by an
    /// advanced option (Skip/Binary extensions or an Include/Exclude filter). Returns false to cancel
    /// the search (user chose Cancel). "Include &amp; search" un-excludes the extension, then proceeds.
    /// </summary>
    private async Task<bool> CheckExcludedExtensionAndWarnAsync()
    {
        var warning = ViewModel.TryGetExcludedExtensionWarning();
        if (warning is null) return true;

        string ext = warning.Extension;

        // Honor a saved "Always do this" preference — skip the dialog and silently apply the remembered
        // action: either include the excluded type for this search, or search without it.
        if (ViewModel.SuppressExcludedExtensionWarnings)
        {
            if (ViewModel.IncludeExcludedExtensionByDefault)
                await ViewModel.IncludeExtensionForSearchAsync(warning);
            return true;
        }

        string sources = DescribeExclusionReasons(warning.Reasons);

        var contentPanel = new StackPanel { Spacing = 12, MinWidth = 360 };
        var message = new TextBlock { TextWrapping = TextWrapping.Wrap };
        message.Inlines.Add(new Run { Text = "You're searching for a file ending in " });
        message.Inlines.Add(new Run { Text = "." + ext, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        message.Inlines.Add(new Run { Text = $", but .{ext} files are currently excluded by your {sources}, so they won't appear in the results." });
        contentPanel.Children.Add(message);

        contentPanel.Children.Add(new TextBlock
        {
            Text = $"Choose \"Include .{ext} & search\" to include this file type in this search, or \"Search anyway\" to continue without it.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = 0.8,
        });

        // Clarifies exactly what checking the box does (the previous "Don't warn me again" wording did not
        // say whether excluded types would be included or not next time): it REMEMBERS the button you click
        // as a permanent default and stops asking.
        var alwaysDoThis = new CheckBox { Content = "Always do this \u2014 don't ask again for excluded file types" };
        contentPanel.Children.Add(alwaysDoThis);
        contentPanel.Children.Add(new TextBlock
        {
            Text = "Yagu will remember the button you click and apply it automatically next time: it will either " +
                   "always include the excluded file type in matching searches, or always search without it. " +
                   "You can change this any time in Settings \u2192 Search.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Opacity = 0.6,
        });

        var result = await YaguDialog.ShowAsync(
            _hwnd,
            new YaguDialogOptions
            {
                Title = "Excluded file type",
                TitleGlyph = "\uE71C", // Filter
                Content = contentPanel,
                PrimaryButtonText = $"Include .{ext} & search",
                SecondaryButtonText = "Search anyway",
                CloseButtonText = null,
                DefaultButton = YaguDialogDefaultButton.Primary,
                RequestedTheme = RootGrid.ActualTheme,
                ShowTitleBar = false,
                Width = 600,
                Height = 380,
                MaxContentHeight = 280,
            });

        bool remember = alwaysDoThis.IsChecked == true;

        if (result == YaguDialogResult.Close)
        {
            // Dismissed without choosing an action (Esc / window close) — cancel the search rather than
            // run one that would not find the file. Nothing is remembered (no button was clicked).
            return false;
        }

        if (result == YaguDialogResult.Secondary)
        {
            // "Search anyway" — run the search without un-excluding the type. If "Always do this" is
            // checked, remember it: future matching searches skip the dialog and search WITHOUT the type.
            if (remember)
            {
                ViewModel.SuppressExcludedExtensionWarnings = true;
                ViewModel.IncludeExcludedExtensionByDefault = false;
                await ViewModel.PersistSettingsAsync();
            }
            return true;
        }

        // Primary: include the extension for THIS search only (no settings are changed by the fix itself;
        // Advanced Options are reset to the saved defaults when the search finishes). If "Always do this"
        // is checked, remember it: future matching searches skip the dialog and auto-include the type.
        if (remember)
        {
            ViewModel.SuppressExcludedExtensionWarnings = true;
            ViewModel.IncludeExcludedExtensionByDefault = true;
            await ViewModel.PersistSettingsAsync();
        }
        await ViewModel.IncludeExtensionForSearchAsync(warning);
        return true;
    }

    /// <summary>
    /// Warns before a REGEX <em>content</em> search whose whole pattern matches everything (<c>.</c>,
    /// <c>.*</c>, <c>[\s\S]</c>, …). Such a search emits one match per character (or line) of every
    /// file — millions of noise matches on minified/one-line files — and is almost always a mistake
    /// (a literal <c>.</c>, or a file-name listing). Offers "Search file names" (switch to a filename
    /// listing) or "Search anyway"; Esc cancels. A literal "." (regex off) and a file-names-only search
    /// are never flagged. Returns false to cancel the search.
    /// </summary>
    private async Task<bool> CheckMatchEverythingPatternAndWarnAsync()
    {
        // Only a REGEX match-everything pattern that will actually scan CONTENT is a problem.
        if (!ViewModel.UseRegex) return true;
        if ((Yagu.Models.SearchMode)ViewModel.SearchModeIndex == Yagu.Models.SearchMode.FileNames) return true;
        if (!Yagu.Helpers.SearchPatternClassifier.IsMatchEverythingRegex(ViewModel.Query)) return true;

        string query = (ViewModel.Query ?? string.Empty).Trim();

        var contentPanel = new StackPanel { Spacing = 12, MinWidth = 360 };
        var message = new TextBlock { TextWrapping = TextWrapping.Wrap };
        message.Inlines.Add(new Run { Text = "The regular expression " });
        message.Inlines.Add(new Run { Text = query, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        message.Inlines.Add(new Run { Text = " matches everything, so a content search returns one result for every " +
            "character (or line) of every file \u2014 usually millions of noise matches. Did you mean to search file " +
            "names, or to search for a literal character?" });
        contentPanel.Children.Add(message);

        contentPanel.Children.Add(new TextBlock
        {
            Text = "Choose \"Search file names\" to list matching files instead, or \"Search anyway\" to run the content " +
                   "search (results stay bounded by your safety limits). To search for a literal character, turn off the " +
                   "regex option and try again.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = 0.8,
        });

        var result = await YaguDialog.ShowAsync(
            _hwnd,
            new YaguDialogOptions
            {
                Title = "Very broad search pattern",
                TitleGlyph = "\uE7BA", // Warning
                Content = contentPanel,
                PrimaryButtonText = "Search file names",
                SecondaryButtonText = "Search anyway",
                CloseButtonText = null,
                DefaultButton = YaguDialogDefaultButton.Primary,
                RequestedTheme = RootGrid.ActualTheme,
                ShowTitleBar = false,
                Width = 600,
                Height = 340,
                MaxContentHeight = 240,
            });

        if (result == YaguDialogResult.Close)
            return false; // Esc / close — cancel rather than flood the results.

        if (result == YaguDialogResult.Primary)
        {
            // Switch this search to a file-name listing (the likely intent). SearchModeIndex is
            // session-only and resets to the default after the search, so no persisted change.
            ViewModel.SearchModeIndex = (int)Yagu.Models.SearchMode.FileNames;
        }

        // Secondary ("Search anyway") proceeds unchanged — the per-line cap and absolute results
        // ceiling keep memory bounded.
        return true;
    }

    private static string DescribeExclusionReasons(Yagu.Services.ExtensionExclusionReason reasons)
    {
        var parts = new List<string>();
        if (reasons.HasFlag(Yagu.Services.ExtensionExclusionReason.BinaryExtensions)) parts.Add("Binary extensions list");
        if (reasons.HasFlag(Yagu.Services.ExtensionExclusionReason.SkipExtensions)) parts.Add("Skip extensions list");
        if (reasons.HasFlag(Yagu.Services.ExtensionExclusionReason.ArchiveExtensions)) parts.Add("Archive extensions list");
        if (reasons.HasFlag(Yagu.Services.ExtensionExclusionReason.ExcludeFilter)) parts.Add("Exclude filter");
        if (reasons.HasFlag(Yagu.Services.ExtensionExclusionReason.IncludeFilter)) parts.Add("Include filter");
        return parts.Count switch
        {
            0 => "advanced options",
            1 => parts[0],
            2 => $"{parts[0]} and {parts[1]}",
            _ => string.Join(", ", parts.Take(parts.Count - 1)) + ", and " + parts[^1],
        };
    }

    /// <summary>
    /// Returns a stable per-disk key for the HDD warning dedup set. Uses the path root
    /// (drive letter or UNC share) so repeated searches under the same disk share one key.
    /// </summary>
    private static string GetHddWarningDriveKey(string directory)
    {
        try
        {
            var root = Path.GetPathRoot(directory);
            if (!string.IsNullOrEmpty(root)) return root.TrimEnd('\\', '/').ToUpperInvariant();
        }
        catch { /* fall through to the raw directory below */ }
        return directory.ToUpperInvariant();
    }

    private void OnCloseWindowClick(object sender, RoutedEventArgs e)
    {
        if (!_forceClose && ViewModel.CloseToTray)
        {
            RequestCloseToTray();
            return;
        }
        Close();
    }

    private HelpWindow? _helpWindow;

    private void OnOpenCredits(object sender, RoutedEventArgs e)
        => OpenHelpWindow();

    private void OpenHelpWindow()
    {
        if (_helpWindow is not null)
        {
            try
            {
                _helpWindow.Activate();
                _helpWindow.BringInFrontOfMainWindow(_hwnd);
                return;
            }
            catch { _helpWindow = null; }
        }

        var helpPath = Path.Combine(AppContext.BaseDirectory, "HELP.html");
        _helpWindow = new HelpWindow(_hwnd, helpPath, CurrentAppWindowTitle);
        _helpWindow.Closed += (_, _) => _helpWindow = null;
        _helpWindow.Activate();
        _helpWindow.BringInFrontOfMainWindow(_hwnd);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Link;
        }
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        foreach (var item in items)
        {
            if (item is Windows.Storage.StorageFolder f)
            {
                ViewModel.Directory = f.Path;
                return;
            }
        }
    }
}
