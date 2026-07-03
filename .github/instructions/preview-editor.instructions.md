---
description: "Yagu context-preview pane and built-in editor (WinUI RichTextBlock + vendored TextControlBox). Use when: editing MainWindow.Preview*, MainWindow.MatchNav, MainWindow.FindReplace, preview highlighting, match navigation, RichTextBlock selection, TextControlBox, editor, 0xc0000005 / 0xc000027b preview crash, long-line preview."
applyTo: "Yagu/UI/Windows/MainWindow/MainWindow.Preview*.cs, Yagu/UI/Windows/MainWindow/MainWindow.MatchNav.cs, Yagu/UI/Windows/MainWindow/MainWindow.FindReplace.cs, vendor/TextControlBox-WinUI/**"
---

# Yagu — Preview Pane & Built-in Editor

The context-preview pane (`MainWindow.Preview*.cs`, `MainWindow.MatchNav.cs`) and the built-in editor
(vendored `TextControlBox-WinUI`, wired in `MainWindow.PreviewEditor.cs` / `MainWindow.FindReplace.cs`)
are WinUI-coupled → **source-pin tests only** (`PreviewCoreRegressionTests`). Pin windows are brittle
char-offset slices; when a pinned method grows and a pin falls outside the window, **widen the window**
rather than fighting it.

## Native-crash guards — do not regress these

- **RichTextBlock native selection causes native access violations.** WinUI's native double-tap
  word-select (`IsTextSelectionEnabled=true`) dereferences a stale text-view mid-reflow → `0xc0000005`
  in `Microsoft.UI.Xaml.dll`, **untrappable by managed try/catch**. The preview keeps
  `IsTextSelectionEnabled=false` **always** and drives selection through a custom overlay
  (`ConfigurePreviewSelectionMode` / `DrawPreviewCustomSelectionOverlay`, wrap-aware). **Never
  re-enable native selection.** Because taking over `PointerPressed` (`e.Handled=true` /
  `CapturePointer`) suppresses `Tapped`/`DoubleTapped`, double-click-to-editor is detected manually in
  the pointer handler — preserve that.
- **Win2D `CanvasControl` draw callbacks that throw fail-fast the process** (`STATUS_STOWED_EXCEPTION`
  `0xc000027b`). All four vendored draw handlers are `try/catch` → `TextControlBoxDiagnostics`; keep
  them guarded, and clamp indices before `GetCharacterRegions`/`GetCharacterRect`.
- **Never render an unbounded long line.** Segment at ~4096 chars (a single very long `Run` throws
  `E_FAIL` in NoWrap), cap "Show all" at ~20K and segment the bounded window, and prompt before
  wrapping a `>200K`-char line in the editor. Force-wrapping a megabyte line hangs/crashes WinUI.

## Highlighting & line numbers

- Preview match highlight must use the **search-time** pattern/flags (`BuildSearchHighlightRegex` /
  `LastSearch*`), **not** live `ViewModel.Query` — after a semantic search the box shows the NL text
  and the case/regex/exact flags have reverted to the user's defaults.
- The **file/results list** line-number de-dup (`SearchResult.SetContextTrim` + `FileGroup`) is a
  SEPARATE code path from the **preview** range-merge (`BuildHighlightSectionAsync`). If "repeated line
  numbers" recurs, confirm **which panel** first.

## Diagnosis & validation

- A RichTextBlock native AV with only the message pump on the managed stack = a native
  input-dispatched callback (selection/hit-test), not a managed handler. Resolve it from the **full**
  native stack (`cdb … -c ".ecxr; kP 60"`), not just the fault `ln`, plus the `yagu.log` tail (a
  runaway loop floods it).
- GDI `CopyFromScreen` / `PrintWindow` yield a BLACK bitmap for WinUI 3 (DirectComposition) content;
  validate preview/editor behavior headlessly with UIA + verbose `[Preview*]` diagnostics, not
  screenshots.

## Editor

The active editor is the vendored `TextControlBox-WinUI` under `vendor/`. An AvaloniaEdit migration is
**planned but not done** (`PLANS/AVALONIAEDIT_MIGRATION_PLAN.md`) — TextControlBox is still live.
