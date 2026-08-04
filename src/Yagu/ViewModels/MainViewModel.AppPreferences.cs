using CommunityToolkit.Mvvm.ComponentModel;
using Yagu.Services;

namespace Yagu.ViewModels;

/// <summary>
/// Application preferences that are not search inputs: all-drives scope options, editor/save
/// behavior, window and launcher placement, tray behavior, terminal working directory, preview
/// checkbox behavior, and the global hotkey.
/// </summary>
public sealed partial class MainViewModel
{
    [ObservableProperty] public partial bool LimitParallelismOnHdd { get; set; } = true;
    [ObservableProperty] public partial bool SuppressHddParallelismWarnings { get; set; }
    [ObservableProperty] public partial bool SearchAllDrivesIncludesNetwork { get; set; }
    [ObservableProperty] public partial bool SearchAllDrivesIncludesRemovable { get; set; }
    [ObservableProperty] public partial bool SearchAllDrivesIncludesCloud { get; set; }
    [ObservableProperty] public partial bool SearchAllDrivesForceFullScan { get; set; }
    [ObservableProperty] public partial bool BackupBeforeSave { get; set; } = true;
    [ObservableProperty] public partial bool ShowEditorSavedOverlay { get; set; } = true;
    [ObservableProperty] public partial bool EditorSyntaxHighlightingEnabled { get; set; } = true;
    [ObservableProperty] public partial int WindowFocusBehavior { get; set; } = 1; // 0 = MinimizeToTray, 1 = StayOpen (default), 2 = AlwaysOnTop
    [ObservableProperty] public partial bool StartInLauncherMode { get; set; } = true;
    [ObservableProperty] public partial bool CloseToTray { get; set; } = true;
    [ObservableProperty] public partial bool HasShownCloseToTrayNotification { get; set; }
    [ObservableProperty] public partial bool MaximizeOnStartup { get; set; }
    // 0 = Centered (default), 1 = Top Left, 2 = Top Middle, 3 = Top Right, 4 = Middle Left,
    // 5 = Middle Right, 6 = Bottom Left, 7 = Bottom Middle, 8 = Bottom Right.
    [ObservableProperty] public partial int LaunchWindowPosition { get; set; }
    // Compact launcher position; same anchors as LaunchWindowPosition but defaults to 2 = Top Middle.
    [ObservableProperty] public partial int LauncherWindowPosition { get; set; } = 2;
    [ObservableProperty] public partial int AdvancedOptionsCollapsedWidthModeIndex { get; set; }
    [ObservableProperty] public partial string TerminalDefaultWorkingDirectory { get; set; } = string.Empty;
    // 0 = Command Prompt (cmd.exe, default), 1 = PowerShell. Mirrors the terminal-pane shell dropdown.
    [ObservableProperty] public partial int TerminalShellKindIndex { get; set; }
    [ObservableProperty] public partial bool FileHeaderCheckAddsToPreview { get; set; } = true;
    [ObservableProperty] public partial bool MatchLineCheckAddsToPreview { get; set; } = true;

    private string _globalHotkeyKey = HotkeyService.DefaultStartKey.ToString();
    public string GlobalHotkeyKey
    {
        get => _globalHotkeyKey;
        set
        {
            var normalized = HotkeyService.TryNormalizeLetter(value, out var key)
                ? key.ToString()
                : HotkeyService.DefaultStartKey.ToString();
            SetProperty(ref _globalHotkeyKey, normalized);
        }
    }
}
