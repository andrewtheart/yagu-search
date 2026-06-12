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

internal enum SessionLoadSortColumn
{
    Modified,
    FileName,
    ParentDirectory,
    Size,
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
            RowSpacing = 16,
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new StackPanel
        {
            Spacing = 5,
        };
        header.Children.Add(new TextBlock
        {
            Text = "Pick a saved session",
            FontSize = 22,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        header.Children.Add(new TextBlock
        {
            Text = "Choose a .yagu-session file to restore saved results. Use the column headers to sort by file name, parent folder, size, or last modified time.",
            FontSize = 13,
            Opacity = 0.72,
            TextWrapping = TextWrapping.WrapWholeWords,
        });
        header.Children.Add(new TextBlock
        {
            Text = sessions.Count == 0
                ? "No .yagu-session files found by Everything."
                : $"{sessions.Count:N0} .yagu-session file{(sessions.Count == 1 ? string.Empty : "s")} found",
            FontSize = 12,
            Opacity = 0.58,
        });
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        FrameworkElement body = sessions.Count == 0
            ? BuildEmptyState()
            : BuildSessionTable(sessions, loadPath);
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        return root;
    }

    private static Border BuildSessionTable(IReadOnlyList<SessionFileCandidate> sessions, Action<string> loadPath)
    {
        var table = new Grid
        {
            RowSpacing = 0,
            MinHeight = 300,
            MaxHeight = 440,
        };
        table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        table.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var list = BuildSessionList(loadPath);
        var currentSortColumn = SessionLoadSortColumn.Modified;
        var sortAscending = false;

        Button fileNameHeader = CreateSortHeaderButton("File name", HorizontalAlignment.Left);
        Button parentHeader = CreateSortHeaderButton("Parent folder", HorizontalAlignment.Left);
        Button sizeHeader = CreateSortHeaderButton("Size", HorizontalAlignment.Right);
        Button modifiedHeader = CreateSortHeaderButton("Modified", HorizontalAlignment.Right);
        fileNameHeader.Click += (_, _) => SortBy(SessionLoadSortColumn.FileName);
        parentHeader.Click += (_, _) => SortBy(SessionLoadSortColumn.ParentDirectory);
        sizeHeader.Click += (_, _) => SortBy(SessionLoadSortColumn.Size);
        modifiedHeader.Click += (_, _) => SortBy(SessionLoadSortColumn.Modified);

        var header = BuildSessionTableHeader(fileNameHeader, parentHeader, sizeHeader, modifiedHeader);
        Grid.SetRow(header, 0);
        table.Children.Add(header);

        Grid.SetRow(list, 1);
        table.Children.Add(list);

        RefreshSessionList();
        return new Border
        {
            BorderBrush = ResourceBrush("CardStrokeColorDefaultBrush", Microsoft.UI.Colors.DimGray),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            MinHeight = 300,
            MaxHeight = 440,
            Child = table,
        };

        void SortBy(SessionLoadSortColumn column)
        {
            if (currentSortColumn == column)
                sortAscending = !sortAscending;
            else
            {
                currentSortColumn = column;
                sortAscending = DefaultSortAscending(column);
            }

            RefreshSessionList();
        }

        void RefreshSessionList()
        {
            PopulateSessionListItems(
                list,
                SortSessionCandidates(sessions, currentSortColumn, sortAscending),
                loadPath);
            UpdateSortHeaderButtons(
                currentSortColumn,
                sortAscending,
                (SessionLoadSortColumn.FileName, fileNameHeader, "File name"),
                (SessionLoadSortColumn.ParentDirectory, parentHeader, "Parent folder"),
                (SessionLoadSortColumn.Size, sizeHeader, "Size"),
                (SessionLoadSortColumn.Modified, modifiedHeader, "Modified"));
        }
    }

    private static Grid BuildSessionTableHeader(params Button[] headers)
    {
        var header = CreateSessionRowGrid();
        header.Background = ResourceBrush("CardBackgroundFillColorSecondaryBrush", Windows.UI.Color.FromArgb(0x20, 0x80, 0x80, 0x80));
        header.Padding = new Thickness(12, 8, 12, 8);

        for (int i = 0; i < headers.Length; i++)
        {
            Grid.SetColumn(headers[i], i);
            header.Children.Add(headers[i]);
        }

        return header;
    }

    private static Button CreateSortHeaderButton(string label, HorizontalAlignment contentAlignment)
    {
        var button = new Button
        {
            Content = label,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = contentAlignment,
            FontSize = 12,
            MinHeight = 24,
            MinWidth = 0,
        };
        ToolTipService.SetToolTip(button, $"Sort by {label.ToLowerInvariant()}");
        return button;
    }

    private static void UpdateSortHeaderButtons(
        SessionLoadSortColumn activeColumn,
        bool sortAscending,
        params (SessionLoadSortColumn Column, Button Button, string Label)[] headers)
    {
        foreach (var header in headers)
        {
            bool isActive = header.Column == activeColumn;
            header.Button.Content = isActive
                ? string.Concat(header.Label, sortAscending ? " ^" : " v")
                : header.Label;
            header.Button.FontWeight = isActive
                ? Microsoft.UI.Text.FontWeights.SemiBold
                : Microsoft.UI.Text.FontWeights.Normal;
        }
    }

    private static ListView BuildSessionList(Action<string> loadPath)
    {
        var list = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            IsItemClickEnabled = true,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };

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

        return list;
    }

