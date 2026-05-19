using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Yagu.Services;

namespace Yagu;

/// <summary>
/// Find bar, replace, and replace-in-all-files logic.
/// </summary>
public sealed partial class MainWindow
{
    private void OnOpenFindReplaceBar(object sender, RoutedEventArgs e)
    {
        OpenFindBar(showReplace: true);
    }

    private void OpenFindBar(bool showReplace)
    {
        FindBar.Visibility = Visibility.Visible;
        bool inEditor = PreviewEditor.Visibility == Visibility.Visible;
        if (showReplace)
        {
            ReplaceRow.Visibility = Visibility.Visible;
            FindReplaceToggle.IsChecked = true;
            ReplaceOneButton.IsEnabled = inEditor;
            ReplaceAllButton.IsEnabled = inEditor;
            ReplaceInFilesButton.IsEnabled = ViewModel.HasResults;
        }

        // Pre-fill with selected text from the editor
        if (PreviewEditor.Visibility == Visibility.Visible && PreviewEditor.SelectedText.Length > 0 && !PreviewEditor.SelectedText.Contains('\n'))
            FindTextBox.Text = PreviewEditor.SelectedText;

        FindTextBox.Focus(FocusState.Programmatic);
        FindTextBox.SelectAll();
    }

    private void OnCloseFindBar(object sender, RoutedEventArgs e)
    {
        CloseFindBar();
    }

    private void CloseFindBar()
    {
        FindBar.Visibility = Visibility.Collapsed;
        ReplaceRow.Visibility = Visibility.Collapsed;
        FindReplaceToggle.IsChecked = false;
        _findIndex = -1;
        FindStatusText.Text = string.Empty;

        // Return focus to the editor or preview
        if (PreviewEditor.Visibility == Visibility.Visible)
            PreviewEditor.Focus(FocusState.Programmatic);
    }

    private void OnFindReplaceToggle(object sender, RoutedEventArgs e)
    {
        bool show = FindReplaceToggle.IsChecked == true;
        ReplaceRow.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        // Single-file replace buttons only make sense in the editor
        bool inEditor = PreviewEditor.Visibility == Visibility.Visible;
        ReplaceOneButton.IsEnabled = inEditor;
        ReplaceAllButton.IsEnabled = inEditor;
        ReplaceInFilesButton.IsEnabled = ViewModel.HasResults;
    }

