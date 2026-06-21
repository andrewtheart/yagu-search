using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Yagu.Services;

namespace Yagu;

internal enum SessionLoadDialogAction
{
    None,
    Load,
    Browse,
}

internal sealed record SessionLoadDialogResult(SessionLoadDialogAction Action, string? Path)
{
    public static SessionLoadDialogResult None { get; } = new(SessionLoadDialogAction.None, null);
    public static SessionLoadDialogResult Browse { get; } = new(SessionLoadDialogAction.Browse, null);
    public static SessionLoadDialogResult Load(string path) => new(SessionLoadDialogAction.Load, path);
}

internal static class SessionLoadDialog
{
    public static async Task<SessionLoadDialogResult> ShowAsync(
        IntPtr ownerHwnd,
        IReadOnlyList<SessionFileCandidate> sessions,
        ElementTheme requestedTheme = ElementTheme.Default)
    {
        string? selectedPath = null;
        bool completed = false;
        YaguDialog? dialog = null;
        var content = BuildContent(sessions, path =>
        {
            if (completed)
                return;

            completed = true;
            selectedPath = path;
            dialog?.AcceptSecondary();
        });

        var result = await YaguDialog.ShowAsync(
            ownerHwnd,
            new YaguDialogOptions
            {
                Title = "Load session",
                Content = content,
                PrimaryButtonText = "Browse...",
                CloseButtonText = "Cancel",
                DefaultButton = YaguDialogDefaultButton.Close,
                RequestedTheme = requestedTheme,
                Width = 860,
                Height = 620,
                MinContentHeight = 360,
                MaxContentHeight = 480,
                IsResizable = true,
                ShowTitle = false,
                ShowTitleBar = false,
            },
            createdDialog => dialog = createdDialog).ConfigureAwait(true);

        return result switch
        {
            YaguDialogResult.Primary => SessionLoadDialogResult.Browse,
            YaguDialogResult.Secondary when !string.IsNullOrWhiteSpace(selectedPath) => SessionLoadDialogResult.Load(selectedPath),
            _ => SessionLoadDialogResult.None,
        };
    }

