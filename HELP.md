# Yagu Help Guide

Yagu is a fast Windows search app for finding text, regex matches, or file names across large folder trees. The name means "Yet Another Grep Utility". It is built for repeated code, log, and source-tree investigation where you want command-line search speed with a graphical result browser.

## Quick Start

1. Open Yagu.
2. Choose a directory to search.
   - Type or paste a path into the directory box.
   - Click Browse to pick a folder.
   - Drag a folder onto the window.
   - Start from a command line with `Yagu.exe --dir "D:\projects\myapp"`.
3. Enter the search query.
4. Choose search options such as Regex, Case sensitive, and search mode.
5. Click Search or press Enter in the search box.
6. Watch results stream in while the search is still running.
7. Click a result or match line to preview it.
8. Use Open, Editor, copy, or export actions to work with the results.

The bottom-left status text shows progress while a search is active. When the search finishes or is canceled, it shows elapsed time and processed files per second.

## Main Screen

The main screen has six working areas:

| Area | Purpose |
| --- | --- |
| Title bar | App title, About button, and Settings gear. |
| Directory bar | The folder tree to search. |
| Search bar | Query entry, Search, and Cancel. |
| Options row | Common search controls such as Regex, case sensitivity, context, search mode, skip extensions, and binary skipping. |
| Results pane | Matching files and lines, with sorting, grouping, filtering, selection, copy, and export actions. |
| Preview pane | Match preview, full-file view/editing, open-in-editor actions, preview mode, and word wrap. |

Click the question-mark button next to the settings gear to open the About dialog. Click the gear to open Settings.

## Search Modes

Use the search mode dropdown to decide what the query matches:

| Mode | What It Searches | Best For |
| --- | --- | --- |
| Content + Names | File contents and file names. | General investigation when either the path or the text may contain the clue. |
| Content only | File contents only. | Code, logs, config files, text dumps. |
| File names only | File names only. | Finding files by name without reading file contents. |
| File name, then content | File contents, but only for files whose names match the query first. | Narrowing content search to files with relevant names. |

## Query Options

| Option | Effect |
| --- | --- |
| Case sensitive | Requires exact casing. Leave off for case-insensitive search. |
| Regex | Treats the query as a .NET regular expression. Invalid regexes are reported before scanning starts. |
| Context | Number of lines before and after each match stored with each result row. |
| Preview | Number of surrounding lines shown in the preview pane. |
| Skip binary | Skips files detected as binary during content search. Keep this enabled for normal text searches. |

Literal searches are usually faster than regex searches. Use regex when you need pattern matching, anchors, alternation, groups, or character classes.

## Include And Exclude Filters

Use filters to reduce the number of files Yagu has to inspect.

