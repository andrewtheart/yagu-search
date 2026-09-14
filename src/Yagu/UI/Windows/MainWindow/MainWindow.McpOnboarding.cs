using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Yagu.Helpers;
using Yagu.Services;
using Yagu.Services.Mcp;

namespace Yagu;

/// <summary>
/// First-run prompt that grants an installed MCP server its initial folders.
/// </summary>
/// <remarks>
/// Installing the optional server copies files but grants nothing, and a server with no folder refuses
/// to run. Without this the feature looks installed while every client Install button stays disabled,
/// which is exactly how it reads to someone who ticked the box in the installer.
/// </remarks>
public sealed partial class MainWindow
{
    private async Task CheckFirstRunMcpAllowedRootsAsync()
    {
        // RunStartupDialogStepAsync invokes every step regardless of the plan — the plan only drives the
        // progress badge — so the real gate has to be re-checked here or this returns on every launch.
        if (!WillShowMcpAllowedRootsPrompt())
            return;

        // Another modal is up, so retry on the next launch rather than stacking on top of it.
        if (YaguDialog.HasOpenOwnedWindow(_hwnd))
            return;

        AppSettings settings = ViewModel.Settings;
        bool serverInstalled = McpClientSetup.IsServerInstalled();

        var checkBoxes = new List<CheckBox>();
        var listPanel = new StackPanel { Spacing = 2 };
        var clientRows = new StackPanel { Spacing = 4, Margin = new Thickness(0, 6, 0, 0) };
        var status = new TextBlock
        {
            FontSize = 12,
            Opacity = 0.85,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        };

        List<string> SelectedRoots() => checkBoxes
            .Where(box => box.IsChecked == true)
            .Select(box => (string)box.Tag)
            .ToList();

        // Registering a client bakes the folders into that client's command line, so they have to be
        // saved before the entry is generated — not when the dialog is finally dismissed.
        void CommitRoots()
        {
            List<string> chosen = SelectedRoots();
            if (chosen.Count == 0)
                return;

            settings.McpAllowedRoots =
                AppSettings.NormalizeMcpAllowedRoots([.. settings.McpAllowedRoots, .. chosen]);
        }

        void RefreshClientRows()
        {
            clientRows.Children.Clear();
            // A committed root stays checked, so summing the two lists would count it twice.
            int grantedCount = AppSettings
                .NormalizeMcpAllowedRoots([.. settings.McpAllowedRoots, .. SelectedRoots()]).Count;

            foreach (McpClientKind client in Enum.GetValues<McpClientKind>())
            {
                McpClientKind captured = client;
                bool clientInstalled = McpClientSetup.IsInstalled(client);

                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                row.Children.Add(new TextBlock
                {
                    Text = McpConfigSnippetBuilder.DisplayName(client),
                    VerticalAlignment = VerticalAlignment.Center,
                    Width = 120,
                });
                row.Children.Add(new TextBlock
                {
                    Text = clientInstalled ? "installed" : "not installed",
                    VerticalAlignment = VerticalAlignment.Center,
                    Opacity = 0.75,
                    FontSize = 12,
                    Width = 90,
                });

                var install = new Button
                {
                    Content = clientInstalled ? "Reinstall" : "Install",
                    Padding = new Thickness(10, 2, 10, 2),
                    IsEnabled = serverInstalled && grantedCount > 0,
                };
                install.Click += (_, _) =>
                {
                    CommitRoots();
                    status.Text = McpClientSetup.Describe(
                        settings, captured, McpClientSetup.Apply(settings, captured, install: true));
                    RefreshClientRows();
                };
                row.Children.Add(DisabledReasonTooltip.Wrap(
                    install, McpClientSetup.DescribeInstallBlock(serverInstalled, grantedCount)));

                var remove = new Button
                {
                    Content = "Remove",
                    Padding = new Thickness(10, 2, 10, 2),
                    IsEnabled = clientInstalled,
                };
                remove.Click += (_, _) =>
                {
                    status.Text = McpClientSetup.Describe(
                        settings, captured, McpClientSetup.Apply(settings, captured, install: false));
                    RefreshClientRows();
                };
                row.Children.Add(DisabledReasonTooltip.Wrap(remove, McpClientSetup.DescribeRemoveBlock(clientInstalled)));

                clientRows.Children.Add(row);
            }
        }

        void AddCandidate(string folder, bool isChecked)
        {
            if (checkBoxes.Any(c => string.Equals((string)c.Tag, folder, StringComparison.OrdinalIgnoreCase)))
                return;

            var box = new CheckBox
            {
                Content = folder,
                Tag = folder,
                IsChecked = isChecked,
                MinWidth = 0,
            };
            // Ticking a folder is what unblocks the Install buttons below.
            box.Checked += (_, _) => RefreshClientRows();
            box.Unchecked += (_, _) => RefreshClientRows();
            checkBoxes.Add(box);
            listPanel.Children.Add(box);
        }

        foreach (string folder in BuildMcpRootSuggestions())
            AddCandidate(folder, isChecked: false);

        var intro = new TextBlock
        {
            Text = "Yagu's MCP server is installed, but it has no folders yet — and a server with no folder "
                 + "refuses to run. Tick every folder an AI assistant may search and read. "
                 + "You can change this at any time in Settings → MCP server.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        };

        var emptyNote = new TextBlock
        {
            Text = "No folders suggested yet — use Browse to pick one.",
            Opacity = 0.75,
            FontSize = 12,
            Visibility = checkBoxes.Count == 0 ? Visibility.Visible : Visibility.Collapsed,
            TextWrapping = TextWrapping.Wrap,
        };

        var browseButton = new Button
        {
            Content = "Browse\u2026",
            Padding = new Thickness(12, 4, 12, 4),
            Margin = new Thickness(0, 8, 0, 0),
        };

        var content = new StackPanel();
        content.Children.Add(intro);
        content.Children.Add(emptyNote);
        content.Children.Add(new ScrollViewer
        {
            Content = listPanel,
            MaxHeight = 190,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        });
        content.Children.Add(browseButton);
        content.Children.Add(new TextBlock
        {
            Text = "Connect a client",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 18, 0, 4),
        });
        content.Children.Add(new TextBlock
        {
            Text = "Yagu merges its entry into the client's configuration — it never replaces the file, so other "
                 + "servers and comments are kept. Installing here saves the folders ticked above first.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = 0.85,
        });
        content.Children.Add(clientRows);
        content.Children.Add(status);

        IntPtr dialogHwnd = _hwnd;
        browseButton.Click += (_, _) =>
        {
            // Parented to the dialog, not the main window, or the picker opens behind this modal.
            string? folder = Win32FileDialog.SelectFolder(dialogHwnd, "Select Folder an MCP Client May Access");
            if (string.IsNullOrWhiteSpace(folder))
                return;

            AddCandidate(folder, isChecked: true);
            emptyNote.Visibility = Visibility.Collapsed;
            RefreshClientRows();
        };

        RefreshClientRows();

        YaguDialogResult result;
        _ownedModalWindowDepth++;
        try
        {
            result = await YaguDialog.ShowAsync(
                _hwnd,
                new YaguDialogOptions
                {
                    Title = "Set up the MCP server",
                    TitleGlyph = "\uE8B7",
                    Content = content,
                    PrimaryButtonText = "Save folders",
                    CloseButtonText = "Not now",
                    ShowTitleBar = false,
                    Width = 660,
                    Height = 700,
                    MaxContentHeight = 560,
                },
                dialog => dialogHwnd = WinRT.Interop.WindowNative.GetWindowHandle(dialog));
        }
        finally
        {
            _ownedModalWindowDepth = Math.Max(0, _ownedModalWindowDepth - 1);
        }

        // Answered either way: offering this on every launch would be nagging, and Settings still has it.
        settings.McpAllowedRootsPromptShown = true;

        if (result == YaguDialogResult.Primary)
            CommitRoots();

        await ViewModel.PersistSettingsAsync();
    }

