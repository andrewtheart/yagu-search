# Yagu

Yagu is a fast Windows directory search tool for finding text or regex matches across large folder trees. It is a native WinUI 3 desktop app built on .NET 10, with an optional Rust search engine DLL for the hot content-scanning path and optional voidtools Everything integration for very fast file discovery.

The name means "Yet Another Grep Utility". The goal is the speed of command-line search with a GUI built for repeated code and log investigation: streaming results, context preview, filtering, sorting, exporting, and quick opening in your editor.

For a user-focused walkthrough of the app, see [HELP.md](HELP.md).

## Current Project Status

Use [Yagu.sln](Yagu.sln) for current development.

The [PLANS](PLANS/) directory contains design notes, performance investigations, and future optimization plans. Treat those files as engineering history and roadmap material, not as the canonical build instructions. This README is the entry point for new contributors.

## Important Features

- Fast recursive text search across a directory and all subdirectories.
- Literal and regex search, with optional case-sensitive matching.
- Search modes for content plus file names, content only, file names only, or file-name-gated content search.
- Streaming results: matches appear while the scan is still running.
- voidtools Everything support for file discovery, with automatic fallback to built-in .NET enumeration.
- Optional Rust native scanner for fast per-file matching, with managed C# fallback when the DLL is unavailable.
- Include and exclude filters with glob/path or regex modes, skip-extension lists, optional binary-file inclusion, and max-file-size limits.
- Configurable result cap, per-file match cap, maximum search depth, content-search parallelism, and memory limits.
- Memory-pressure mode that pages result payloads to disk and keeps searching instead of exhausting RAM.
- Grouped result list with optional no-sort mode plus sorting by match count, modified date, file size, or file name.
- Result filtering by file name/path and by match text without rerunning the search.
- Context preview with match highlighting, line numbers, optional word wrap, and lightweight syntax coloring for common source files.
- Multi-select preview modes for reviewing selected matches together.
- Highlighted full-file previews for selected result groups or checked match lines.
- Open files in the default Windows app or in a configurable external editor command.
- Copy or export selected match lines, selected file paths, or selected files with content.
- Explorer context menu registration for "Search with Yagu".
- Startup directory argument via `--dir` or `--dir=...`.
- Recent directory and search-query history.
- Drag-and-drop folders onto the window to set the search directory.
- Optional global `Ctrl+Shift+letter` hotkey to bring Yagu forward and focus the search box.
- Admin elevation banner with a "Restart as Admin" action when some files may be inaccessible.
- Persisted settings, rotating logs, and crash logging.

## Prerequisites

Yagu is Windows-only.

- Windows 10 version 1809 / build 17763 or newer.
- .NET 10 SDK. The repo pins SDK `10.0.107` with `rollForward: latestFeature` in [global.json](global.json).
- PowerShell for the helper scripts.
- Rust stable toolchain if you want the native `yagu_core.dll` fast path. The app still builds and runs without Rust if you pass `-p:BuildRustCore=false`; it will use the managed scanner.
- voidtools Everything is optional but strongly recommended. Yagu can use the in-process Everything SDK or `es.exe`; if neither is available, it falls back to recursive .NET file enumeration.

For app development, Visual Studio 2022 or Build Tools with Windows desktop/Windows SDK components is recommended because the main app is an unpackaged WinUI 3 application. The test project avoids WinUI dependencies and can run on a normal Windows .NET SDK installation.

## Quick Start

```powershell
git clone <repo-url>
cd agentRansackAlternative

dotnet restore Yagu.sln
dotnet build Yagu.sln -c Release
dotnet run -c Release --project Yagu -- --dir "D:\projects\myapp"
```

If Rust is not installed or you want to iterate on managed code only:

```powershell
dotnet build Yagu.sln -c Release -p:BuildRustCore=false
dotnet run -c Release --project Yagu -p:BuildRustCore=false -- --dir "D:\projects\myapp"
```

The C# app loader tolerates a missing `yagu_core.dll`; native search is an optimization, not a hard runtime requirement.

## Common Commands

### Restore

```powershell
dotnet restore Yagu.sln
```

### Build

```powershell
dotnet build Yagu.sln -c Debug
dotnet build Yagu.sln -c Release
```

### Build Without Rust

```powershell
dotnet build Yagu.sln -c Release -p:BuildRustCore=false
```

### Run The App

```powershell
dotnet run -c Release --project Yagu
dotnet run -c Release --project Yagu -- --dir "D:\projects\myapp"
dotnet run -c Release --project Yagu -- --dir="D:\projects\myapp"
dotnet run -c Release --project Yagu -- --window-mode traditional
```

