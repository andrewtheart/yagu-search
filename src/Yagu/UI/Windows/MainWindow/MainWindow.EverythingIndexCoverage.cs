using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using Yagu.Services;
using Yagu.Services.Logging;

namespace Yagu;

public sealed partial class MainWindow
{
    /// <summary>
    /// Before a GUI search touches Everything, read its active INI and warn when one or more search
    /// roots are not covered by an enabled volume/folder index. The check is deliberately read-only:
    /// Everything's INI says the process must be stopped before editing it, and the official CLI only
    /// rescans/reindexes already-configured roots. A missing/unreadable config fails open with no warning.
    /// </summary>
    private async Task<bool> CheckEverythingIndexCoverageAndWarnAsync()
    {
        if (ViewModel.SuppressEverythingIndexCoverageWarning)
            return true;

        // The built-in .NET backend does not consume Everything, so its index coverage is irrelevant.
        if (ViewModel.FileListerBackendIndex == (int)FileListerBackend.Managed)
            return true;
        // This per-search all-drive option also forces the managed walker regardless of the global backend.
        if (string.IsNullOrWhiteSpace(ViewModel.Directory) && ViewModel.SearchAllDrivesForceFullScan)
            return true;

        string? esPath = FileLister.FindEsExe();
        string? everythingExe = esPath is not null ? FindEverythingExe(esPath) : FindEverythingExeStandalone();
        if (everythingExe is null)
            return true; // not installed — the existing installer/startup flow owns that case
        bool everythingRunning = false;
        try
        {
            Process[] processes = Process.GetProcessesByName("Everything");
            everythingRunning = processes.Length > 0;
            foreach (Process process in processes) process.Dispose();
        }
        catch { /* unknown -> false; saved config is the only available source */ }

        IReadOnlyList<string> targets = ViewModel.ResolveTargetRoots();
        if (targets.Count == 0)
            return true;

        IReadOnlyList<string>? uncovered;
        try
        {
            uncovered = await Task.Run(() =>
                EverythingIndexCoverageDetector.FindConfirmedUncoveredPaths(
                    targets, everythingExe, everythingRunning)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            YaguLog.For("Everything").LogDebug(ex, "Everything index-coverage preflight failed; skipping warning.");
            return true;
        }
        if (uncovered is null || uncovered.Count == 0)
            return true;

        string[] roots = uncovered
            .Select(GetEverythingCoverageDisplayRoot)
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(root => root, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (roots.Length == 0)
            return true;

        YaguLog.For("Everything").LogInformation(
            "Everything index-coverage warning: uncovered search root(s) [{Roots}] for target(s) [{Targets}].",
            string.Join(", ", roots), string.Join(", ", uncovered));

        var panel = new StackPanel { Spacing = 12, MinWidth = 440 };
        panel.Children.Add(new TextBlock
        {
            Text = roots.Length == 1
                ? "The following drive does not appear to be in Everything's index:"
                : "The following drives do not appear to be in Everything's index:",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
        });
        panel.Children.Add(new TextBlock
        {
            Text = string.Join(Environment.NewLine, roots),
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            FontSize = 17,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(12, 0, 0, 0),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Adding these root drives to the Everything index is highly recommended for the fastest initial filename-based search results.",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "To add a drive, open Everything and go to Tools → Options → Indexes. Add the root drive shown above (for example, D:), not only the nested folder you searched.",
            TextWrapping = TextWrapping.Wrap,
        });
        var automaticStatus = new TextBlock
        {
            FontSize = 11,
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap,
        };
        var addAutomatically = new Button
        {
            Content = roots.Length == 1 ? "Add drive to Everything now" : "Add drives to Everything now",
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 5, 10, 5),
        };
        YaguDialog? coverageDialog = null;
        addAutomatically.Click += async (_, _) =>
        {
            addAutomatically.IsEnabled = false;
            addAutomatically.Content = "Adding to Everything…";
            automaticStatus.Text = "Everything may take a few minutes to finish scanning a large FAT/exFAT drive.";
            bool added;
            try
            {
                added = await EverythingIndexConfigurator.AddVolumesAndRescanAsync(
                    everythingExe, uncovered).ConfigureAwait(true);
            }
            catch (OperationCanceledException) { added = false; }

            if (added)
            {
                automaticStatus.Text = "The drive was added. Everything is now indexing/rescanning it; filename results will become available as that scan progresses.";
                addAutomatically.Content = "Added to Everything";
                coverageDialog?.AcceptClose();
            }
            else
            {
                automaticStatus.Text = "Yagu could not add the drive automatically. Add it manually in Everything → Tools → Options → Indexes.";
                addAutomatically.Content = "Try adding again";
                addAutomatically.IsEnabled = true;
            }
        };
        panel.Children.Add(addAutomatically);
        panel.Children.Add(automaticStatus);
        var dontWarnAgain = new CheckBox
        {
            Content = "Don't warn me again",
            Margin = new Thickness(0, 4, 0, 0),
        };
        panel.Children.Add(dontWarnAgain);

        await YaguDialog.ShowAsync(
            _hwnd,
            new YaguDialogOptions
            {
                Title = "Drive not indexed by Everything",
                TitleGlyph = "\uE721", // Search
                Content = panel,
                PrimaryButtonText = "Ok, I added it",
                SecondaryButtonText = "Ignore for now",
                CloseButtonText = null,
                DefaultButton = YaguDialogDefaultButton.Secondary,
                RequestedTheme = RootGrid.ActualTheme,
                ShowTitleBar = false,
                ShowTopRightCloseButton = true,
                Width = 640,
                Height = 470,
                MaxContentHeight = 380,
            },
            dialog => coverageDialog = dialog);

        if (dontWarnAgain.IsChecked == true)
        {
            ViewModel.SuppressEverythingIndexCoverageWarning = true;
            await ViewModel.PersistSettingsAsync().ConfigureAwait(true);
        }

        // Informational only: both buttons continue the search. If the user added the drive while the
        // dialog was open, the immediately-following Everything query sees the newly configured index.
        return true;
    }

    private static string GetEverythingCoverageDisplayRoot(string path)
    {
        try
        {
            string root = Path.GetPathRoot(path.Replace('/', '\\')) ?? path;
            if (root.Length >= 2 && root[1] == ':')
                return root[..2]; // "D:" — ask for the root drive, never the nested searched folder
            return root.TrimEnd('\\');
        }
        catch { return path; }
    }
}