| Field | Examples | Notes |
| --- | --- | --- |
| Include | Glob: `cs`, `ts,js,py`, `*.json;*.yaml`; Regex: `\.(cs|xaml)$` | Limits the candidate set to matching files. Glob mode accepts comma- or semicolon-separated extensions, globs, and path segments. Regex mode matches the normalized full path. |
| Exclude | Glob: `node_modules;bin;obj;.git`, `*.min.js`; Regex: `(^|/)node_modules/|\.min\.js$` | Removes matching files from the candidate set. Glob mode accepts comma- or semicolon-separated extensions, globs, and path segments. Regex mode matches the normalized full path. |
| Filter results | `Program.cs`, `error`, `Services\` | Filters already-found results without rerunning the search. |
| Filter files | `MainWindow`, `.cs`, `ViewModels` | Filters visible result groups by file name/path. |

For broad scans, start with the narrowest include filter that still answers your question. This usually improves performance more than changing CPU settings.

## Results Pane

Results are grouped by file. Each group shows the file name, match count, size, modified date, and directory. Expand a file group to see matching lines.

Common actions:

| Action | How To Use It |
| --- | --- |
| Preview a file or match | Click a file group or a match line. |
| Open a match in your editor | Double-click a result group, or use the Editor button in the preview pane. |
| Select match lines | Use checkboxes next to individual lines. |
| Select all matches in a file | Use Select All inside the expanded file group. |
| Copy selected lines | Right-click selected lines and choose Copy Selected Lines. |
| Export selected lines | Right-click selected lines and choose Export Selected to File. |
| Copy file paths | Right-click the result list or file group and choose Copy Selected File Paths. |
| Export selected files with content | Right-click the result list or file group and choose Save Selected Files With Content. |
| Disable result following | Clear Auto-scroll in the results toolbar, or scroll upward in the result list. |

Use the Sort control to leave results unsorted for fastest streaming, or sort by match count, date modified, file size, or file name. Enable Group by folder when you want large result sets organized by directory. Enable Auto-scroll when you want the list to follow newly arriving results during a search.

## Preview Pane

The preview pane shows context around the selected match and highlights the matched text.

| Control | Purpose |
| --- | --- |
| Copy path icon | Copies the full file path. |
| Full File | Shows full-file previews with highlighted matches for the selected file groups or checked match lines. |
| Open | Opens the file with the default Windows application. |
| Editor | Opens the file in the configured external editor command. |
| Concatenated | Shows selected matches as separate snippets. |
| Multi-highlight | Shows selected matches in a unified file-style view. |
| Wrap | Toggles word wrap in the preview pane. |

Full-file preview follows the left panel: checked match lines preview each selected file and highlight those selected matches; selected file rows preview those whole files and highlight their known matches.

## Settings

Open Settings from the gear button in the title bar. Settings are saved to `%APPDATA%\Yagu\settings.json`.

Important settings:

| Setting | What It Controls |
| --- | --- |
| Context lines | Match context saved in result rows. |
| Preview context lines | Match context shown in the preview pane. |
| Default include/exclude globs | Filters applied by default. |
| Max results | Stops after this many matches. Non-zero values are capped at 50,000. Use 0 for unlimited. |
| Max file size | Skips files larger than this size. Use 0 for no size limit. |
| Skip binary files | Avoids scanning binary-looking files. |
| Skip extensions | Extensions skipped entirely before file contents are read. |
| Content-search parallelism | Number of concurrent file scan workers. |
| File-listing backend | Auto, Everything SDK, `es.exe`, or .NET enumeration. |
| Process memory hard cap | Working-set limit before memory-saving behavior activates. |
| SDK channel buffer size | Path buffer between Everything SDK discovery and search workers. |
| System memory pressure limit | Machine RAM usage threshold for memory-saving mode. |
| Line truncation length | Result-list line length cap to keep the UI responsive. |
| Editor command | Command used by the Editor button. Supports `{file}` and `{line}` tokens. |
| Global hotkey | Optional `Ctrl+Shift+letter` shortcut to bring Yagu forward. |
| Log verbosity | Amount of diagnostic logging. Verbose logging can reduce performance. |

## Performance Overview

Yagu is designed around a streaming search pipeline:

1. File discovery finds candidate paths.
2. Filters remove files that do not need to be opened.
3. Content workers scan files concurrently.
4. Results stream to the UI as batches.
5. Memory-pressure mode can page result payloads to disk and keep the search alive.

The fastest path uses:

- voidtools Everything running with the Everything SDK backend.
- The Rust native scanner DLL, `yagu_core.dll`, built for the same architecture as the app.
- Release builds.
- Literal search when regex is not required.
- Tight include/exclude filters.
- Skip binary enabled.
- A practical max file size.
- A practical max result count when you are exploring very broad result sets.

The slowest searches are usually very broad scans that make Yagu open or inspect huge numbers of files. On large drives, file discovery can produce millions of candidates, and avoiding unnecessary file opens is often the biggest win.

## Reading Files Per Second

After a search completes or is canceled, the status bar includes a rate such as `12,345.6 files/sec`. This is the number of files processed divided by elapsed search time.

Use it to compare changes on the same machine and same dataset:

1. Run the same query against the same directory.
2. Change one setting at a time.
3. Compare the final files/sec value.
4. Also compare match count, skipped count, and whether the search was truncated or degraded.

Do not compare files/sec across unrelated directories. A tree of tiny source files behaves very differently from a tree of large logs, archives, binaries, or cloud-synced files.

## Performance Tuning Recipes

### Fast Code Search

- Use Content only unless file-name matches matter; use File name, then content when both must match.
- Prefer a literal query over regex.
- Include only source extensions, such as `cs;ts;js;py;rs`.
- Exclude generated folders, such as `bin;obj;node_modules;.git;target`.
- Keep Skip binary enabled.
- Use Auto file-listing backend.
- Use Auto parallelism first. Try `2x cores` on fast SSDs if the CPU is underused.

### Large Log Search

- Include only log-like extensions, such as `log;txt;json`.
- Raise Max file size if your logs are larger than the default.
- Keep Max results bounded if you only need enough examples to diagnose the issue.
- Use regex only for patterns that literal search cannot express.

### Whole Drive Or Very Large Tree

- Install and run voidtools Everything.
- Keep File-listing backend on Auto or Everything SDK only.
- Use include filters before starting the search.
- Use skip extensions aggressively for archives, databases, media, build outputs, and dumps.
- Keep Max file size set unless you specifically need very large files.
- Watch the skipped count and click it for the skip breakdown.
- If you see memory-saving mode, narrow the search or lower result volume.

### HDD Or Network Share

- Try 1 thread or Half cores instead of `2x cores`.
- Use stricter include/exclude filters.
- Avoid broad regex scans.
- Expect lower files/sec because storage latency dominates.

## File Discovery Backends

Yagu can find files in several ways:

| Backend | Description | When To Use |
| --- | --- | --- |
| Auto | Tries Everything SDK, then `es.exe`, then .NET enumeration. | Best default. |
| Everything SDK only | In-process Everything API. Fastest when Everything is installed and running. | Maximum discovery speed. |
| `es.exe` only | Uses the voidtools command-line client. | Useful when SDK is unavailable but `es.exe` works. |
| .NET enumeration only | Built-in recursive enumeration. | No Everything dependency, but slower on very large trees. |

If Everything is missing or not running, Yagu falls back and shows the reason in the UI. Searches still work without Everything.

## Native Scanner And Managed Fallback

Yagu has two content search engines:

- Native Rust fast path through `yagu_core.dll`.
- Managed C# fallback path.

If the native DLL is missing, has the wrong architecture, or fails its ABI check, Yagu logs the issue and continues with the managed scanner. The managed path is slower for many workloads, but it preserves functionality.

## Memory Behavior

Large searches can produce many results. Yagu uses several safeguards:

- Bounded channels apply back-pressure between discovery, scanning, and UI updates.
- Max results can stop runaway result streams.
- Max file size prevents accidental reads of enormous files.
- Skip binary and skip extensions reduce unnecessary reads.
- Memory-pressure mode pages result payloads to `%TEMP%\yagu-results-*.tmp`.
- The status text says when memory-saving mode is active.

If memory-saving mode appears often, reduce result volume with include/exclude filters, a more specific query, fewer context lines, or a lower max result count.

## Logs And Diagnostics

| Data | Location |
| --- | --- |
| Settings | `%APPDATA%\Yagu\settings.json` |
| Current log | `%APPDATA%\Yagu\yagu.log` |
| Rotated log | `%APPDATA%\Yagu\yagu.log.old` |
| Crash log | `yagu-crash.log` next to the executable when possible. |
| Memory-pressure result temp files | `%TEMP%\yagu-results-*.tmp` |

Use Info logging for normal troubleshooting. Use Verbose only when you need detailed diagnostics, because it can add overhead during large searches.

## Troubleshooting

### No results appear

- Confirm the directory exists and is readable.
- Check whether Content only, File names only, File name, then content, or Content + Names is selected.
- Clear Include, Exclude, Filter results, and Filter files to rule out filters.
- Turn off Regex if the query should be literal text.
- Check the status area for invalid regex or fallback messages.

### Search is slower than expected

- Install and run voidtools Everything.
- Keep File-listing backend on Auto.
- Build/run Release rather than Debug.
- Use include filters and skip extensions.
- Keep Skip binary enabled.
- Avoid broad regex searches when a literal query works.
- Turn off Verbose logging.
- Compare the final files/sec value after changing only one setting.

### Access denied or missing files

- Some directories require administrator rights.
- Use the Restart as Admin action in the banner if you need elevated access.
- Cloud-synced, offline, locked, or protected files may still be skipped.
- Click the skipped count to see why files were skipped.

### Search gets truncated

The result cap was reached. Use a narrower query or change Max results in Settings. Setting Max results to 0 allows unlimited results, but memory-pressure protections can still activate.

### Memory-saving mode appears

Yagu detected high process or system memory pressure and paged result payloads to disk. The search can continue, but previews may need to hydrate data from temp storage. Narrow the query or reduce context/result volume for better responsiveness.

### Everything is not used

- Make sure voidtools Everything is installed and running.
- Keep the backend on Auto or Everything SDK only.
- If SDK is unavailable, install or configure `es.exe` so Yagu can use the second backend.
- If neither backend is available, Yagu uses .NET enumeration.

## Running From Source

Use these commands from the repository root:

```powershell
dotnet restore Yagu.sln
dotnet build Yagu.sln -c Release
dotnet run -c Release --project Yagu -- --dir "D:\projects\myapp"
```

If Rust is not installed and you only need the managed fallback:

```powershell
dotnet build Yagu.sln -c Release -p:BuildRustCore=false
dotnet run -c Release --project Yagu -p:BuildRustCore=false
```

## Running Benchmarks

Yagu includes a BenchmarkDotNet project for repeatable search benchmark scenarios:

```powershell
dotnet run -c Release --project Yagu.Benchmarks
```

For a shorter smoke benchmark:

```powershell
dotnet run -c Release --project Yagu.Benchmarks -- --filter "*LiteralSearch*" --job short --launchCount 1 --warmupCount 1 --iterationCount 1
```

Benchmarks are useful for code changes. For day-to-day tuning, the in-app files/sec rate is faster to compare.

## Explorer Context Menu

Yagu can be launched from a folder right-click menu with the helper script:

```powershell
.\scripts\register-context-menu.ps1 -ExePath 'C:\Tools\Yagu\Yagu.exe'
```

Remove the entry with:

```powershell
.\scripts\register-context-menu.ps1 -Uninstall
```

The context menu command opens Yagu with the selected folder already filled in.

## Practical Defaults

For most users, start with these defaults:

- File-listing backend: Auto.
- Content-search parallelism: Auto.
- Skip binary files: enabled.
- Skip extensions: keep the default broad binary/archive/media/database list.
- Max file size: keep a practical limit unless you know you need huge files.
- Max results: 0 (unlimited) by default; set a bounded value while exploring very broad result sets.
- Log verbosity: Warning or Info.

Then tune only the filters and query until the result set is small enough to review comfortably.