    private static Grid BuildContent(IReadOnlyList<SessionFileCandidate> sessions, Action<string> loadPath)
    {
        var root = new Grid
        {
            MinWidth = 760,
            RowSpacing = 14,
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new StackPanel
        {
            Spacing = 6,
        };
        header.Children.Add(new TextBlock
        {
            Text = "Load session",
            FontSize = 22,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.WrapWholeWords,
        });
        header.Children.Add(new TextBlock
        {
            Text = "Saved sessions reopen previous Yagu results without rerunning the search. Select a .yagu-session file from the list, or choose Browse... to pick one manually.",
            FontSize = 13,
            Opacity = 0.78,
            TextWrapping = TextWrapping.WrapWholeWords,
        });

        var summary = new TextBlock
        {
            Text = sessions.Count == 0
                ? "No .yagu-session files found by Everything."
                : $"{sessions.Count:N0} .yagu-session file{(sessions.Count == 1 ? string.Empty : "s")} found",
            FontSize = 13,
            Opacity = 0.72,
            TextWrapping = TextWrapping.WrapWholeWords,
        };
        header.Children.Add(summary);
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        FrameworkElement body = sessions.Count == 0
            ? BuildEmptyState()
            : BuildSessionTable(sessions, loadPath);
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        return root;
    }

    private enum SortColumn { Name, Directory, Size, Created }

    private static FrameworkElement BuildSessionTable(IReadOnlyList<SessionFileCandidate> sessions, Action<string> loadPath)
    {
        var sortedSessions = sessions.OrderByDescending(s => s.CreatedUtc ?? DateTimeOffset.MinValue).ToList();
        var currentSort = SortColumn.Created;
        var currentAscending = false;

        var container = new Grid();
        container.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        container.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // Column header
        var headerGrid = new Grid { Padding = new Thickness(10, 6, 10, 6) };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) }); // Name
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // Directory
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) }); // Size
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) }); // Created

        TextBlock nameHeader = CreateSortableHeader("Name", SortColumn.Name);
        TextBlock dirHeader = CreateSortableHeader("Directory", SortColumn.Directory);
        TextBlock sizeHeader = CreateSortableHeader("Size", SortColumn.Size);
        TextBlock createdHeader = CreateSortableHeader("Created \u25BC", SortColumn.Created);

        Grid.SetColumn(nameHeader, 0);
        Grid.SetColumn(dirHeader, 1);
        Grid.SetColumn(sizeHeader, 2);
        Grid.SetColumn(createdHeader, 3);
        headerGrid.Children.Add(nameHeader);
        headerGrid.Children.Add(dirHeader);
        headerGrid.Children.Add(sizeHeader);
        headerGrid.Children.Add(createdHeader);

        Grid.SetRow(headerGrid, 0);
        container.Children.Add(headerGrid);

        // List
        var list = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            IsItemClickEnabled = true,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            MinHeight = 240,
            MaxHeight = 400,
        };

        void RebuildList()
        {
            list.Items.Clear();
            foreach (var session in sortedSessions)
            {
                var item = new ListViewItem
                {
                    Tag = session,
                    Content = BuildTableRow(session),
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Padding = new Thickness(10, 6, 10, 6),
                };
                item.Tapped += (_, _) => loadPath(session.Path);
                item.DoubleTapped += (_, _) => loadPath(session.Path);
                item.KeyDown += (_, args) =>
                {
                    if (args.Key == Windows.System.VirtualKey.Enter)
                    {
                        args.Handled = true;
                        loadPath(session.Path);
                    }
                };
                list.Items.Add(item);
            }
        }

        RebuildList();

        list.ItemClick += (_, args) =>
        {
            if (TryGetSessionCandidate(args.ClickedItem, out var session))
                loadPath(session.Path);
        };
        list.KeyDown += (_, args) =>
        {
            if (args.Key != Windows.System.VirtualKey.Enter)
                return;
            if (list.SelectedItem is ListViewItem { Tag: SessionFileCandidate session })
            {
                args.Handled = true;
                loadPath(session.Path);
            }
        };

        void SortBy(SortColumn column)
        {
            if (currentSort == column)
                currentAscending = !currentAscending;
            else
            {
                currentSort = column;
                currentAscending = column is SortColumn.Name or SortColumn.Directory;
            }

            sortedSessions = currentSort switch
            {
                SortColumn.Name => currentAscending
                    ? sortedSessions.OrderBy(s => System.IO.Path.GetFileName(s.Path), StringComparer.OrdinalIgnoreCase).ToList()
                    : sortedSessions.OrderByDescending(s => System.IO.Path.GetFileName(s.Path), StringComparer.OrdinalIgnoreCase).ToList(),
                SortColumn.Directory => currentAscending
                    ? sortedSessions.OrderBy(s => System.IO.Path.GetDirectoryName(s.Path) ?? "", StringComparer.OrdinalIgnoreCase).ToList()
                    : sortedSessions.OrderByDescending(s => System.IO.Path.GetDirectoryName(s.Path) ?? "", StringComparer.OrdinalIgnoreCase).ToList(),
                SortColumn.Size => currentAscending
                    ? sortedSessions.OrderBy(s => s.SizeBytes ?? 0).ToList()
                    : sortedSessions.OrderByDescending(s => s.SizeBytes ?? 0).ToList(),
                SortColumn.Created => currentAscending
                    ? sortedSessions.OrderBy(s => s.CreatedUtc ?? DateTimeOffset.MinValue).ToList()
                    : sortedSessions.OrderByDescending(s => s.CreatedUtc ?? DateTimeOffset.MinValue).ToList(),
                _ => sortedSessions,
            };

            string arrow = currentAscending ? " \u25B2" : " \u25BC";
            nameHeader.Text = "Name" + (currentSort == SortColumn.Name ? arrow : "");
            dirHeader.Text = "Directory" + (currentSort == SortColumn.Directory ? arrow : "");
            sizeHeader.Text = "Size" + (currentSort == SortColumn.Size ? arrow : "");
            createdHeader.Text = "Created" + (currentSort == SortColumn.Created ? arrow : "");

            RebuildList();
        }

        nameHeader.Tapped += (_, _) => SortBy(SortColumn.Name);
        dirHeader.Tapped += (_, _) => SortBy(SortColumn.Directory);
        sizeHeader.Tapped += (_, _) => SortBy(SortColumn.Size);
        createdHeader.Tapped += (_, _) => SortBy(SortColumn.Created);

        Grid.SetRow(list, 1);
        container.Children.Add(list);

        return container;
    }

    private static TextBlock CreateSortableHeader(string text, SortColumn _)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Opacity = 0.8,
            VerticalAlignment = VerticalAlignment.Center,
            IsTextSelectionEnabled = false,
        };
    }

    private static Grid BuildTableRow(SessionFileCandidate session)
    {
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) }); // Name
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // Directory
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) }); // Size
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) }); // Created

        string fileName = System.IO.Path.GetFileName(session.Path);
        string directory = System.IO.Path.GetDirectoryName(session.Path) ?? "";

        var nameBlock = new TextBlock
        {
            Text = fileName,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(nameBlock, fileName);
        Grid.SetColumn(nameBlock, 0);
        row.Children.Add(nameBlock);

        var dirBlock = new TextBlock
        {
            Text = directory,
            FontSize = 12,
            Opacity = 0.7,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(dirBlock, directory);
        Grid.SetColumn(dirBlock, 1);
        row.Children.Add(dirBlock);

        var sizeBlock = new TextBlock
        {
            Text = session.SizeBytes.HasValue ? FormatByteSize(session.SizeBytes.Value) : "—",
            FontSize = 12,
            Opacity = 0.7,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(sizeBlock, 2);
        row.Children.Add(sizeBlock);

        var createdBlock = new TextBlock
        {
            Text = session.CreatedUtc.HasValue
                ? session.CreatedUtc.Value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
                : "—",
            FontSize = 12,
            Opacity = 0.7,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(createdBlock, 3);
        row.Children.Add(createdBlock);

        return row;
    }

    private static bool TryGetSessionCandidate(object? value, out SessionFileCandidate session)
    {
        switch (value)
        {
            case SessionFileCandidate candidate:
                session = candidate;
                return true;
            case ListViewItem { Tag: SessionFileCandidate candidate }:
                session = candidate;
                return true;
            case FrameworkElement { Tag: SessionFileCandidate candidate }:
                session = candidate;
                return true;
            default:
                session = null!;
                return false;
        }
    }

    private static Border BuildEmptyState()
    {
        var border = new Border
        {
            BorderBrush = ResourceBrush("CardStrokeColorDefaultBrush", Microsoft.UI.Colors.DimGray),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(20),
            MinHeight = 300,
            Child = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Spacing = 8,
                Children =
                {
                    new FontIcon
                    {
                        Glyph = "\uE8A5",
                        FontSize = 28,
                        Opacity = 0.65,
                    },
                    new TextBlock
                    {
                        Text = "No .yagu-session files found",
                        FontSize = 16,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        HorizontalAlignment = HorizontalAlignment.Center,
                    },
                },
            },
        };

        return border;
    }

    private static string FormatByteSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = Math.Max(0, bytes);
        int unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{value:N0} {units[unitIndex]}"
            : $"{value:N1} {units[unitIndex]}";
    }

    private static Brush ResourceBrush(string key, Windows.UI.Color fallback)
    {
        if (Application.Current.Resources.TryGetValue(key, out object resource) && resource is Brush brush)
            return brush;

        return new SolidColorBrush(fallback);
    }
}