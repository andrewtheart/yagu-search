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
| Search binary | Includes binary files in content search. Leave this off for normal text searches. |

Literal searches are usually faster than regex searches. Use regex when you need pattern matching, anchors, alternation, groups, or character classes.

## Advanced Options

Click the **Advanced Options** expander below the search bar to reveal additional search controls. These settings are per-session and reset to defaults on the next launch (defaults can be configured in Settings).

### Search Behavior

| Control | Effect |
| --- | --- |
| Search mode | Chooses what the query matches: Content + Names, Content only, File names only, or File name then content. |
| Obey .gitignore | When enabled, Yagu reads `.gitignore` files in the directory tree and excludes matching paths from the file listing. Has a performance cost on very large trees because each directory must be checked for gitignore rules. |

### Path Filters

| Control | Effect |
| --- | --- |
| Include filter | Limits the search to files matching these patterns. Accepts comma- or semicolon-separated extensions, globs, and path segments. Switch the adjacent dropdown between Glob and Regex modes. |
| Exclude filter | Removes files matching these patterns from the search. Same syntax options as Include. |

### File Type Filters

| Control | Effect |
| --- | --- |
| Skip Extensions | A dropdown list of file extensions to skip entirely. Files with these extensions are never opened or read. Use the All/None links to quickly toggle the whole list. |
| Search binary | When enabled, files detected as binary (null bytes, magic headers) are included in content search. Off by default for speed. |
| Search archives | When enabled, Yagu opens ZIP-format containers (ZIP, DOCX, XLSX, JAR, NUPKG, etc.) and searches text entries inside them. Has a performance cost. |
| Archive Extensions | Visible when Search archives is on. Controls which extensions are treated as ZIP-format containers. |

### Property Filters (Size and Date)

| Control | Effect |
| --- | --- |
| Min MB | Only include files at least this many megabytes. 0 or blank means no lower limit. |
| Max MB | Only include files at most this many megabytes. 0 or blank means no upper limit. |
| Created Date From/To | Only include files created within this date range. Leave blank for no restriction. |
| Modified Date From/To | Only include files modified within this date range. Leave blank for no restriction. |

### Max Depth

| Control | Effect |
| --- | --- |
| Max depth | Maximum number of subdirectory levels to recurse below the search root for the current search only. 0 means unlimited (search the entire tree). For example, a value of 2 searches the root directory and up to two levels of child folders. This value is not saved to settings. |

---

## Results Pane (Left Panel)

Results are grouped by file. Each group header shows the file name, match count, file size, modified date, and directory. Expand a file group to see individual matching lines with context.

### Results Toolbar

| Control | Purpose |
| --- | --- |
| Sort | Changes the order of result file groups. Options: None (arrival order, fastest), Match count (descending/ascending), Date modified, File size, File name. |
| Group | Groups result files into collapsible sections. Options: None, Folder (A–Z / Z–A), Date range modified, Date range created, Extension, File size range. |
| Auto-scroll | When checked, the result list scrolls to follow newly arriving results during a search. Uncheck or scroll upward to freeze the view. |
| Context lines | Number of lines before and after each match stored with result rows. Higher values provide more context but use more memory. |
| Clear results | Removes all results from the list (trash icon, or Ctrl+Shift+Delete). |
| Expand/collapse | Toggles between an expanded result list and a split view with the preview panel. |

### Filtering Results

Below the toolbar, the results panel has a filter bar:

| Control | Purpose |
| --- | --- |
| Select All checkbox | Checks or unchecks all file groups at once. |
| Filter files textbox | Instantly filters visible file groups by file name or path substring. Does not re-run the search. |
| Date range filter | Quickly narrows results by modification date: Last day, Last week, Last month, Last year, etc. |

### Right-Click Context Menus

**Right-click on a file group header:**

| Option | Action |
| --- | --- |
| Preview | Opens a full-file preview of that file in the right panel. |
| Preview all selected | Previews all checked/selected files. |
| Copy Full Path | Copies the file's full path to clipboard. |
| Copy Selected File Paths | Copies paths of all checked files. |
| Copy Selected Files With Content | Copies file paths and their matched content. |
| Save Selected File Paths… | Saves checked file paths to a text file. |
| Save Selected Files With Content… | Saves checked files with matched content to a text file. |

**Right-click on a match line:**

| Option | Action |
| --- | --- |
| Copy Selected Lines | Copies all checked match lines to clipboard. |
| Export Selected to File… | Saves checked match lines to a text file. |
| Copy This Line | Copies just the right-clicked line. |

### Previewing Files

