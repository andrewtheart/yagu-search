using System.Diagnostics;
using Microsoft.UI.Xaml;
using Windows.System;
using Yagu.Services;
namespace Yagu;

/// <summary>
/// Content-loaded startup flow, Everything detection, and first-run result-store location prompts.
/// </summary>
public sealed partial class MainWindow
{
    private async void OnContentLoaded(object sender, RoutedEventArgs e)
    {
        ((FrameworkElement)sender).Loaded -= OnContentLoaded;
        AlignBrowseButtonToSearchButton();
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
    }

    private void AlignBrowseButtonToSearchButton()
    {
        if (_topSearchDrawerCompact) return;
        if (SearchCancelButton.ActualWidth <= 0) return;
        BrowseDirectoryButton.Width = SearchCancelButton.ActualWidth;
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
