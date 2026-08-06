using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Yagu.UI;

/// <summary>
/// An in-app folder browser rendered as a themed <see cref="Flyout"/>: a path bar, an "up" control, a
/// scrollable list of drives/subfolders, and explicit "all drives" / "use this folder" actions.
/// <para>
/// Used where a picker must appear anchored to a control inside another surface (the Quick searches
/// editor lives inside the Advanced Options drawer, and opening the modal Win32 folder dialog from there
/// light-dismisses the drawer underneath it). Everywhere a plain modal picker is fine,
/// <c>Win32FileDialog.SelectFolder</c> remains the right choice.
/// </para>
/// All directory enumeration runs on a background thread — <c>DriveInfo.GetDrives</c> and
/// <c>Directory.GetDirectories</c> can block for seconds on removable or network volumes.
/// </summary>
internal static class FolderBrowseFlyout
{
    /// <summary>Result of one background listing: the child folders, or why they could not be read.</summary>
    private sealed class Listing
    {
        public List<(string Display, string Path)> Entries { get; } = [];
        public string? Error { get; set; }
    }

    /// <summary>
    /// Builds the flyout. <paramref name="getInitialPath"/> is read each time it opens so the browser
    /// starts where the field currently points; <paramref name="onPicked"/> receives the chosen folder,
    /// or an empty string when the user chooses to search every drive.
    /// </summary>
    public static Flyout Create(Func<string?> getInitialPath, Action<string> onPicked)
    {
        ArgumentNullException.ThrowIfNull(getInitialPath);
        ArgumentNullException.ThrowIfNull(onPicked);

        var flyout = new Flyout { ShouldConstrainToRootBounds = false };
        string? current = null;

        var pathBox = new TextBox
        {
            PlaceholderText = "All drives",
            VerticalAlignment = VerticalAlignment.Center,
        };
        var upButton = new Button
        {
            Content = new FontIcon { Glyph = "\uE74A", FontSize = 12 },
            Width = 32,
            Height = 32,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(upButton, "Up one level");

        var listPanel = new StackPanel { Spacing = 1 };
        var scroller = new ScrollViewer
        {
            Content = listPanel,
            Height = 220,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        var status = new TextBlock
        {
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };

        var allDrivesButton = new Button
        {
            Content = "Search all drives",
            MinWidth = 0,
            Padding = new Thickness(12, 4, 12, 4),
        };
        ToolTipService.SetToolTip(allDrivesButton, "Clear the folder so the search starts at the root of every drive.");
        var selectButton = new Button
        {
            Content = "Use this folder",
            MinWidth = 0,
            Padding = new Thickness(12, 4, 12, 4),
            Style = Application.Current.Resources["AccentButtonStyle"] as Style,
        };

        void Pick(string value)
        {
            onPicked(value);
            flyout.Hide();
        }

        async void Navigate(string? path)
        {
            current = string.IsNullOrWhiteSpace(path) ? null : path!.Trim();
            pathBox.Text = current ?? string.Empty;
            selectButton.IsEnabled = current is not null;
            listPanel.Children.Clear();
            status.Text = "Loading\u2026";
            status.Visibility = Visibility.Visible;

            Listing listing = await Task.Run(() => Enumerate(current)).ConfigureAwait(true);

            listPanel.Children.Clear();
            if (listing.Error is { } error)
            {
                status.Text = error;
                status.Visibility = Visibility.Visible;
                return;
            }

            status.Visibility = listing.Entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            status.Text = current is null ? "No drives found." : "This folder has no subfolders.";

            foreach (var (display, fullPath) in listing.Entries)
            {
                var content = new Grid { ColumnSpacing = 8 };
                content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                content.Children.Add(new FontIcon
                {
                    Glyph = current is null ? "\uEDA2" : "\uE8B7",
                    FontSize = 14,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                var text = new TextBlock
                {
                    Text = display,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextWrapping = TextWrapping.NoWrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                Grid.SetColumn(text, 1);
                content.Children.Add(text);

                var row = new Button
                {
                    Content = content,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Background = null,
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(8, 6, 8, 6),
                };
                row.Click += (_, _) => Navigate(fullPath);
                ToolTipService.SetToolTip(row, fullPath);
                listPanel.Children.Add(row);
            }
        }

        upButton.Click += (_, _) => Navigate(ParentOf(current));
        allDrivesButton.Click += (_, _) => Pick(string.Empty);
        selectButton.Click += (_, _) => Pick(current ?? string.Empty);
        pathBox.KeyDown += (_, e) =>
        {
            if (e.Key != Windows.System.VirtualKey.Enter)
                return;
            e.Handled = true;
            Navigate(pathBox.Text);
        };

        var pathRow = new Grid { ColumnSpacing = 6 };
        pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pathRow.Children.Add(upButton);
        Grid.SetColumn(pathBox, 1);
        pathRow.Children.Add(pathBox);

        var hint = new TextBlock
        {
            Text = "Leave the folder empty to search every drive from its root.",
            FontSize = 12,
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        actions.Children.Add(allDrivesButton);
        actions.Children.Add(selectButton);

        // A wrapping hint inside a horizontal StackPanel would measure at infinite width and never wrap.
        var footer = new Grid { ColumnSpacing = 10 };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.Children.Add(hint);
        Grid.SetColumn(actions, 1);
        footer.Children.Add(actions);

        var panel = new StackPanel { Spacing = 8, Width = 420 };
        panel.Children.Add(pathRow);
        panel.Children.Add(scroller);
        panel.Children.Add(status);
        panel.Children.Add(footer);

        flyout.Content = panel;
        flyout.Opened += (_, _) => Navigate(getInitialPath());
        return flyout;
    }

    /// <summary>Drives when <paramref name="path"/> is null, otherwise its subfolders. Runs off the UI thread.</summary>
    private static Listing Enumerate(string? path)
    {
        var listing = new Listing();
        try
        {
            if (path is null)
            {
                foreach (var drive in DriveInfo.GetDrives())
                {
                    string label = string.Empty;
                    try
                    {
                        if (drive.IsReady)
                            label = drive.VolumeLabel;
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }

                    string name = drive.Name.TrimEnd('\\');
                    listing.Entries.Add((label.Length == 0 ? name : $"{name}  {label}", drive.Name));
                }
                return listing;
            }

            if (!System.IO.Directory.Exists(path))
            {
                listing.Error = "That folder does not exist.";
                return listing;
            }

            foreach (string child in System.IO.Directory.EnumerateDirectories(path))
                listing.Entries.Add((Path.GetFileName(child), child));

            listing.Entries.Sort(static (a, b) => string.Compare(a.Display, b.Display, StringComparison.OrdinalIgnoreCase));
        }
        catch (UnauthorizedAccessException)
        {
            listing.Error = "You do not have permission to browse that folder.";
        }
        catch (IOException ex)
        {
            listing.Error = ex.Message;
        }
        return listing;
    }

    /// <summary>The parent folder, or null once the walk reaches the drive list.</summary>
    private static string? ParentOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        try
        {
            return System.IO.Directory.GetParent(path.TrimEnd('\\', '/'))?.FullName;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
