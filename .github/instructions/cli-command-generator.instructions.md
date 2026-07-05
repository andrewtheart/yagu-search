---
description: "Keep the in-app CLI command generator in sync with Advanced Options. Use when: adding/changing/removing an Advanced Option, search/scope/performance option, BuildGeneratedCliCommand, CLI flag, --pattern, --semantic-pattern, hidden files flag."
applyTo: "src/Yagu/UI/Windows/MainWindow/MainWindow.xaml, src/Yagu/UI/Windows/MainWindow/MainWindow.CliCommand.cs, src/Yagu/ViewModels/MainViewModel.cs, src/Yagu/Services/SettingsService.cs"
---

# Keep the CLI Command Generator in Sync with Advanced Options

The **Generate CLI command** button builds a reproducible `Yagu.exe --cli` command from the current
UI state in `BuildGeneratedCliCommand` (in `src/Yagu/UI/Windows/MainWindow/MainWindow.CliCommand.cs`).

## Rule

Whenever you **add, modify, or remove an Advanced Option** — i.e. any search, scope, filtering, or
performance setting exposed in the Advanced Options drawer (`MainWindow.xaml`), as a `MainViewModel`
property, or as an `AppSettings` field that influences a search — you MUST update
`BuildGeneratedCliCommand` so the generated command reflects it.

For each affected option:

- Emit the matching CLI flag for the option, gated through `ShouldIncludeSavedSettingOption(...)` so
  it is omitted when it already matches the saved settings file (unless "include saved options" is on).
- Use the existing helpers (`AddValue`, `AddDateValue`, `FormatFileSizeArgument`, etc.) and follow the
  established ordering/grouping.
- Make sure the flag is actually parsed in `CliRunner.cs`; if it is a new flag, add it to the arg
  parser, the `--help` output, and `HELP.md` (per the repo's CLI documentation rules).
- Keep `tests/Yagu.Tests/MainWindowCliCommandRegressionTests.cs` passing and extend it for the new flag.

## Mode-specific reminders

- **Semantic mode** (`ViewModel.IsSemanticQueryMode`): the query is sent to the on-device model via
  `--semantic-pattern "<text>"`, NOT `--pattern`. Traditional mode uses `--pattern`.
- **Hidden files** (`ViewModel.SearchHiddenFiles`): emit `--hidden` / `--no-hidden`.
- Note: `SearchOnlineOnlyFiles` currently has no CLI flag (it is settings-only), so it is intentionally
  not emitted — add a flag in `CliRunner.cs` first if it ever needs to be reproducible.
