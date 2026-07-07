---
name: native-crash-diagnostics
description: "Diagnose Yagu NATIVE fail-fast crashes that bypass managed exception logging — 0xc000027b / 0xc0000005 / StackHash in Microsoft.UI.Xaml.dll (WinUI) or an access violation / 0xc0000409 in the Rust yagu_core.dll. Use when: Yagu crashed, why did Yagu crash, Yagu crash diagnostics, root cause of a Yagu crash, native crash, access violation, 0xc000027b, 0xc0000005, 0xc0000409, StackHash, Microsoft.UI.Xaml crash, preview/editor crash, match-nav crash, no managed exception was logged, WER LocalDumps, crash dump."
---

# Yagu Native Crash Diagnostics

Yagu's worst crashes are **native fail-fasts**, not managed exceptions: a WinUI fail-fast in
`Microsoft.UI.Xaml.dll` (usually `0xc000027b`, sometimes reported as `StackHash`) or a fault in the
Rust `yagu_core.dll` (access violation `0xc0000005`, or a Rust `panic = "abort"` that exits as
`0xc0000409`). These do **NOT** flow through .NET's `UnhandledException`, so `App.xaml.cs` never logs
them, `%APPDATA%\Yagu\yagu-crash.log` stays empty, and a managed `try/catch (Exception)` around XAML
geometry calls (e.g. `TransformToVisual` / `GetCharacterRect` in `MainWindow.MatchNav.cs`) does **not**
catch them. Diagnose from Windows Error Reporting plus the verbose app log — never expect a managed
stack.

## Step 1 — Ensure crash dumps are captured

Debug builds of `Yagu.csproj` run this automatically, but to configure it by hand run the committed
helper (HKCU, no elevation needed):

```powershell
pwsh scripts/ensure-wer-localdumps.ps1 -DumpFolder C:\src\Yagu\TestResults\CrashDumps
```

It writes `HKCU:\Software\Microsoft\Windows\Windows Error Reporting\LocalDumps\Yagu.exe` with
`DumpType=2` (full), `DumpCount=10`, `DumpFolder=C:\src\Yagu\TestResults\CrashDumps`, and pre-creates
the folder (WER silently skips capture if it is missing). Caveat: WER sometimes still routes a report
through its own `WER\Temp` and produces **no** dump — do not assume a dump exists.

## Step 2 — Build symbol-rich binaries

For a `yagu_core.dll` crash, or to get any usable native stack, build Debug with the Rust **profiling**
profile so `yagu_core.dll` + `yagu_core.pdb` land beside `Yagu.exe`:

```powershell
dotnet build src/Yagu/Yagu.csproj -c Debug -p:RustProfile=profiling -p:SkipYaguVersionIncrement=true
```

Then confirm both files exist under `src/Yagu/bin/Debug/net10.0-windows10.0.19041.0/`.

## Step 3 — Read the evidence (WER + app log)

Query the Application log for the faulting module and exception code:

```powershell
Get-WinEvent -FilterHashtable @{LogName='Application'; StartTime=(Get-Date).AddMinutes(-30)} |
  Where-Object { $_.ProviderName -in @('Application Error','Windows Error Reporting','.NET Runtime') -or
                 $_.Message -match 'Yagu|Microsoft\.UI\.Xaml|0xc000027b|0xc0000005|0xc0000409' } |
  Select-Object -First 20 TimeCreated, ProviderName, Id, LevelDisplayName, Message | Format-List
```

Then read `%APPDATA%\Yagu\yagu.log` — the **last line before the process died** is the strongest clue to
which operation faulted. Enable verbose logging first via Settings (Log level = Verbose) or the
installer `/VERBOSELOG` flag; a native fail-fast leaves the log truncated mid-operation (no `Warning`
line, because it was not a managed error).

## Step 4 — Match against known Yagu native-crash triggers

- **Preview / editor `0xc000027b`** — a very large **single-line** file in the preview, then repeated
  word-wrap toggles + Select-All across the whole line + Find/Replace + scroll. The virtualized
  vendored `TextControlBox` selection over an unrendered slice fail-fasts. Reproduce with a big
  single-line file; see `.github/instructions/preview-editor.instructions.md`.
- **Match-nav overlay** — stale `RichTextBlock` geometry after result eviction/expansion. Forcing a
  final `GetCharacterRect` / `ScrollPreviewToLine` after the retry budget is exhausted fail-fasts.
  The fix pattern: do not re-touch geometry after retries exhaust — revalidate block/paragraph/run
  attachment and abandon stale overlay work.
- **Rust `panic = "abort"` (`0xc0000409`)** — a native panic in `yagu_core.dll`. Common causes: an
  mmap-threshold vs test-fixture mismatch, or a null-context callback. See
  `.github/instructions/performance.instructions.md`.

## Step 5 — Prevent recurrences

Lightweight warning modals implemented as custom owned `Window`s have triggered these fail-fasts —
prefer the in-window `YaguDialog` for simple warnings/prompts (see
`.github/instructions/modal-no-title-bar.instructions.md`), and never re-enter stale XAML geometry
after a layout change.
