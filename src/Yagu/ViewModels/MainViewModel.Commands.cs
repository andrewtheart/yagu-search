using CommunityToolkit.Mvvm.Input;
using Yagu.Models;
using Yagu.Services;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Yagu.Services.Logging;

namespace Yagu.ViewModels;

/// <summary>
/// Result commands invoked from the results list and its context menu: open in editor, open
/// containing folder, open a terminal here, and the clipboard copies.
/// </summary>
public sealed partial class MainViewModel
{
    [RelayCommand]
    public void OpenInEditor(SearchResult? result)
    {
        if (result is null) return;
        // Test seam: a UI-automation harness (e.g. scripts\test-match-nav.ps1) can set
        // YAGU_EDITOR_COMMAND so that double-tapping a result while driving the real app
        // never launches the user's configured editor. Launching `code` under an elevated
        // VS Code pops a modal "Another instance of Code is already running as administrator"
        // dialog that steals focus and hangs the automation. When the variable is unset (the
        // normal case) the user's configured EditorCommand is used unchanged.
        var editorOverride = Environment.GetEnvironmentVariable("YAGU_EDITOR_COMMAND");
        _editor.Command = string.IsNullOrWhiteSpace(editorOverride) ? EditorCommand : editorOverride;
        _editor.Open(result.FilePath, result.LineNumber);
    }

    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "RelayCommand source generator expects instance command methods.")]
    [RelayCommand]
    public void OpenContainingFolder(SearchResult? result)
    {
        if (result is null) return;
        EditorLauncher.OpenContainingFolder(result.FilePath);
    }

    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "RelayCommand source generator expects instance command methods.")]
    [RelayCommand]
    public void OpenTerminalHere(SearchResult? result)
    {
        if (result is null) return;
        EditorLauncher.OpenTerminalAt(result.FilePath);
    }

    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "RelayCommand source generator expects instance command methods.")]
    [RelayCommand]
    public void CopyFilePath(SearchResult? result)
    {
        if (result is null) return;
        SetClipboard(result.FilePath);
    }

    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "RelayCommand source generator expects instance command methods.")]
    [RelayCommand]
    public void CopyMatchLine(SearchResult? result)
    {
        if (result is null) return;
        SetClipboard(result.MatchLine);
    }

    private static void SetClipboard(string text)
    {
        try
        {
            var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage();
            pkg.SetText(text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);
        }
        catch (Exception ex) { YaguLog.For("Clipboard").LogDebug(ex, "Clipboard unavailable"); }
    }
}
