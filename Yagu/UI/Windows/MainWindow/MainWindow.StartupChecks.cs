using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Yagu.Services;
using Yagu.Services.Ai;
namespace Yagu;

/// <summary>
/// Content-loaded startup flow, Everything detection, and first-run result-store location prompts.
/// </summary>
public sealed partial class MainWindow
{
    private async void OnContentLoaded(object sender, RoutedEventArgs e)
    {
        ((FrameworkElement)sender).Loaded -= OnContentLoaded;
        SyncWrapModeToggles(ViewModel.PreviewWrapModeIndex);
        ApplyWordWrap(ViewModel.PreviewWordWrap);
        ApplyPreviewColors();
        if (_launcherMode) PositionLauncherWindow();

        // Apply maximize-on-startup setting (only in non-launcher mode)
        if (!_launcherMode && ViewModel.MaximizeOnStartup &&
            AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
        {
            presenter.Maximize();
        }
        else if (!_launcherMode)
        {
            // Place the window per the user's launch-position setting once its size has settled.
            DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                PositionWindowOnLaunch);
        }

        if (_autoSearchOnLoad)
        {
            // Suppress dropdowns so the query/directory suggestion lists
            // don't pop open during an auto-search launch.
            SuppressQuerySuggestionsFor(3000);
            DirectoryBox.IsSuggestionListOpen = false;
        }

        FocusSearchOnLaunch();
        await CheckFirstRunResultStoreTempLocationAsync();
        await CheckEverythingAsync();
        await CheckFirstRunContextMenuAsync();
        await ShowFontContrastWarningIfNeededAsync();
        await ShowCpuSemanticWarningIfNeededAsync();

        if (_autoSearchOnLoad)
        {
            _autoSearchOnLoad = false;
            if (await CheckHddAndWarnAsync())
            {
                CollapseAdvancedOptionsForSearch();
                await ViewModel.StartSearchAsync();
            }
        }
        else
        {
            FocusSearchBox();
        }

        // Non-blocking: alert (once) if Foundry Local has new/updated on-device models available.
        // Fire-and-forget so a slow catalog query never delays the search box or startup focus.
        _ = CheckForNewFoundryModelsAsync();
    }

    private void FocusSearchBox(bool suppressSuggestions = false)
    {
        if (suppressSuggestions)
            SuppressQuerySuggestionsFor(1000);

        DispatcherQueue.TryEnqueue(() =>
        {
            if (suppressSuggestions)
                SuppressQuerySuggestionsFor(1000);

            QueryBox.Focus(FocusState.Programmatic);

            if (suppressSuggestions)
            {
                QueryBox.IsSuggestionListOpen = false;
                DispatcherQueue.TryEnqueue(() => QueryBox.IsSuggestionListOpen = false);
            }
        });
    }

    /// <summary>
    /// First-run only: when AI (Semantic) search is available but no GPU/NPU was detected, warn that the
    /// suggested model would run on the CPU (slower, variable results) and offer to make Traditional the
    /// default search mode. Shown at most once. Titleless modal with a warning glyph, matching the app's
    /// other warning dialogs. Accepting persists the Traditional default and switches the UI immediately.
    /// </summary>
    private async Task ShowCpuSemanticWarningIfNeededAsync()
    {
        if (!ViewModel.ShouldShowCpuSemanticWarning)
            return;
        // Don't stack on another startup prompt; the warning is not marked shown yet, so it simply
        // tries again on the next launch.
        if (YaguDialog.HasOpenOwnedWindow(_hwnd))
            return;

        var result = await YaguDialog.ShowAsync(
            _hwnd,
            new YaguDialogOptions
            {
                Title = "AI search will run on your CPU",
                TitleGlyph = "\uE7BA",
                TitleGlyphColor = Microsoft.UI.Colors.Gold,
                Content = BuildCpuSemanticWarningContent(),
                PrimaryButtonText = "Use Traditional search",
                CloseButtonText = "Keep AI search",
                DefaultButton = YaguDialogDefaultButton.Primary,
                RequestedTheme = RootGrid.ActualTheme,
                ShowTitleBar = false,
                Width = 560,
                Height = 360,
                MaxContentHeight = 240,
            });

        await ViewModel.DismissCpuSemanticWarningAsync(result == YaguDialogResult.Primary);
    }