    private static void PopulateSessionListItems(
        ListView list,
        IReadOnlyList<SessionFileCandidate> sessions,
        Action<string> loadPath)
    {
        list.Items.Clear();

        foreach (var session in sessions)
        {
            var item = new ListViewItem
            {
                Tag = session,
                Content = BuildSessionRow(session),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(10, 8, 10, 8),
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

    private static Grid BuildSessionRow(SessionFileCandidate session)
    {
        var row = CreateSessionRowGrid();
        row.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));

        var nameStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Center,
        };
        nameStack.Children.Add(new FontIcon
        {
            Glyph = "\uE8A5",
            FontSize = 16,
            Width = 20,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.72,
        });
        var nameText = new TextBlock
        {
            Text = GetDisplayName(session.Path),
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        ToolTipService.SetToolTip(nameText, session.Path);
        nameStack.Children.Add(nameText);
        Grid.SetColumn(nameStack, 0);
        row.Children.Add(nameStack);

        row.Children.Add(CreateCellText(GetParentDirectory(session.Path), 1, TextAlignment.Left, 0.66));
        row.Children.Add(CreateCellText(FormatSize(session.SizeBytes), 2, TextAlignment.Right, 0.72));
        row.Children.Add(CreateCellText(FormatModified(session.ModifiedUtc), 3, TextAlignment.Right, 0.72));

        return row;
    }

    private static Grid CreateSessionRowGrid()
    {
        var row = new Grid
        {
            ColumnSpacing = 12,
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(145) });

        return row;
    }

    private static TextBlock CreateCellText(string text, int column, TextAlignment alignment, double opacity)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = 12,
            Opacity = opacity,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = alignment,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        ToolTipService.SetToolTip(block, text);
        Grid.SetColumn(block, column);
        return block;
    }

    private static string GetDisplayName(string path)
    {
        string fileName = System.IO.Path.GetFileName(path);
        return string.IsNullOrWhiteSpace(fileName) ? path : fileName;
    }

    private static string GetParentDirectory(string path)
    {
        string? directory = System.IO.Path.GetDirectoryName(path);
        return string.IsNullOrWhiteSpace(directory) ? "No parent folder" : directory;
    }

    private static string FormatSize(long? bytes)
    {
        return bytes is long sizeBytes ? FormatByteSize(sizeBytes) : "Unknown";
    }

    private static string FormatModified(DateTimeOffset? modifiedUtc)
    {
        return modifiedUtc is DateTimeOffset modified
            ? modified.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
            : "Unknown";
    }

    private static bool DefaultSortAscending(SessionLoadSortColumn column)
        => column is SessionLoadSortColumn.FileName or SessionLoadSortColumn.ParentDirectory or SessionLoadSortColumn.Size;

    private static SessionFileCandidate[] SortSessionCandidates(
        IReadOnlyList<SessionFileCandidate> sessions,
        SessionLoadSortColumn sortColumn,
        bool sortAscending)
    {
        return sortColumn switch
        {
            SessionLoadSortColumn.FileName => SortByText(sessions, session => GetDisplayName(session.Path), sortAscending),
            SessionLoadSortColumn.ParentDirectory => SortByText(sessions, session => GetParentDirectory(session.Path), sortAscending),
            SessionLoadSortColumn.Size => SortByNullableNumber(sessions, sortAscending),
            _ => SortByNullableDate(sessions, sortAscending),
        };
    }

    private static SessionFileCandidate[] SortByText(
        IReadOnlyList<SessionFileCandidate> sessions,
        Func<SessionFileCandidate, string> selector,
        bool ascending)
    {
        IOrderedEnumerable<SessionFileCandidate> ordered = ascending
            ? sessions.OrderBy(selector, StringComparer.OrdinalIgnoreCase)
            : sessions.OrderByDescending(selector, StringComparer.OrdinalIgnoreCase);

        return ordered
            .ThenBy(session => GetDisplayName(session.Path), StringComparer.OrdinalIgnoreCase)
            .ThenBy(session => session.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static SessionFileCandidate[] SortByNullableNumber(IReadOnlyList<SessionFileCandidate> sessions, bool ascending)
    {
        IOrderedEnumerable<SessionFileCandidate> ordered = ascending
            ? sessions.OrderBy(session => session.SizeBytes is null).ThenBy(session => session.SizeBytes ?? 0)
            : sessions.OrderBy(session => session.SizeBytes is null).ThenByDescending(session => session.SizeBytes ?? 0);

        return ordered
            .ThenBy(session => GetDisplayName(session.Path), StringComparer.OrdinalIgnoreCase)
            .ThenBy(session => session.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static SessionFileCandidate[] SortByNullableDate(IReadOnlyList<SessionFileCandidate> sessions, bool ascending)
    {
        IOrderedEnumerable<SessionFileCandidate> ordered = ascending
            ? sessions.OrderBy(session => session.ModifiedUtc is null).ThenBy(session => session.ModifiedUtc ?? DateTimeOffset.MinValue)
            : sessions.OrderBy(session => session.ModifiedUtc is null).ThenByDescending(session => session.ModifiedUtc ?? DateTimeOffset.MinValue);

        return ordered
            .ThenBy(session => GetDisplayName(session.Path), StringComparer.OrdinalIgnoreCase)
            .ThenBy(session => session.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
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