- **Single file:** Right-click a file group header and select **Preview**. Or simply click the file group header.
- **Multiple files:** Check the files you want using the checkboxes in the left panel, then right-click any file and select **Preview all selected** (shows "Preview selected (N)").
- **Match lines:** Click any match line to see a context preview in the right panel.
- **Double-click a file header:** Selects all matches in that file and shows them in the preview.

---

## Preview Pane (Right Panel)

The preview pane displays file content with highlighted matches and line numbers.

### Preview Toolbar

| Button | Icon | Purpose |
| --- | --- | --- |
| Full File | 📄 | Shows the complete file content with all matches highlighted. |
| Copy Path | 📋 | Copies the previewed file's full path to clipboard. |
| Open | 🔗 | Opens the file with the default Windows application. |
| Edit | ✏️ | Opens the file in the built-in editor (editable mode with save). |
| Show in Explorer | 📁 | Opens the containing folder in Windows Explorer. |
| Expand All | ↕ | Expands and renders all collapsed sections (visible for multi-file previews). |
| Clear | ✕ | Clears all files from the preview pane. |
| Export Report | 📊 | Exports all current results as a styled HTML report with highlighted matches, line numbers, and context. |

### View Options

| Control | Purpose |
| --- | --- |
| Layout → Concatenated | When multiple files are previewed, shows each file as a separate section stacked vertically. |
| Layout → Multi-highlight | Shows multiple selections merged into a unified view. |
| Word Wrap | Toggles line wrapping in the preview. Long lines either wrap or scroll horizontally. |
| Find & Replace | Opens the find/replace bar (Ctrl+H). Search within the previewed content. |
| Preview Context | Number of context lines shown around each match in the preview. Adjust in real time. |

### Match Navigation

When viewing a file with multiple matches, use the match navigation controls at the bottom-right of the preview:

| Control | Purpose |
| --- | --- |
| "N of M" label | Shows your position among the matches in the current file. |
| Previous (◀) | Jumps to the previous match. Keyboard: Shift+Enter. |
| Next (▶) | Jumps to the next match. Keyboard: Enter. |
| Ctrl+Click | Jumps multiple matches at once. |

### Per-File Section Headers

When previewing multiple files, each file section has its own header bar with:

| Control | Purpose |
| --- | --- |
| File path | Full path displayed in the section header. |
| Section match nav | Previous/Next match navigation within that file section. |
| Dismiss | Closes that file section from the preview. |

### Double-Click to Edit

**Double-click on a highlighted match** in the preview panel to open the built-in editor and jump directly to that line and word. This lets you quickly navigate from a search result to an editable view at the exact match location.

### Built-in Editor

Click the **Edit** (pencil) button in the preview toolbar to enter editor mode. The editor provides:

- Full file editing with syntax-aware text display.
- **Save** button to write changes back to disk.
- **Back** button to return to preview mode.
- **Backup on save:** When you save a file, Yagu automatically creates a backup named `{filename}.yagubak` in the same directory. If a backup already exists, it appends a number (`{filename}.yagubak-2`, `.yagubak-3`, etc.). This behavior is controlled by the "Backup before save" setting (on by default).
- Large files (over ~10 MB) are loaded in chunks with a "Load More" button to avoid excessive memory use.

### Find and Replace

Open the find bar with **Ctrl+F** (find only) or **Ctrl+H** (find and replace).

| Control | Purpose |
| --- | --- |
| Find textbox | Type text to search within the previewed file. |
| Previous / Next | Navigate between find matches. |
| Match case (Aa) | Toggles case-sensitive find. |
| Replace textbox | Replacement text (visible when replace is toggled on). |
| Replace | Replaces the current match. |
| All | Replaces all matches in the current file. |
| All Files | Replaces in all result files on disk (prompts for confirmation). |

> **Note:** The "Replace in All Files" feature performs disk writes across multiple files. A confirmation dialog shows how many occurrences and files will be affected before proceeding.

## Settings

Open Settings from the gear button in the title bar. Settings are saved to `%APPDATA%\Yagu\settings.json`.

Settings are organized into tabs:

### Search Defaults Tab

| Setting | What It Controls |
| --- | --- |
| Case sensitive | Default state of case-sensitive matching. |
| Regex | Default state of regex mode. |
| Exact match | Default state of whole-word matching. |
| Context lines | Match context lines saved in result rows. |
| Preview context lines | Match context lines shown in the preview pane. |
| Default include/exclude globs | Filters applied by default on app start. |

### Search Limits Tab