`--window-mode` accepts the same four modes exposed in Settings: `minimize-to-tray`, `stay-open`, `always-on-top`, and `traditional`. Numeric values `0` through `3` are also accepted. `--windowing-mode` is available as an alias.

`--max-depth <n>` limits how many levels of subdirectories are searched below the root directory. `0` (the default) means unlimited. For example, `--max-depth 2` searches the root and up to two levels of child folders.

### Run .NET Tests

```powershell
dotnet test Yagu.Tests/Yagu.Tests.csproj
dotnet test Yagu.Tests/Yagu.Tests.csproj -c Release
```

### Run Focused .NET Tests

```powershell
dotnet test Yagu.Tests/Yagu.Tests.csproj -c Release --filter ContentSearcherTests
dotnet test Yagu.Tests/Yagu.Tests.csproj -c Release --filter SearchServiceTests
dotnet test Yagu.Tests/Yagu.Tests.csproj -c Release --filter NativeParityTests
```

### Run UI Automation Tests

Most tests run without extra setup, but the match-navigation UI regression test is opt-in because it launches Yagu, drives the desktop UI through UI Automation, and captures screenshots. It requires Windows in an interactive desktop session, a Debug build of the app, and `YAGU_RUN_UI_REGRESSION=1`. Without that variable, the test exits early with a skipped message.

```powershell
dotnet build Yagu/Yagu.csproj -c Debug
$env:YAGU_RUN_UI_REGRESSION = '1'
dotnet test Yagu.Tests/Yagu.Tests.csproj -c Release --filter MatchNavRegressionTests
```

### Run Coverage

```powershell
dotnet test Yagu.Tests/Yagu.Tests.csproj -c Release --settings Yagu.Tests/coverage.runsettings --collect:"XPlat Code Coverage"
```

Coverage output is written under `TestResults/`.

### Run Rust Tests

```powershell
cargo test --manifest-path yagu-core/Cargo.toml
cargo test --manifest-path yagu-core/Cargo.toml --release
```

### Run Benchmarks

```powershell
dotnet run -c Release --project Yagu.Benchmarks
```

For a short BenchmarkDotNet smoke run similar to CI:

```powershell
dotnet run -c Release --project Yagu.Benchmarks -- --filter "*LiteralSearch*" --job short --launchCount 1 --warmupCount 1 --iterationCount 1
```

### Register Explorer Context Menu

The script writes per-user registry entries under `HKCU`, so it does not require machine-wide installation.

```powershell
.\scripts\register-context-menu.ps1 -ExePath 'C:\Tools\Yagu\Yagu.exe'
```

Uninstall the context menu entry:

```powershell
.\scripts\register-context-menu.ps1 -Uninstall
```

The registered command launches `Yagu.exe --dir "%V"` for folder and folder-background right-clicks.

### Build Installer (EXE)