    /// <summary>
    /// Folders worth offering: where the user already searches, then their profile folders.
    /// </summary>
    /// <remarks>
    /// Ticking boxes is the whole point — being made to browse once per folder is what makes granting
    /// several folders tedious enough that people grant one and move on.
    /// </remarks>
    private List<string> BuildMcpRootSuggestions()
    {
        var ordered = new List<string>();

        void Offer(string? folder)
        {
            if (string.IsNullOrWhiteSpace(folder))
                return;

            string trimmed = folder.Trim();
            if (!Directory.Exists(trimmed))
                return;
            if (ordered.Any(existing => string.Equals(existing, trimmed, StringComparison.OrdinalIgnoreCase)))
                return;

            ordered.Add(trimmed);
        }

        Offer(ViewModel.Directory);
        foreach (string recent in ViewModel.Settings.RecentDirectories)
            Offer(recent);

        foreach (Environment.SpecialFolder special in new[]
                 {
                     Environment.SpecialFolder.MyDocuments,
                     Environment.SpecialFolder.DesktopDirectory,
                     Environment.SpecialFolder.MyPictures,
                 })
        {
            Offer(Environment.GetFolderPath(special));
        }

        // Deliberately no extra cap here. The only open-ended source is RecentDirectories, which is
        // already bounded by the user's "Recent items" setting, and the list renders inside a scrolling
        // container -- so a second, hidden ceiling would only hide folders the user asked to remember.
        return ordered;
    }
}
