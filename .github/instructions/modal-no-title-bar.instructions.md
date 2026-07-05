---
description: "Modals must be title-bar-less by default. Use when: creating a modal, dialog, YaguDialog, owned Window, popup window, ShowAsync, ShowModalAsync, SetBorderAndTitleBar, ExtendsContentIntoTitleBar, title bar, caption."
applyTo: "**"
---

# Modals Have No Title Bar by Default

Any modal, dialog, or owned pop-up `Window` we create MUST NOT show an OS title bar
(the caption strip with the title text and min/max/close buttons) **unless the user
explicitly asks for one**.

## Rule

When you create a new modal/dialog (a `YaguDialog`, or a custom owned `Window` such as
`SemanticModelDownloadDialog` / `AdminProtectedPathsDialog` / `ResultStoreTempLocationWindow`):

- For a `YaguDialog`, set `ShowTitleBar = false` in its `YaguDialogOptions`.
- Make the window title-bar-less **reliably**. `OverlappedPresenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false)`
  alone is **not** sufficient — it can silently fail to apply (a title bar was observed on a
  clean Release install). You MUST also set `ExtendsContentIntoTitleBar = true` directly on the
  `Window` (in the constructor, right after `Content = ...; Closed += ...;`), matching the proven
  pattern used by `MainWindow`, `SettingsWindow`, and `ResultStoreTempLocationWindow`.
- Do **not** call `SetTitleBar(...)` for these modals. With `ExtendsContentIntoTitleBar = true`
  and no `SetTitleBar`, there is no drag region, so all content (including a top-right close
  button) stays interactive.

`ShowTitle` (the in-content heading text/glyph) is a separate concept from the OS title bar and
is unaffected by this rule — leave it as the modal's design requires.

## Only add a title bar when explicitly requested

Add a title bar (`ShowTitleBar = true` / omit `ExtendsContentIntoTitleBar`) **only** when the user
explicitly asks for a modal to have a title bar.

## Regression test

Title-bar-less behavior is source-pinned in
`tests/Yagu.Tests/EverythingSearchDialogRegressionTests.cs`. When adding a new modal type, extend the
relevant pin so the `ExtendsContentIntoTitleBar = true` recipe can't silently regress.