| Setting | What It Controls |
| --- | --- |
| Max results | Stops after this many matches. Non-zero values capped at 50,000. Use 0 for unlimited. |
| Default file size filter | Min/Max MB applied by default. Both 0 = any size. |
| Default date filters | Created/Modified date ranges applied by default. |
| Search binary files | Includes binary-looking files in the scan. Off by default. |
| Skip admin-protected paths | Excludes Windows system directories that always deny access when not elevated. |
| Admin-protected path segments | Custom list of path segments to skip (one per line or semicolon-separated). |
| Skip extensions | Extensions skipped entirely before file contents are read. |
| Archive extensions | Extensions treated as ZIP-format containers when archive search is on. |

### Performance Tab

| Setting | What It Controls |
| --- | --- |
| Content-search parallelism | Number of concurrent file scan workers: Auto, 1 thread, Half cores, 2× cores, All cores. |
| File-listing backend | Auto, Everything SDK, `es.exe`, or .NET enumeration. |
| Process memory hard cap (MB) | Working-set limit before memory-saving behavior activates. |
| SDK channel buffer size | Path buffer capacity between Everything SDK discovery and search workers. |
| System memory pressure limit | Machine RAM usage percentage threshold for memory-saving mode. |
| Line truncation length | Result-list line length cap to keep the UI responsive with very long lines. |
| Limit parallelism on HDD | Automatically reduces thread count when searching on a rotational drive. |

### Editor Tab

| Setting | What It Controls |
| --- | --- |
| Editor command | External command used by the Editor button. Supports `{file}` and `{line}` placeholder tokens. Example: `code -g {file}:{line}` for VS Code. |
| Backup before save | When on (default), the built-in editor creates a `.yagubak` file before overwriting. |
| Preview editor max size (MB) | Maximum file size the built-in editor will load. |

### UI Tab

| Setting | What It Controls |
| --- | --- |
| Global hotkey | Optional system-wide keyboard shortcut to bring Yagu to the foreground. |
| Window focus behavior | Controls window mode: Minimize to tray, Stay open, Always on top, or Traditional window. |
| Close to tray | When enabled, closing the window minimizes to the system tray instead of exiting. |
| Max recent items | Number of recent directories and queries stored. |
| Log verbosity | Amount of diagnostic logging. Verbose logging can reduce performance. |

---

## Keyboard Shortcuts

| Shortcut | Action |
| --- | --- |
| Enter (in search box) | Start search. |
| Escape (in search box) | Cancel running search. |
| F5 | Start search. |
| Ctrl+F | Open Find bar in preview. |
| Ctrl+H | Open Find & Replace bar in preview. |
| Ctrl+Shift+Delete | Clear all results. |
| Ctrl+Shift+S | Copy window screenshot to clipboard. |
| Enter (in preview) | Jump to next match. |
| Shift+Enter (in preview) | Jump to previous match. |
| Alt+C | Toggle case sensitive. |
| Alt+R | Toggle regex. |
| Alt+E | Toggle exact match. |

---

## Window Modes

The pin/window button in the title bar cycles through four modes:

| Mode | Behavior |
| --- | --- |
| Minimize to tray | Window minimizes to the system tray when it loses focus. Click the tray icon to restore. |
| Stay open | Normal window behavior. |
| Always on top | Window stays above other windows. |
| Traditional window | Full window with standard title bar and close button (default). |

---

## Drag and Drop

Drag a folder from Windows Explorer onto the Yagu window to set it as the search directory.

---

## System Tray

When tray mode is active, the system tray icon provides:

- **Open (reset)** — Restores the window and clears the directory.
- **Open (existing)** — Restores the window keeping the current directory.
- **Close** — Exits the application.

---

## Skipped Files

During a search, the status bar shows a "Skipped: N" count. Click it to see a breakdown of why files were skipped (access denied, too large, binary, extension, gitignore, admin-protected paths, etc.).

---

## Admin Elevation

If Yagu detects it cannot access certain directories, a banner appears offering:

- **Learn more…** — Explains the limitation.
- **Restart as Admin** — Relaunches Yagu with administrator privileges.
- **Don't show again** — Suppresses the banner for future sessions.

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
- Keep Search binary off.
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
- Keep Search binary off.
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
- Keep Search binary off.
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
- Search binary files: off (default).
- Skip extensions: keep the default broad binary/archive/media/database list.
- Max file size: keep a practical limit unless you know you need huge files.
- Max results: 0 (unlimited) by default; set a bounded value while exploring very broad result sets.
- Log verbosity: Warning or Info.

Then tune only the filters and query until the result set is small enough to review comfortably.