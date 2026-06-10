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

        var summary = new TextBlock
        {
            Text = sessions.Count == 0
                ? "No .yagu-session files found by Everything."
                : $"{sessions.Count:N0} .yagu-session file{(sessions.Count == 1 ? string.Empty : "s")} found",
            FontSize = 13,
            Opacity = 0.72,
            TextWrapping = TextWrapping.WrapWholeWords,
        };
        Grid.SetRow(summary, 0);
        root.Children.Add(summary);

        FrameworkElement body = sessions.Count == 0
            ? BuildEmptyState()
            : BuildSessionList(sessions, loadPath);
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        return root;
    }

    private static ListView BuildSessionList(IReadOnlyList<SessionFileCandidate> sessions, Action<string> loadPath)
    {
        var list = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            IsItemClickEnabled = true,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            MinHeight = 300,
            MaxHeight = 440,
        };

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
        var row = new Grid
        {
            ColumnSpacing = 12,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new FontIcon
        {
            Glyph = "\uE8A5",
            FontSize = 18,
            Width = 24,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.78,
        };
        Grid.SetColumn(icon, 0);
        row.Children.Add(icon);

        var textStack = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
        };
        textStack.Children.Add(new TextBlock
        {
            Text = GetDisplayName(session.Path),
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        textStack.Children.Add(new TextBlock
        {
            Text = session.Path,
            FontSize = 12,
            Opacity = 0.66,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        Grid.SetColumn(textStack, 1);
        row.Children.Add(textStack);

        var metadata = new TextBlock
        {
            Text = FormatMetadata(session),
            FontSize = 12,
            Opacity = 0.62,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = Microsoft.UI.Xaml.TextAlignment.Right,
            MinWidth = 120,
        };
        Grid.SetColumn(metadata, 2);
        row.Children.Add(metadata);

        return row;
    }

    private static string GetDisplayName(string path)
    {
        string fileName = System.IO.Path.GetFileName(path);
        return string.IsNullOrWhiteSpace(fileName) ? path : fileName;
    }

    private static string FormatMetadata(SessionFileCandidate session)
    {
        var parts = new List<string>(2);
        if (session.ModifiedUtc is DateTimeOffset modified)
            parts.Add(modified.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));
        if (session.SizeBytes is long sizeBytes)
            parts.Add(FormatByteSize(sizeBytes));

        return string.Join(Environment.NewLine, parts);
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