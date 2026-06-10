using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Yagu.Services;

namespace Yagu;

/// <summary>
/// Builds and shows the custom report export dialog, returning the chosen options or null if cancelled.
/// </summary>
internal static class ReportExportDialog
{
    public static async Task<ReportExportOptions?> ShowAsync(IntPtr ownerHwnd, int defaultContextLines)
    {
        var options = new ReportExportOptions { ContextLineCount = defaultContextLines };

        // Format radio buttons
        var radioHtml = new RadioButton { Content = "HTML report", IsChecked = true, GroupName = "ExportFormat" };
        var radioJson = new RadioButton { Content = "JSON", GroupName = "ExportFormat" };
        var radioCsv = new RadioButton { Content = "CSV", GroupName = "ExportFormat" };

        // Content options
        var chkFileSizes = new CheckBox { Content = "Include file sizes", IsChecked = false };
        var chkModifiedDates = new CheckBox { Content = "Include file modified dates", IsChecked = false };
        var chkContextLines = new CheckBox { Content = "Include context lines around matches", IsChecked = true };
        var contextCountBox = new NumberBox
        {
            Value = defaultContextLines,
            Minimum = 0,
            Maximum = 50,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Header = "Context lines:",
            Width = 140,
            Margin = new Thickness(24, 0, 0, 0),
        };

        var chkMatchMarkers = new CheckBox
        {
            Content = "Include <match></match> markers (JSON/CSV)",
            IsChecked = true,
        };

        // CSV-specific: embed context option
        var chkCsvEmbedContext = new CheckBox
        {
            Content = "Include context in CSV (requires selecting a line separator below)",
            IsChecked = false,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(24, 0, 0, 0),
        };
        var radioCsvNewlines = new RadioButton
        {
            Content = "Separate lines with embedded newlines (RFC 4180)",
            IsChecked = true,
            GroupName = "CsvLineSep",
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(48, 0, 0, 0),
        };
        var radioCsvPipe = new RadioButton
        {
            Content = "Separate lines with pipe ( | )",
            GroupName = "CsvLineSep",
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(48, 0, 0, 0),
        };
        var csvContextNote = new TextBlock
        {
            Text = "When disabled, context lines are omitted from CSV for maximum compatibility.",
            FontSize = 11,
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(24, 0, 0, 4),
        };

        // Wire visibility of CSV-specific options
        void UpdateCsvVisibility()
        {
            bool isCsv = radioCsv.IsChecked == true;
            bool csvContextVisible = isCsv && chkContextLines.IsChecked == true;
            chkCsvEmbedContext.Visibility = csvContextVisible ? Visibility.Visible : Visibility.Collapsed;
            radioCsvNewlines.Visibility = csvContextVisible ? Visibility.Visible : Visibility.Collapsed;
            radioCsvPipe.Visibility = csvContextVisible ? Visibility.Visible : Visibility.Collapsed;
            csvContextNote.Visibility = csvContextVisible ? Visibility.Visible : Visibility.Collapsed;
            // Match markers label hint
            chkMatchMarkers.Visibility = (radioJson.IsChecked == true || radioCsv.IsChecked == true)
                ? Visibility.Visible : Visibility.Collapsed;
        }

        radioHtml.Checked += (_, _) => UpdateCsvVisibility();
        radioJson.Checked += (_, _) => UpdateCsvVisibility();
        radioCsv.Checked += (_, _) => UpdateCsvVisibility();
        chkContextLines.Checked += (_, _) => { contextCountBox.IsEnabled = true; UpdateCsvVisibility(); };
        chkContextLines.Unchecked += (_, _) => { contextCountBox.IsEnabled = false; UpdateCsvVisibility(); };
        chkCsvEmbedContext.Checked += (_, _) => UpdateCsvVisibility();
        chkCsvEmbedContext.Unchecked += (_, _) => UpdateCsvVisibility();

        // Initial state
        chkMatchMarkers.Visibility = Visibility.Collapsed;

        var formatPanel = new StackPanel { Spacing = 4 };
        formatPanel.Children.Add(new TextBlock { Text = "Export Format", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        formatPanel.Children.Add(radioHtml);
        formatPanel.Children.Add(radioJson);
        formatPanel.Children.Add(radioCsv);

        var optionsPanel = new StackPanel { Spacing = 4, Margin = new Thickness(0, 12, 0, 0) };
        optionsPanel.Children.Add(new TextBlock { Text = "Include in Export", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        optionsPanel.Children.Add(chkFileSizes);
        optionsPanel.Children.Add(chkModifiedDates);
        optionsPanel.Children.Add(chkContextLines);
        optionsPanel.Children.Add(contextCountBox);
        optionsPanel.Children.Add(chkMatchMarkers);
        optionsPanel.Children.Add(chkCsvEmbedContext);
        optionsPanel.Children.Add(radioCsvNewlines);
        optionsPanel.Children.Add(radioCsvPipe);
        optionsPanel.Children.Add(csvContextNote);

        var root = new StackPanel { Spacing = 4 };
        root.Children.Add(formatPanel);
        root.Children.Add(optionsPanel);

        var result = await YaguDialog.ShowAsync(
            ownerHwnd,
            new YaguDialogOptions
            {
                Title = "Export Report",
                Content = root,
                PrimaryButtonText = "Export",
                CloseButtonText = "Cancel",
                DefaultButton = YaguDialogDefaultButton.Primary,
                Width = 520,
                Height = 560,
                MaxContentHeight = 440,
            });
        if (result != YaguDialogResult.Primary)
            return null;

        // Gather selections
        options.Format = radioJson.IsChecked == true ? ReportFormat.Json
            : radioCsv.IsChecked == true ? ReportFormat.Csv
            : ReportFormat.Html;
        options.IncludeFileSizes = chkFileSizes.IsChecked == true;
        options.IncludeModifiedDates = chkModifiedDates.IsChecked == true;
        options.IncludeContextLines = chkContextLines.IsChecked == true;
        options.ContextLineCount = (int)contextCountBox.Value;
        options.IncludeMatchMarkers = chkMatchMarkers.IsChecked == true;
        options.CsvEmbedContext = chkCsvEmbedContext.IsChecked == true;
        options.CsvUsePipeSeparator = radioCsvPipe.IsChecked == true;

        return options;
    }
}
