---
description: "Yagu WinUI 3 / XAML conventions — checkbox circles + indeterminate-glyph theme fix, themed dialog surfaces, wrapping-text layout, Native-AOT COM/folder-picker rules, and non-blocking Settings-window build. Use when: editing App.xaml, a WinUI page/window/control, XAML styling, CheckBox, indeterminate dash, theme brush, dark/light modal background, AppThemeService, TextBlock wrapping, TextBox AcceptsReturn, FolderPicker, Win32FileDialog, COMException, Settings window build, NumberBox dirty tracking, RegisterHotKey."
applyTo: "src/Yagu/UI/**, src/Yagu/App.xaml, src/Yagu/App.xaml.cs, src/Yagu/Services/AppThemeService.cs"
---

# Yagu — WinUI 3 / XAML Conventions

Yagu is unpackaged **WinUI 3 on .NET 10 Native AOT**. These are the non-obvious, hard-won UI rules.

## Theming & styling

- **Checkboxes are circles app-wide** — `App.xaml` has an implicit `CheckBox` style with a large
  `CornerRadius`. A stray **indeterminate "dash"** on a two-state CheckBox (`IsThreeState="False"`) is
  a **theme-brush / visual-state artifact**, not a property-value bug: WinUI can momentarily drop the
  control into the indeterminate visual state on a template re-apply, and the template paints it from
  the `CheckBoxCheckGlyphForegroundIndeterminate*` brushes. Fix it at the **theme brushes** (make the
  indeterminate brushes identical to unchecked + a transparent glyph), not with C# `IsChecked`/
  `GoToState` corrections (they lose the race). Groups that legitimately use the indeterminate state
  (per-file partial selection) are the exception.
- **Modal / dialog background must follow the Yagu theme, not the system theme.** Use
  `AppThemeService.ApplyThemedDialogSurface(grid, requestedTheme)`. Never set
  `Background = ResourceBrush("ApplicationPageBackgroundThemeBrush", …)` (it resolves the APP/system
  theme, not the element's `RequestedTheme`) and never hardcode `Colors.White` /
  `FromArgb(0xFF,0x20,0x20,0x20)` (never adapts to Light). When auditing modal theming, grep for BOTH
  the `ResourceBrush(...)` app-resource lookup AND hardcoded colors.
- A `{ThemeResource X}` key that does not exist throws `XamlParseException` **at window-open time, not
  at build time** (e.g. there is no `DefaultNumberBoxStyle`). Only reference theme resources you have
  confirmed exist (or that are already used elsewhere in the app), or set the value directly on the
  element.

## Layout

- **Never put a wrapping `TextBlock` in a horizontal `StackPanel`** — a horizontal StackPanel measures
  children with infinite width, which disables `TextWrapping`, so long text overflows/clips. Use a
  two-column `Grid` (`Auto` + `*`) so the text column is width-constrained and wraps.
- In C# `TextBox` object initializers, set `AcceptsReturn = true` (and `TextWrapping`) **before** `Text`.
  A TextBox is single-line by default and assigning multi-line `Text` while still single-line **silently
  truncates** everything after the first line break.
- Do not set attached properties in an object initializer (`ToolTipService.ToolTip = …`); create the
  element, then call `ToolTipService.SetToolTip(element, …)`.

## Native AOT + WinUI pitfalls

- **Folder picking:** prefer `Win32FileDialog.SelectFolder` (Common Item Dialog with
  `FOS_PICKFOLDERS`) over `FolderPicker.PickSingleFolderAsync()` (throws `COMException 0x80004005` in
  Yagu's settings/search paths). Because Yagu is Native AOT, `Win32FileDialog` MUST use raw
  `CoCreateInstance` / vtable calls — **not** `[ComImport]`, `Activator.CreateInstance`, or
  `Marshal.ReleaseComObject`, or the runtime throws "Built-in COM has been disabled via a feature switch."

## Settings window build must not block the UI thread

- `BuildSettingsContent` runs on the UI thread — do **no** drive-readiness / free-space / write-probe
  I/O there (it takes seconds with network/removable drives). Populate such combos **async**
  (`Task.Run` → `DispatcherQueue.TryEnqueue`) with dirty-tracking suppressed while populating.
- `RegisterHotKey` / `UnregisterHotKey` **must run on the UI thread** that owns the target HWND — a
  background `Task.Run` fails every probe ("cannot associate a hot key with a window created by another
  thread"). Use a low-priority `DispatcherQueue.TryEnqueue`, not `Task.Run`.
- NumberBox dirty-tracking must snapshot `numberBox.Value`, **never** `numberBox.Text` (Text formats
  asynchronously after the template loads, so a Text snapshot falsely reports unsaved changes).
- Settings search must not reparent live controls (WinUI throws "Element is already the child of
  another element"); render fresh lightweight result rows and dedup shared `UIElement`s through a
  `HashSet` before adding.