The installer is built with [Inno Setup 6](https://jrsoftware.org/isdl.php) (free). Install it first:

```powershell
winget install JRSoftware.InnoSetup
```

Then build the installer from the repo root:

```powershell
.\build-installer.ps1
```

This builds Yagu in Release, stages the output, compiles an installer EXE at `installer\output\YaguSetup-<version>.exe`, and copies the latest versioned installer to `installer\YaguSetup-<version>.exe`.

Running `dotnet publish` for the Yagu project also builds the installer after publish completes. To publish without rebuilding the installer, pass `-p:BuildInstallerOnPublish=false`.

At install time, the setup program checks for the x64 .NET 10 runtime before copying files. If it is missing, setup offers to download [.NET 10.0 Runtime (v10.0.9) - Windows x64 Installer](https://dotnet.microsoft.com/en-us/download/dotnet/thank-you/runtime-10.0.9-windows-x64-installer?cid=getdotnetcore), shows download progress, and runs the runtime installer before continuing.

To skip the build step and use existing Release output:

```powershell
.\build-installer.ps1 -SkipBuild
```

To specify a custom Inno Setup path:

```powershell
.\build-installer.ps1 -InnoSetupPath "C:\Path\To\ISCC.exe"
```

The installer creates a Start Menu shortcut, optional desktop icon, optional Explorer context menu, and a full uninstaller.

## Repository Layout

| Path | Purpose |
| --- | --- |
| [Yagu.sln](Yagu.sln) | Current solution for the app, tests, and benchmarks. |
| [Yagu](Yagu/) | Main WinUI 3 application: XAML UI, view model, services, models, and native wrappers. |
| [Yagu/Yagu.csproj](Yagu/Yagu.csproj) | App project, package references, WinUI settings, and Rust build integration. |
| [Yagu/UI/Windows/MainWindow](Yagu/UI/Windows/MainWindow/) | Main search window XAML and partial code-behind files. |
| [Yagu/UI/Windows/Settings](Yagu/UI/Windows/Settings/) | Settings window XAML and code-behind. |
| [Yagu/UI/Windows/Help](Yagu/UI/Windows/Help/) | Help window XAML and code-behind. |
| [Yagu/ViewModels/MainViewModel.cs](Yagu/ViewModels/MainViewModel.cs) | MVVM search state, settings binding, result grouping, commands, memory-pressure handling. |
| [Yagu/Models](Yagu/Models/) | Search options, results, grouped result collection, progress summaries, and file groups. |
| [Yagu/Services](Yagu/Services/) | File discovery, content scanning, settings, logging, editor launch, hotkeys, result export, disk-backed result store. |
| [Yagu/Native](Yagu/Native/) | P/Invoke wrappers for `yagu_core.dll` and the Everything SDK. |
| [yagu-core](yagu-core/) | Rust native search engine built as `yagu_core.dll`. |
| [Yagu.Tests](Yagu.Tests/) | xUnit tests for the engine-facing C# code and helpers. |
| [Yagu.Benchmarks](Yagu.Benchmarks/) | BenchmarkDotNet benchmark harness. |
| [scripts/register-context-menu.ps1](scripts/register-context-menu.ps1) | Explorer context menu registration script. |
| [.github/workflows/ci.yml](.github/workflows/ci.yml) | Windows CI: Rust build/test, .NET restore/build/test, benchmark smoke test. |
| [PLANS](PLANS/) | Design notes, performance reports, and future work. |

## Architecture

At a high level, Yagu is a WinUI shell around a streaming producer/consumer search pipeline.

```text
WinUI MainWindow
    |
    v
MainViewModel
    |
    v
SearchService
    |-----------------------------|
    v                             v
FileLister                    ContentSearcher
    |                             |
    v                             v
Everything SDK / es.exe /      Rust yagu_core.dll
.NET fallback                  or managed C# scanner
    |                             |
    |------------- channels -------|
                  |
                  v
        SearchResultCollection
                  |
                  v
          grouped WinUI result UI
```

### Main Components

| Component | Responsibility |
| --- | --- |
| `MainWindow` | Owns WinUI controls, dialogs, preview panel, full-file editor, clipboard/export UI, Everything install/start prompts, and global hotkey window hook. |
| `MainViewModel` | Converts UI state into `SearchOptions`, starts/cancels searches, persists settings, receives `SearchEvent` updates, groups/sorts/filters results, and handles memory-pressure eviction. |
| `SearchService` | Orchestrates the search pipeline, validates regexes, discovers files, runs parallel content workers, streams progress/matches, enforces caps, and emits fallback/memory-pressure events. |
| `FileLister` | Discovers candidate files with Everything SDK, `es.exe`, or recursive managed enumeration. Pushes some filters into Everything when possible. |
| `ContentSearcher` | Searches one file, choosing Rust native search when available and managed stream/MMF search otherwise. Applies binary, extension, size, encoding, and cancellation policies. |
| `NativeSearcher` | C# P/Invoke wrapper over `yagu_core.dll`, including ABI checks, native sessions, streaming callbacks, and native status mapping. |
| `ResultStore` | Disk-backed temp store for match payloads evicted during memory pressure. |
| `SearchResultCollection` | WinUI-free grouped result model used by the UI and large-result performance tests. |
| `SettingsService` | Loads and saves `%APPDATA%\Yagu\settings.json`. |
| `LogService` | Writes `%APPDATA%\Yagu\yagu.log` and rotates large logs. |

## Search Pipeline

1. The user chooses a directory, enters a query, and clicks Search or presses Enter.
2. `MainViewModel` validates the input and builds a `SearchOptions` object from the current UI/settings state.
3. `SearchService` validates regex syntax up front so the UI can show a clear error.
4. `FileLister` streams file paths from the best available backend.
5. File paths flow through bounded channels so discovery cannot outrun scanning indefinitely.
6. Search workers scan files concurrently with a safe configurable degree of parallelism.
7. Each matching line is emitted as a `SearchResult`; high-volume filename hits can be batched.
8. Progress, skip counts, fallback reasons, memory-pressure events, and completion summaries flow back as `SearchEvent` records.
9. `MainViewModel` adds results to grouped collections on the UI thread and loads file metadata in the background.
10. The preview panel hydrates evicted result payloads from `ResultStore` when needed.

## File Discovery Backends

Yagu tries file discovery backends in this order when the setting is `Auto`:

1. Everything SDK: in-process API, fastest path when Everything is installed and its database is loaded.
2. `es.exe`: voidtools Everything command-line client.
3. Managed .NET enumeration: slower fallback with cycle protection for recursive directory walking.

The backend can also be forced in Settings:

- Auto: SDK -> `es.exe` -> .NET.
- Everything SDK only.
- `es.exe` only.
- .NET enumeration only.

The Everything SDK path can pre-filter by extension, maximum size, and simple excluded path segments before those files enter the content-search pipeline. Complex globs are still checked by Yagu after discovery.

If Everything is not installed or not running, the UI can prompt to start it or install it. Searches still work without Everything; they just use the managed recursive enumerator.

## Content Search Engine

Yagu has two content-search implementations:

- Native fast path: [yagu-core](yagu-core/) builds `yagu_core.dll`, and [Yagu/Native/NativeSearcher.cs](Yagu/Native/NativeSearcher.cs) calls it through P/Invoke.
- Managed fallback: [Yagu/Services/ContentSearcher.cs](Yagu/Services/ContentSearcher.cs) reads files with buffered streams or memory-mapped files and uses compiled .NET regex/literal matching.

The native path is optional. If the DLL is missing, has the wrong architecture, or fails the ABI check, Yagu logs the condition and uses the managed scanner.

The Rust crate exposes a C ABI and supports:

- UTF-16 Windows paths from C# and UTF-8 search patterns.
- Literal and regex matching.
- Case-sensitive and case-insensitive options.
- Context-before/context-after capture.
- Binary-file detection (skipped by default, opt-in via Search binary toggle).
- Max file size and max result limits.
- Cancellation polling.
- Streaming callbacks so matches do not need to be buffered as one giant native result.
- Native sessions that compile the matcher once and reuse it across many files.

By default, the Rust crate uses the in-tree scanner in [yagu-core/src/scan.rs](yagu-core/src/scan.rs). Experimental ripgrep-library spike code exists behind the `grep_crates` Cargo feature and is off by default.

## Performance

### UI Update Throttling

Search statistics (files scanned, files skipped, matches found, files/second) are **not** updated per-file. A dedicated `PeriodicTimer` in `SearchService` emits a `SearchEvent.Progress` snapshot every **100 ms**. The view model handles each snapshot by setting approximately six properties that cascade into roughly ten `PropertyChanged` notifications per tick. At 10 Hz this is lightweight and does not compete with the search pipeline for dispatcher time.

The `ProgressBar` binds to `TotalFiles` and `FilesScanned` with compiled `x:Bind` (no reflection). The auto-scroll timer that keeps the result list pinned to the bottom fires at **500 ms**, well below the threshold for perceptible UI stutter.

### Result Batching

Content and filename matches are batched before delivery to the UI thread:

| Match type | Batch size | Delivery mechanism |
| --- | --- | --- |
| Content matches | 256 | `SearchEvent.MatchBatch` via bounded channel |
| Filename matches | 256 | `SearchEvent.MatchBatch` via bounded channel |

Each result added to a `FileGroup` can trigger up to three `CollectionChanged` events (group collection, `VisibleResults` sub-collection, and top-level `VisibleGroups`). In a worst-case batch where every result belongs to a different new file, one batch may produce up to ~768 `CollectionChanged` notifications in a single dispatcher tick. WinUI handles incremental `Add` without a full layout pass, but very high-cardinality searches can still cause brief dispatcher stalls at batch boundaries.

### FileGroup Metadata

File size and last-modified metadata are loaded off the UI thread via `Task.Run`. Only the four property-change notifications (`FileSize`, `LastModified`, `FormattedSize`, `FormattedDate`) dispatch back to the UI. Because these bind to collapsed expander headers, the layout cost is minimal until the user expands a group.

### Memory Pressure

When the process working set exceeds the configured memory limit, `SearchService` switches to memory-pressure mode. Result payloads are evicted to disk-backed `ResultStore` temp files under `%TEMP%`, and only lightweight metadata stays in memory. The preview panel rehydrates payloads on demand when the user selects a result.

## Settings, Logs, And Temp Files

| Data | Location |
| --- | --- |
| User settings | `%APPDATA%\Yagu\settings.json` |
| App log | `%APPDATA%\Yagu\yagu.log` |
| Rotated app log | `%APPDATA%\Yagu\yagu.log.old` |
| Crash log | `yagu-crash.log` next to the running executable when possible |
| Memory-pressure result temp files | `%TEMP%\yagu-results-*.tmp` |

Settings include recent directories, search history, search defaults, file limits, skip extensions, backend selection, parallelism, memory limits, editor command, preview mode, word wrap, global hotkey, and log verbosity.

In CLI mode, Yagu looks for `.yagu.json` in the current working directory first. If it is not present there, Yagu checks the running process launch directory next, then falls back to `%APPDATA%\Yagu\settings.json`. CLI flags override any file-based settings.

The app schedules best-effort cleanup for orphaned Yagu result temp files on startup.

## Test Setup

The .NET test project intentionally compiles selected source files from [Yagu](Yagu/) directly instead of referencing the WinUI app project. This keeps the test assembly free of Windows App SDK UI runtime requirements and makes engine/helper behavior easier to test.

Areas covered by [Yagu.Tests](Yagu.Tests/) include:

- `ContentSearcher` literal, regex, binary, size, encoding, and skip behavior.
- `SearchService` progress, batching, limits, fallback events, and memory pressure behavior.
- `FileLister` fallback enumeration and cycle-protection behavior.
- `NativeSearcher` outcome mapping and native/managed parity.
- Settings persistence and migration.
- Result grouping, sorting, filtering, selection, and large-result UI model behavior.
- Editor launcher command parsing.
- Hotkey registration helpers.
- Export formatting for selected files and selected content.
- Syntax highlighting, glob matching, line truncation, and other helpers.

Run Rust tests when changing [yagu-core](yagu-core/). Run .NET tests when changing [Yagu/Services](Yagu/Services/), [Yagu/Models](Yagu/Models/), [Yagu/Helpers](Yagu/Helpers/), [Yagu/Native](Yagu/Native/), or settings behavior. Run both when changing the native ABI or native search semantics.

## CI

The GitHub Actions workflow in [.github/workflows/ci.yml](.github/workflows/ci.yml) runs on Windows and performs:

1. Checkout.
2. Setup .NET 10 SDK.
3. Setup Rust stable.
4. Cache Cargo registry/git/target directories.
5. Build `yagu-core` in release mode.
6. Test `yagu-core` in release mode.
7. Restore [Yagu.sln](Yagu.sln).
8. Build [Yagu.sln](Yagu.sln) in release mode.
9. Run [Yagu.Tests](Yagu.Tests/).
10. Upload test result artifacts.
11. Run a short BenchmarkDotNet smoke benchmark.

## Development Notes

- Prefer [Yagu.sln](Yagu.sln); [QuickGrep.sln](QuickGrep.sln) is stale.
- The app build attempts `cargo build --release --quiet` for [yagu-core](yagu-core/) before C# compilation unless `BuildRustCore=false` is set.
- After changing Rust FFI exports or ABI-sensitive code, rebuild [yagu-core](yagu-core/) before running .NET native parity tests.
- The native DLL must match the process architecture.
- Avoid broad formatting churn in [yagu-core](yagu-core/) unless that is the goal of the change.
- The managed scanner remains important because it is the fallback path and handles cases the native engine may reject.
- Keep UI-free behavior in services/models where possible so tests can cover it without WinUI.
- Use the memory and result caps during broad scans; unchecked searches over an entire drive can produce very large result sets.

## Troubleshooting

### `cargo` is not installed

Build without the native fast path:

```powershell
dotnet build Yagu.sln -c Release -p:BuildRustCore=false
```

Install Rust from https://rustup.rs/ when you want to build and test [yagu-core](yagu-core/).

### Everything is missing or not running

Yagu can still search with managed enumeration. For best file-discovery speed, install voidtools Everything and make sure the Everything process/database is running. The app also checks this on startup and can prompt to start or install it.

### Search sees access-denied errors

Some folders require elevation. Use the in-app "Restart as Admin" action from the warning banner, or search a directory that your user account can read.

### Tests need native behavior

Build the Rust DLL first:

```powershell
cargo build --manifest-path yagu-core/Cargo.toml --release
dotnet test Yagu.Tests/Yagu.Tests.csproj -c Release --filter NativeParityTests
```

### The result set is huge

Use include globs, exclude globs, skip extensions, max file size, and max result settings. If memory pressure is detected, Yagu pages result payloads to disk and continues in memory-saving mode.