    /// <summary>Body of the first-run CPU-mode AI-search warning: what CPU mode means and the
    /// recommendation to keep Traditional search as the default.</summary>
    private static StackPanel BuildCpuSemanticWarningContent()
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = "Yagu didn't find a compatible GPU or NPU on this PC, so AI (Semantic) search would run on "
                 + "your CPU. It still works, but it can be slow and the quality of results may vary.",
            TextWrapping = TextWrapping.WrapWholeWords,
            FontSize = 14,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "We recommend keeping Traditional search as your default. You can still switch to AI search "
                 + "any time from the search bar.",
            TextWrapping = TextWrapping.WrapWholeWords,
            FontSize = 13,
            Opacity = 0.85,
        });
        return panel;
    }

    private async Task CheckForNewFoundryModelsAsync()
    {
        // Don't stack on top of another startup prompt (Everything, font-contrast, etc.). If one is
        // open we skip entirely this session — the VM has not committed a baseline yet, so the check
        // simply runs again next launch.
        if (YaguDialog.HasOpenOwnedWindow(_hwnd))
            return;

        IReadOnlyList<FoundryModelChange> changes;
        try
        {
            changes = await ViewModel.CheckForNewFoundryModelsAsync(System.Threading.CancellationToken.None);
        }
        catch (System.Exception ex)
        {
            LogService.Instance.Warning("MainWindow", $"CheckForNewFoundryModelsAsync failed: {ex.Message}", ex);
            return;
        }

        if (changes.Count == 0 || YaguDialog.HasOpenOwnedWindow(_hwnd))
            return;

        var (content, dontAlertAgain) = BuildFoundryModelAlertContent(changes);
        var theme = (Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default;

        var result = await YaguDialog.ShowAsync(
            _hwnd,
            new YaguDialogOptions
            {
                Title = changes.Count == 1 ? "New AI model available" : "New AI models available",
                Content = content,
                TitleGlyph = "\uE99A",
                PrimaryButtonText = "Choose a model\u2026",
                CloseButtonText = "Dismiss",
                DefaultButton = YaguDialogDefaultButton.Primary,
                RequestedTheme = theme,
                Width = 560,
                Height = 420,
                MaxContentHeight = 320,
            });

        if (dontAlertAgain.IsChecked == true)
            ViewModel.FoundryModelUpdateAlertsEnabled = false;

        if (result == YaguDialogResult.Primary)
        {
            await SemanticModelDownloadDialog.ShowAsync(
                _hwnd,
                theme,
                (progress, token) => ViewModel.GetSemanticModelOptionsAsync(progress, token),
                (alias, progress, token) => ViewModel.PrepareSemanticModelAsync(alias, progress, token),
                ViewModel.SemanticModelAlias);
        }
    }

    /// <summary>Builds the body of the new-model alert: an intro line, a row per new/updated model, and
    /// a "Don't alert me again" checkbox (returned so the caller can read its state after the dialog).</summary>
    private static (FrameworkElement Content, CheckBox DontAlertAgain) BuildFoundryModelAlertContent(
        IReadOnlyList<FoundryModelChange> changes)
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = changes.Count == 1
                ? "A new on-device model is available for AI (Semantic) search:"
                : $"{changes.Count} new on-device models are available for AI (Semantic) search:",
            TextWrapping = TextWrapping.WrapWholeWords,
            FontSize = 14,
        });

        var list = new StackPanel { Spacing = 6 };
        foreach (var change in changes)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            row.Children.Add(new FontIcon
            {
                Glyph = "\uE753",
                FontSize = 15,
                VerticalAlignment = VerticalAlignment.Center,
            });

            var parts = new List<string> { change.Alias };
            if (!string.IsNullOrWhiteSpace(change.DeviceLabel))
                parts.Add(change.DeviceLabel!);
            string size = FormatModelSize(change.SizeBytes);
            if (size.Length > 0)
                parts.Add(size);

            row.Children.Add(new TextBlock
            {
                Text = string.Join("  \u00b7  ", parts),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            });

            var tagText = new TextBlock
            {
                Text = change.Kind == FoundryModelChangeKind.New ? "New" : "Updated",
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var tag = new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(7, 1, 7, 1),
                VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(change.Kind == FoundryModelChangeKind.New
                    ? Windows.UI.Color.FromArgb(0x40, 0x4C, 0x9E, 0xFF)
                    : Windows.UI.Color.FromArgb(0x40, 0x5C, 0xB8, 0x5C)),
                Child = tagText,
            };
            row.Children.Add(tag);
            list.Children.Add(row);
        }
        panel.Children.Add(list);

        var dontAlertAgain = new CheckBox
        {
            Content = "Don't alert me about new models again",
            Margin = new Thickness(0, 8, 0, 0),
        };
        panel.Children.Add(dontAlertAgain);

        var scroller = new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        return (scroller, dontAlertAgain);
    }

    private static string FormatModelSize(long? bytes)
    {
        if (bytes is not { } b || b <= 0)
            return string.Empty;
        double gb = b / (1024.0 * 1024 * 1024);
        if (gb >= 1)
            return $"{gb:0.#} GB";
        double mb = b / (1024.0 * 1024);
        return $"{mb:0} MB";
    }

    private async Task CheckEverythingAsync()
    {
        var esPath = FileLister.FindEsExe();
        bool everythingRunning = Process.GetProcessesByName("Everything").Length > 0;
        LogService.Instance.Info("MainWindow", $"CheckEverythingAsync: esPath={esPath ?? "(null)"}, everythingRunning={everythingRunning}");

        // Everything is running — SDK will work regardless of es.exe presence
        if (everythingRunning)
        {
            LogService.Instance.Info("MainWindow", "CheckEverythingAsync: Everything process is running — SDK will work, no action needed");
            return;
        }

        // es.exe found but Everything service not running — offer to start it
        if (esPath != null)
        {
            var everythingExe = FindEverythingExe(esPath);
            LogService.Instance.Info("MainWindow", $"CheckEverythingAsync: es.exe found at '{esPath}', Everything.exe resolve={everythingExe ?? "(null)"}");
            if (everythingExe != null)
            {
                if (await YaguDialog.ShowAsync(
                    _hwnd,
                    new YaguDialogOptions
                    {
                        Title = "Everything Search Not Running",
                        Content = "Everything Search is installed but not currently running.\nIt must be running for fast file discovery.\n\nWould you like to start it now?",
                        PrimaryButtonText = "Start Everything",
                        CloseButtonText = "Skip",
                        DefaultButton = YaguDialogDefaultButton.Primary,
                        ShowTitleBar = false,
                        Width = 560,
                        Height = 300,
                    }) == YaguDialogResult.Primary)
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = everythingExe,
                            UseShellExecute = true,
                        });
                        await WaitForEverythingReadyAndNotifyAsync();
                    }
                    catch (Exception ex)
                    {
                        ViewModel.StatusText = $"Could not start Everything: {ex.Message}. Using built-in file enumeration.";
                        LogService.Instance.Warning("MainWindow", "Failed to start Everything", ex);
                    }
                }
                return;
            }
        }

        // Check if Everything.exe exists in standard locations even without es.exe
        var everythingExeStandalone = FindEverythingExeStandalone();
        if (everythingExeStandalone != null)
        {
            LogService.Instance.Info("MainWindow", $"CheckEverythingAsync: Everything.exe found at '{everythingExeStandalone}' (no es.exe), offering to start");
            if (await YaguDialog.ShowAsync(
                _hwnd,
                new YaguDialogOptions
                {
                    Title = "Everything Search Not Running",
                    Content = "Everything Search is installed but not currently running.\nIt must be running for fast file discovery.\n\nWould you like to start it now?",
                    PrimaryButtonText = "Start Everything",
                    CloseButtonText = "Skip",
                    DefaultButton = YaguDialogDefaultButton.Primary,
                    ShowTitleBar = false,
                    Width = 560,
                    Height = 300,
                }) == YaguDialogResult.Primary)
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = everythingExeStandalone,
                        UseShellExecute = true,
                    });
                    await WaitForEverythingReadyAndNotifyAsync();
                }
                catch (Exception ex)
                {
                    ViewModel.StatusText = $"Could not start Everything: {ex.Message}. Using built-in file enumeration.";
                    LogService.Instance.Warning("MainWindow", "Failed to start Everything", ex);
                }
            }
            return;
        }

        // Nothing found — offer to download and install
        LogService.Instance.Warning("MainWindow", "CheckEverythingAsync: Everything not found anywhere — showing install dialog");
        bool installEverything = await YaguDialog.ShowAsync(
            _hwnd,
            new YaguDialogOptions
            {
                Title = "Everything Search Not Found",
                Content = "Everything Search by voidtools provides significantly faster file discovery.\n\nWould you like to download and install it?",
                PrimaryButtonText = "Install",
                CloseButtonText = "Skip",
                DefaultButton = YaguDialogDefaultButton.Primary,
                ShowTitleBar = false,
                Width = 560,
                Height = 280,
            }) == YaguDialogResult.Primary;

        if (!installEverything) return;

        bool is64Bit = Environment.Is64BitOperatingSystem;
        string url = is64Bit
            ? "https://www.voidtools.com/Everything-1.4.1.1032.x64-Setup.exe"
            : "https://www.voidtools.com/Everything-1.4.1.1032.x86-Setup.exe";
        string fileName = is64Bit ? "Everything-1.4.1.1032.x64-Setup.exe" : "Everything-1.4.1.1032.x86-Setup.exe";
        string tempPath = Path.Combine(Path.GetTempPath(), fileName);

        ViewModel.StatusText = "Downloading Everything Search installer\u2026";

        try
        {
            using var http = new HttpClient();
            var data = await http.GetByteArrayAsync(new Uri(url));
            await File.WriteAllBytesAsync(tempPath, data);

            ViewModel.StatusText = "Running Everything Search installer \u2014 please complete the setup wizard\u2026";

            var psi = new ProcessStartInfo
            {
                FileName = tempPath,
                Verb = "runas",
                UseShellExecute = true,
            };

            var proc = Process.Start(psi);
            if (proc != null)
            {
                await proc.WaitForExitAsync();
            }

            var installedEsPath = FileLister.FindEsExe();
            if (installedEsPath is null)
            {
                ViewModel.StatusText = "Installer completed. Restart Yagu if Everything was installed to a custom location.";
                return;
            }

            if (Process.GetProcessesByName("Everything").Length == 0)
            {
                var everythingExe = FindEverythingExe(installedEsPath);
                if (everythingExe != null)
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = everythingExe,
                            UseShellExecute = true,
                        });
                    }
                    catch (Exception ex)
                    {
                        LogService.Instance.Warning("MainWindow", "Failed to start Everything after install", ex);
                    }
                }
            }

            await WaitForEverythingReadyAndNotifyAsync();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            ViewModel.StatusText = "Everything Search installation was cancelled. Using built-in file enumeration.";
            LogService.Instance.Info("MainWindow", "Everything install UAC declined");
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = $"Failed to install Everything: {ex.Message}. Using built-in file enumeration.";
            LogService.Instance.Warning("MainWindow", "Everything install failed", ex);
        }
    }

    private async Task<bool> WaitForEverythingReadyAndNotifyAsync()
    {
        ViewModel.StatusText = "Waiting for Everything Search to return indexed files and folders...";
        var readiness = await FileLister.WaitForEverythingSdkReadyAsync(
            timeout: TimeSpan.FromSeconds(90),
            pollInterval: TimeSpan.FromSeconds(1),
            cancellationToken: CancellationToken.None);

        if (!readiness.IsReady)
        {
            ViewModel.StatusText = $"Everything Search is not ready yet: {readiness.Error}. Using built-in file enumeration.";
            return false;
        }

        uint indexedCount = readiness.TotalCount > 0 ? readiness.TotalCount : readiness.ReturnedCount;
        ViewModel.StatusText = $"Everything Search is ready - {indexedCount:N0} files and folders indexed.";

        await YaguDialog.ShowAsync(
            _hwnd,
            new YaguDialogOptions
            {
                Title = "Everything Search Ready",
                Content = $"Everything Search returned indexed files and folders through the SDK. Fast file discovery is ready to use.\n\nIndexed items reported: {indexedCount:N0}",
                CloseButtonText = "OK",
                DefaultButton = YaguDialogDefaultButton.Close,
                Width = 560,
                Height = 300,
            });
        return true;
    }

    private static string? FindEverythingExe(string esPath)
    {
        // Everything.exe is typically in the same directory as es.exe
        var dir = Path.GetDirectoryName(esPath);
        if (dir != null)
        {
            var candidate = Path.Combine(dir, "Everything.exe");
            if (File.Exists(candidate))
            {
                LogService.Instance.Info("MainWindow", $"FindEverythingExe: found at {candidate}");
                return candidate;
            }
        }
        // Check standard install locations
        foreach (var path in new[]
        {
            @"C:\Program Files\Everything\Everything.exe",
            @"C:\Program Files (x86)\Everything\Everything.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Everything", "Everything.exe"),
        })
        {
            if (File.Exists(path))
            {
                LogService.Instance.Info("MainWindow", $"FindEverythingExe: found at {path}");
                return path;
            }
        }
        LogService.Instance.Warning("MainWindow", $"FindEverythingExe: NOT FOUND (esPath was '{esPath}', dir was '{dir}')");
        return null;
    }

    private static string? FindEverythingExeStandalone()
    {
        // Check registry install dirs for Everything.exe even when es.exe wasn't found
        foreach (var installDir in FileLister.GetEverythingInstallDirsFromRegistry())
        {
            var candidate = Path.Combine(installDir, "Everything.exe");
            if (File.Exists(candidate))
            {
                LogService.Instance.Info("MainWindow", $"FindEverythingExeStandalone: found via registry at {candidate}");
                return candidate;
            }
        }
        // Standard install locations
        foreach (var path in new[]
        {
            @"C:\Program Files\Everything\Everything.exe",
            @"C:\Program Files (x86)\Everything\Everything.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Everything", "Everything.exe"),
        })
        {
            if (File.Exists(path))
            {
                LogService.Instance.Info("MainWindow", $"FindEverythingExeStandalone: found at {path}");
                return path;
            }
        }
        LogService.Instance.Info("MainWindow", "FindEverythingExeStandalone: Everything.exe not found in any standard location");
        return null;
    }

    private async Task CheckFirstRunResultStoreTempLocationAsync()
    {
        if (ViewModel.HasChosenSearchResultTempDirectory &&
            ResultStoreTempLocationService.IsUsableTempDirectory(ViewModel.SearchResultTempDirectory, requireMinimumFreeSpace: false))
        {
            return;
        }

        string? launchDrive = ResultStoreTempLocationService.GetLaunchDriveRoot();
        var options = ResultStoreTempLocationService.GetWritableDriveOptions(launchDrive);

        ResultStoreTempLocationWindowResult result;
        _ownedModalWindowDepth++;
        try
        {
            result = await ResultStoreTempLocationWindow.ShowAsync(
                _hwnd,
                launchDrive,
                options,
                ViewModel.SearchResultTempDirectory);
        }
        finally
        {
            _ownedModalWindowDepth = Math.Max(0, _ownedModalWindowDepth - 1);
        }

        if (!result.Accepted)
            return;

        ViewModel.SearchResultTempDirectory = result.SelectedOption?.TempDirectory ?? Path.GetTempPath();
        ViewModel.HasChosenSearchResultTempDirectory = true;
        await ViewModel.PersistSettingsAsync();
    }
}