    private void OnFindTextBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape) { CloseFindBar(); e.Handled = true; return; }
        if (e.Key == VirtualKey.Enter)
        {
            bool shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
                             .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            if (shift) FindPrevious(); else FindNext();
            e.Handled = true;
        }
    }

    private void OnReplaceTextBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape) { CloseFindBar(); e.Handled = true; return; }
        if (e.Key == VirtualKey.Enter) { ReplaceOne(); e.Handled = true; }
    }

    private void OnFindTextBoxTextChanged(object sender, TextChangedEventArgs e)
    {
        _findIndex = -1; // reset so next find starts from current selection
        UpdateFindStatus();
    }

    private void OnFindOptionChanged(object sender, RoutedEventArgs e)
    {
        _findIndex = -1;
        UpdateFindStatus();
    }

    private void OnFindNext(object sender, RoutedEventArgs e) => FindNext();
    private void OnFindPrevious(object sender, RoutedEventArgs e) => FindPrevious();
    private void OnReplaceOne(object sender, RoutedEventArgs e) => ReplaceOne();
    private void OnReplaceAll(object sender, RoutedEventArgs e) => ReplaceAll();

    private StringComparison FindComparison =>
        FindMatchCaseCheckBox.IsChecked == true ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    private string FindTarget => PreviewEditor.Visibility == Visibility.Visible ? GetPreviewEditorText() : GetPreviewBlockText();

    private string GetPreviewBlockText()
    {
        var sb = new StringBuilder();
        if (PreviewSectionsPanel.Visibility == Visibility.Visible)
        {
            foreach (var block in EnumeratePreviewSectionBlocks())
                AppendBlockText(block, sb);
        }
        else
        {
            AppendBlockText(PreviewBlock, sb);
        }
        return sb.ToString();
    }

    private static void AppendBlockText(RichTextBlock richBlock, StringBuilder sb)
    {
        foreach (var block in richBlock.Blocks)
        {
            if (block is Microsoft.UI.Xaml.Documents.Paragraph p)
            {
                foreach (var inline in p.Inlines)
                {
                    if (inline is Microsoft.UI.Xaml.Documents.Run run) sb.Append(run.Text);
                    else if (inline is Microsoft.UI.Xaml.Documents.Span span)
                    {
                        foreach (var inner in span.Inlines)
                        {
                            if (inner is Microsoft.UI.Xaml.Documents.Run innerRun) sb.Append(innerRun.Text);
                        }
                    }
                }
                sb.AppendLine();
            }
        }
    }

    private void FindNext()
    {
        var needle = FindTextBox.Text;
        if (string.IsNullOrEmpty(needle)) return;
        var haystack = FindTarget;
        if (haystack.Length == 0) { FindStatusText.Text = "No content"; return; }

        int startPos = _findIndex >= 0 ? _findIndex + needle.Length : 0;
        if (startPos >= haystack.Length) startPos = 0;

        int idx = haystack.IndexOf(needle, startPos, FindComparison);
        if (idx < 0 && startPos > 0)
            idx = haystack.IndexOf(needle, 0, FindComparison); // wrap around

        if (idx < 0) { FindStatusText.Text = "No matches"; _findIndex = -1; return; }

        _findIndex = idx;
        SelectFindMatch(idx, needle.Length);
        UpdateFindStatus();
    }

    private void FindPrevious()
    {
        var needle = FindTextBox.Text;
        if (string.IsNullOrEmpty(needle)) return;
        var haystack = FindTarget;
        if (haystack.Length == 0) { FindStatusText.Text = "No content"; return; }

        int startPos = _findIndex > 0 ? _findIndex - 1 : haystack.Length - 1;

        // Search backwards by scanning substring before startPos
        int idx = haystack.LastIndexOf(needle, startPos, FindComparison);
        if (idx < 0 && startPos < haystack.Length - 1)
            idx = haystack.LastIndexOf(needle, haystack.Length - 1, FindComparison); // wrap around

        if (idx < 0) { FindStatusText.Text = "No matches"; _findIndex = -1; return; }

        _findIndex = idx;
        SelectFindMatch(idx, needle.Length);
        UpdateFindStatus();
    }

    private void SelectFindMatch(int index, int length)
    {
        if (PreviewEditor.Visibility == Visibility.Visible)
        {
            PreviewEditor.Focus(FocusState.Programmatic);
            SelectPreviewEditorText(index, length);
        }
    }

    private void UpdateFindStatus()
    {
        var needle = FindTextBox.Text;
        if (string.IsNullOrEmpty(needle)) { FindStatusText.Text = string.Empty; return; }
        var haystack = FindTarget;
        int count = 0;
        int pos = 0;
        while ((pos = haystack.IndexOf(needle, pos, FindComparison)) >= 0)
        {
            count++;
            pos += needle.Length;
        }
        FindStatusText.Text = count == 0 ? "No matches" : $"{count} match{(count == 1 ? "" : "es")}";
    }

    private void ReplaceOne()
    {
        if (PreviewEditor.Visibility != Visibility.Visible) return;
        var needle = FindTextBox.Text;
        if (string.IsNullOrEmpty(needle)) return;

        var text = GetPreviewEditorText();
        int replaceAt = _findIndex;
        if (replaceAt < 0
            || replaceAt + needle.Length > text.Length
            || !text.AsSpan(replaceAt, needle.Length).Equals(needle.AsSpan(), FindComparison))
        {
            replaceAt = text.IndexOf(needle, FindComparison);
            if (replaceAt < 0)
            {
                FindStatusText.Text = "No matches";
                return;
            }
        }

        var replacement = ReplaceTextBox.Text;
        var updated = text.Remove(replaceAt, needle.Length).Insert(replaceAt, replacement);
        _suppressPreviewEditorTextChanged = true;
        LoadPreviewEditorText(updated);
        _suppressPreviewEditorTextChanged = false;
        _previewEditorDirty = true;
        _findIndex = replaceAt;
        SelectFindMatch(replaceAt, replacement.Length);
        UpdatePreviewEditorButtons();
        FindNext();
    }

    private void ReplaceAll()
    {
        if (PreviewEditor.Visibility != Visibility.Visible) return;
        var needle = FindTextBox.Text;
        if (string.IsNullOrEmpty(needle)) return;

        var replacement = ReplaceTextBox.Text;
        var text = GetPreviewEditorText();
        var sb = new StringBuilder(text.Length);
        int count = 0;
        int pos = 0;
        while (true)
        {
            int idx = text.IndexOf(needle, pos, FindComparison);
            if (idx < 0) { sb.Append(text, pos, text.Length - pos); break; }
            sb.Append(text, pos, idx - pos);
            sb.Append(replacement);
            count++;
            pos = idx + needle.Length;
        }

        if (count > 0)
        {
            _suppressPreviewEditorTextChanged = true;
            LoadPreviewEditorText(sb.ToString());
            _suppressPreviewEditorTextChanged = false;
            _previewEditorDirty = true;
            UpdatePreviewEditorButtons();
        }

        _findIndex = -1;
        FindStatusText.Text = count > 0 ? $"Replaced {count}" : "No matches";
    }

    private async void OnReplaceInAllFiles(object sender, RoutedEventArgs e)
    {
        var needle = FindTextBox.Text;
        if (string.IsNullOrEmpty(needle)) { FindStatusText.Text = "Enter text to find"; return; }

        var replacement = ReplaceTextBox.Text;
        var comparison = FindComparison;
        var groups = ViewModel.ResultGroups.ToList();

        if (groups.Count == 0) { FindStatusText.Text = "No result files"; return; }

        ReplaceInFilesButton.IsEnabled = false;
        FindStatusText.Text = "Scanning files…";

        // Collect changes off the UI thread.
        var changes = await Task.Run(() =>
        {
            var list = new List<(string Path, string Original, string Replaced, Encoding Encoding, int Count)>();
            foreach (var group in groups)
            {
                if (group.IsArchiveEntry) continue;
                var path = group.FilePath;
                if (!File.Exists(path)) continue;
                try
                {
                    Encoding encoding;
                    string original;
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                               FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan))
                    {
                        encoding = Helpers.EncodingDetector.DetectEncoding(stream);
                        if (encoding is System.Text.UTF8Encoding)
                            encoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
                        using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true);
                        original = reader.ReadToEnd();
                        encoding = reader.CurrentEncoding;
                    }

                    var sb = new System.Text.StringBuilder(original.Length);
                    int replaceCount = 0;
                    int pos = 0;
                    while (true)
                    {
                        int idx = original.IndexOf(needle, pos, comparison);
                        if (idx < 0) { sb.Append(original, pos, original.Length - pos); break; }
                        sb.Append(original, pos, idx - pos);
                        sb.Append(replacement);
                        replaceCount++;
                        pos = idx + needle.Length;
                    }
                    if (replaceCount > 0)
                    {
                        var replaced = sb.ToString();
                        if (!TextHasUnencodableCharacters(replaced, encoding))
                            list.Add((path, original, replaced, encoding, replaceCount));
                    }
                }
                catch { /* skip unreadable files */ }
            }
            return list;
        });

        if (changes.Count == 0)
        {
            FindStatusText.Text = "No matches in any file";
            ReplaceInFilesButton.IsEnabled = true;
            return;
        }

        int totalReplacements = changes.Sum(c => c.Count);

        // Confirm before writing.
        var dialog = new ContentDialog
        {
            XamlRoot = ((FrameworkElement)Content).XamlRoot,
            Title = "Replace in All Files",
            Content = $"Replace {totalReplacements:N0} occurrence{(totalReplacements == 1 ? "" : "s")} across {changes.Count:N0} file{(changes.Count == 1 ? "" : "s")}?",
            PrimaryButtonText = "Replace",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        var choice = await dialog.ShowAsync();
        if (choice != ContentDialogResult.Primary)
        {
            FindStatusText.Text = "Cancelled";
            ReplaceInFilesButton.IsEnabled = true;
            return;
        }

        // Write changes to disk.
        int written = 0;
        int errors = 0;
        bool backupEnabled = ViewModel.BackupBeforeSave;
        await Task.Run(() =>
        {
            foreach (var (path, _, replaced, encoding, _) in changes)
            {
                try
                {
                    if (backupEnabled)
                    {
                        var bakPath = path + ".yagubak";
                        if (!File.Exists(bakPath))
                            File.Copy(path, bakPath, overwrite: false);
                        else
                        {
                            int suffix = 2;
                            while (File.Exists($"{path}.yagubak-{suffix}")) suffix++;
                            File.Copy(path, $"{path}.yagubak-{suffix}", overwrite: false);
                        }
                    }
                    File.WriteAllText(path, replaced, encoding);
                    Interlocked.Increment(ref written);
                }
                catch
                {
                    Interlocked.Increment(ref errors);
                }
            }
        });

        // Re-validate results for each changed file.
        foreach (var (path, _, replaced, _, _) in changes)
        {
            ViewModel.RevalidateFileResults(path, replaced);
        }

        // Refresh preview if the currently shown file was affected.
        if (_previewResult is { } current && changes.Any(c =>
                string.Equals(c.Path, current.FilePath, StringComparison.OrdinalIgnoreCase)))
        {
            await ShowSingleFilePreviewAsync(current, fullFile: false);
        }

        var statusParts = new List<string> { $"Replaced in {written:N0} file{(written == 1 ? "" : "s")}" };
        if (errors > 0) statusParts.Add($"{errors} error{(errors == 1 ? "" : "s")}");
        FindStatusText.Text = string.Join(", ", statusParts);
        ViewModel.StatusText = FindStatusText.Text;
        ReplaceInFilesButton.IsEnabled = true;
    }
}
