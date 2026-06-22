<h1 align="center">Yagu Help Guide</h1>

Yagu is a fast Windows search app for finding text, regex matches, or file names across large folder trees. The name stands for "Yet Another Grep Utility". It is built for repeated code, log, and source-tree investigation where you want command-line search speed with a graphical result browser.

## Quick Start

1. Open Yagu.
2. Choose a directory to search:
   - Type or paste a path into the directory box (auto-complete suggests folders as you type).
   - Click **Browse** to pick a folder.
   - Drag a folder from Windows Explorer onto the window.
   - Launch from a command line: `Yagu.exe --dir "D:\projects\myapp"`.
   - Use the Windows Explorer context menu: right-click a folder → **Search with Yagu**.
3. Enter the search query in the search box.
4. Choose search options: **Case sensitive** (Alt+C), **Regex** (Alt+R), **Exact match** (Alt+E).
5. Click **Search** or press **Enter** in the search box.
6. Results stream in while the search runs. Click a result or match line to preview it.
7. Use Open, Edit, Copy, or Export actions to work with the results.

The status bar shows progress during a search. When the search finishes or is canceled, it shows elapsed time. Enable **Stats for nerds** in Settings -> Developer Options to show files processed per second and a real-time throughput sparkline.

## Main Screen

The main screen has six working areas:

| Area | Purpose |
| --- | --- |
| Title bar | App title, Help button (F1), Settings gear, and window mode pin. |
| Directory bar | The folder to search, with auto-complete, Browse, and recent history. |
| Search bar | Query entry with history (Down arrow), Search/Cancel (F5), and option toggles. |
| Options row | Quick toggles for Regex, Case, Exact match, and the Advanced Options expander. |
| Results pane (left) | Matching files and lines with sorting, grouping, filtering, selection, copy, and export. |
| Preview pane (right) | Match preview, full-file view, built-in editor, match navigation, and export. |

- Click the **?** button or press **F1** to open Help.
- Click the **gear** to open Settings.
- Click the **pin** button to cycle window modes (Tray / Stay open / Always on top / Traditional).

---

## Search Modes

Use the search mode dropdown to decide what the query matches:

| Mode | What It Searches | Best For |
| --- | --- | --- |
| Content + Names | File contents and file names. | General investigation when either the path or text may contain the clue. |
| Content only | File contents only. | Code, logs, config files, text dumps. |
| File names only | File names only. | Finding files by name without reading contents. |
| File name, then content | File contents, but only for files whose names match the query first. | Narrowing content search to files with relevant names. |

---

## Query Options

| Option | Shortcut | Effect |
| --- | --- | --- |
| Case sensitive | Alt+C | Requires exact casing. Leave off for case-insensitive search. |
| Regex | Alt+R | Treats the query as a .NET regular expression. Invalid regexes are reported before scanning. |
| Exact match | Alt+E | Matches whole words only (word boundaries around the query). |
| Context | — | Number of lines before and after each match stored with each result row. |
| Preview context | — | Number of surrounding lines shown in the preview pane. |

Literal searches are faster than regex. Use regex when you need pattern matching, anchors, alternation, groups, or character classes.

---

## Semantic Search (Local AI)

The **Search** button has a **chevron** on its right edge (shown when semantic search is enabled in Settings). Click the chevron to open the mode menu and pick **Traditional** or **Semantic**. In **Semantic** mode you describe what you want in plain language and a local on‑device AI model translates it into concrete Yagu search options — directory, include/exclude filters, dates, sizes, search mode, and result **sorting** and **grouping** — then runs the search.

Example query:

> *search the C drive for all png files that were modified in the past year. ignore mov files and any file named "abc"*

Yagu sets the directory to `C:\`, adds an include filter for `*.png`, excludes `*.mov` and `abc`/`abc.*`, sets a "modified after" date one year ago, switches to **File names only** mode, and searches.

You can also ask Yagu to **sort** or **group** the results in the same sentence — e.g. *"find all files on C:\ with invoice2024 in the name, sort by file name and group by directory"* sets the Sort and Group controls accordingly. When you name a sort field without a direction (e.g. *"sort by file name"*), Yagu defaults to **descending**; say *"ascending"* / *"a to z"* for the other direction.

Notes:

- **Runs entirely on your machine.** Your query is never sent off the device. The model is downloaded once via Microsoft **Foundry Local** and cached for reuse.
- **Hardware‑aware.** Yagu auto‑picks the best small instruct model your machine can run. Within that model it prefers the less‑quantized **GPU** build for accuracy, falling back to **NPU** then **CPU**, so it still works even without a dedicated GPU/NPU.
- **Smart default mode.** On machines with a supported GPU/NPU, the search bar starts in **Semantic** mode automatically; machines with no supported accelerator start in **Traditional**. You can override the default for accelerated machines under **Settings → Search Defaults → Default to Traditional search mode** (that option is greyed out when no supported GPU/NPU is present). Either way you can switch modes any time from the Search‑button chevron.
- **First run asks before downloading.** The first time you switch to Semantic, a borderless dialog lists the available models with their download sizes. The model best suited to your hardware is pre‑selected and marked **Recommended**; smaller or lower‑ranked models show a ⚠ warning that they *may be less accurate*. Choose **Use this model** to download (a progress bar shows the percentage), or **Not now** to cancel — declining switches you back to **Traditional**. Already‑cached models are tagged **Downloaded** and start instantly. After the first download the prompt is not shown again.
- **Status while translating.** A progress line appears just below the search card (in the status area above the results) while the runtime/model loads and the query is being translated, so it never pushes the search box down.
- **Transparent.** After translating, Yagu fills in the Advanced Options so you can see and tweak exactly what it set before or after searching.
- **Switching back to Traditional** (via the same Search‑button chevron menu) restores the literal/regex query behavior; the inline Case/Regex/Exact toggles apply in Traditional mode only.

Configure availability and the optional preferred model under **Settings → Search Defaults**. The same capability is available from the CLI via `--semantic-pattern` (see [Command-Line Interface](#command-line-interface-cli-mode)).

---

## Advanced Options

Click the **Advanced Options** expander below the search bar to reveal a tabbed panel. The left tabs group controls into **Search**, **Filters**, **Size**, **Dates**, and **Advanced**. Advanced Options are per-session and reset to defaults on next launch (defaults can be configured in Settings). Changes apply immediately; **Apply** closes the panel, and **Reset** restores the controls to the current settings defaults.

### Search Tab

| Control | Effect |
| --- | --- |
| Search mode | Chooses what the query matches (see Search Modes above). |
| Include filter | Limits the search to files matching these patterns. Accepts comma- or semicolon-separated extensions, globs, or path segments. Switch the dropdown between **Glob** and **Regex** modes. |
| Exclude filter | Removes files matching these patterns. Same syntax options as Include. |

### Filters Tab

| Control | Effect |
| --- | --- |
| Obey .gitignore | Reads `.gitignore` files in the tree and excludes matching paths. Has a performance cost on very large trees. |
| Skip Extensions | A dropdown list of file extensions to skip entirely. Files with these extensions are never opened or read. Use **All/None** links to quickly toggle the whole list. |
| Search binary | When enabled, files detected as binary (null bytes, magic headers) are included in content search. Off by default. |
| Search archives | Opens ZIP-format containers (ZIP, DOCX, XLSX, JAR, NUPKG, etc.) and searches text entries inside. Has a performance cost. |
| Archive Extensions | Visible when Search archives is on. Controls which extensions are treated as ZIP containers. |
| Search online-only cloud files | When enabled, OneDrive/Google Drive online-only placeholder files are downloaded on demand and searched — but only while the sync provider is running to serve them. Off by default: cloud-only files are skipped so the scan can't hang on a download. Can be slow and use network/disk. |
| Search hidden files | When enabled (default), files and folders carrying the Windows Hidden attribute are searched. When disabled, hidden items are excluded — the managed file walker skips them and the Everything backends filter them with `!attrib:h`. System files (e.g. `pagefile.sys`, `hiberfil.sys`) are always skipped by the file walker regardless of this setting. The default for this per-search toggle comes from **Settings ▸ Path and File Type Filters ▸ Search hidden files**. |

### Size Tab

| Control | Effect |
| --- | --- |
| Min MB | Only include files at least this many megabytes. Blank = no lower limit. |
| Max MB | Only include files at most this many megabytes. Blank = no upper limit. |

### Dates Tab

| Control | Effect |
| --- | --- |
| Created Date From/To | Only include files created within this date range. |
| Modified Date From/To | Only include files modified within this date range. |

### Advanced Tab

| Control | Effect |
| --- | --- |
| Max depth | Maximum subdirectory levels to recurse below the search root. Blank or 0 = unlimited. A value of 2 searches the root and up to two levels of child folders. This value is per-search and is not saved to settings. |

### CLI Command

| Control | Effect |
| --- | --- |
| Generate CLI command | Builds a `Yagu.exe --cli` command from the current directory, query, search toggles, and Advanced Options. The command appears in a closable code-styled overlay with Copy, Send to terminal, and Close buttons. The **Options already saved in settings** toggle controls whether the generated command includes options that already match `%APPDATA%\Yagu\settings.json`; it defaults to **Omit** to keep commands short. Sending to the terminal expands the embedded terminal if needed, verifies the shell changed to the running Yagu executable directory, and then places the command at the prompt without pressing Enter. |

The generated command includes supported CLI flags for directory, pattern, regex/case/exact-match state, context, search mode, include/exclude mode and patterns, gitignore behavior, size/date filters, binary/archive search, skip/archive extensions, result caps, max depth, thread count, memory limits, file-listing backend, SDK buffer size, and admin-protected path handling.

### Embedded Terminal

The embedded terminal is a command shell hosted inside Yagu below the main content. Use the chevron in the status area, or the inline chevron beside Advanced Options when the status bar is hidden, to expand or collapse it.

Right-click inside the terminal pane for **Copy**, **Paste**, **Cut**, **Select all**, **Clear**, and **Reset terminal session**. **Clear** runs `cls` and clears the xterm surface; typing `cls` at the prompt clears the surface and erases the typed command line too. **Reset terminal session** disposes the current shell session and starts a fresh one; use it if the terminal appears disconnected, stuck, or out of sync. The generated CLI command overlay can send commands into this terminal without executing them, which gives you a chance to review or edit before pressing Enter.

---

## Results Pane (Left Panel)

Results are grouped by file. Each group header shows the file name, match count, file size, modified date, and directory path. Expand a file group to see individual matching lines with context.

When an expanded file's header scrolls out of view, a compact sticky strip at the top of the results list shows the current file name and includes an Explorer button for that file. Double-click the strip to collapse that file group.

### Results Toolbar

| Control | Purpose |
| --- | --- |
| Sort | Changes the order of file groups: None (arrival order), Match count ↑↓, Date modified ↑↓, File size ↑↓, File name ↑↓. |
| Group | Groups results into collapsible sections: None, Folder A–Z/Z–A, Date range (Modified/Created/Both), Extension A–Z/Z–A, File size range. |
| Auto-scroll | Scrolls to follow new results during a search. Uncheck or scroll up to freeze. |
| Context lines | Lines before/after each match in the result row. Higher = more context but more memory. |
| Clear results | Removes all results (clear-selection icon, or **Ctrl+Shift+Delete**). |
| Expand/Collapse panel | Toggles between expanded result list and split view with preview. |

### Filtering Results

Below the toolbar is a filter bar:

| Control | Purpose |
| --- | --- |
| Select All checkbox | Checks or unchecks all file groups. |
| Filter files textbox | Instantly filters visible file groups by file name or path substring. Does not re-run the search. |
| Date range filter | Narrows results by modification or creation date: Last day, Last week, Last month, Last year, etc. |

### Selecting Results

- **Checkbox** on each file group header: selects that file for batch operations.
- **Ctrl+A** in the results list: selects all file groups.
- **Select All** checkbox in the filter bar: toggles all at once.

### Right-Click Context Menus

**On a file group header:**

| Option | Action |
| --- | --- |
| Preview | Opens full-file preview of that file in the right panel. |
| Preview all selected | Previews all checked files (shows "Preview selected (N)"). |
| Open in Editor | Opens the file in your configured external editor at the first match line. |
| Open containing folder | Opens the directory in Windows Explorer. |
| Copy Full Path | Copies the file's full path to clipboard. |
| Copy Selected File Paths | Copies paths of all checked files. |
| Copy Selected Files With Content | Copies file paths and their matched content. |
| Save Selected File Paths… | Saves checked file paths to a text file. |
| Save Selected Files With Content… | Saves checked files with matched content to a text file. |

**On a match line:**

| Option | Action |
| --- | --- |
| Copy Selected Lines | Copies all checked match lines to clipboard. |
| Export Selected to File… | Saves checked match lines to a text file. |
| Copy This Line | Copies just the right-clicked line. |

### Previewing Files

- **Click a file group header** — opens a context preview of that file's matches.
- **Double-click a file header** — selects all matches and shows full preview.
- **Click a match line** — previews that match with surrounding context.
- **Right-click → Preview all selected** — multi-file preview of all checked files.

---

## Preview Pane (Right Panel)

The preview pane displays file content with highlighted matches, line numbers, and an active match overlay band showing the current match position.

### Preview Toolbar

| Button | Purpose |
| --- | --- |
| Full File | Shows the complete file content with all matches highlighted. |
| Copy Path | Copies the previewed file's full path to clipboard. |
| Open | Opens the file with the default Windows application. |
| Open in Explorer | Opens the containing folder in Windows Explorer. |
| Edit | Opens the file in the built-in editor (editable mode with save). |
| Expand All | Expands and renders all collapsed/lazy-loaded sections. |
| Export Report | Exports all current preview content as a styled HTML report. |
| Clear | Removes all files from the preview pane. |

### View Options

| Control | Purpose |
| --- | --- |
| Layout → Concatenated | Multiple files shown as stacked sections. Each file has its own header. |
| Layout → Multi-highlight | Multiple files merged into a unified highlighted view. |
| Word Wrap | Toggles line wrapping. Long lines either wrap or scroll horizontally. |
| Find & Replace | Opens the find/replace bar (**Ctrl+H**). Search within the preview. |
| Preview Context | Adjusts the number of context lines around each match in real time. |

### Match Navigation

When viewing a file with multiple matches, navigation controls appear at the bottom-right:

| Control | Purpose |
| --- | --- |
| "N of M" label | Shows your current position among matches. |
| Previous (◀) | Jump to previous match. Keyboard: **Shift+Enter**. |
| Next (▶) | Jump to next match. Keyboard: **Enter**. |
| Ctrl+Click Next/Prev | Bulk jump — the first Ctrl+Click shows a flyout asking how many matches to skip at a time. After setting the step, subsequent Ctrl+Clicks jump by that amount. |

A red flash at the boundary indicates you've reached the first or last match.

### Per-File Section Headers (Multi-File Preview)

When previewing multiple files, each file section has its own header:

| Control | Purpose |
| --- | --- |
| File path | Full path in the section header. Click to expand/collapse. |
| Open in Explorer | Opens that file's containing folder. |
| Section match nav | Previous/Next match navigation within that file section only. |
| Dismiss (×) | Closes that file section from the preview. |
| Export section | Exports just that file section as an HTML report. |

### Clipboard Copy in Preview

- **Ctrl+C** in the preview copies selected text **without line numbers** (clean content only).

### Double-Click to Edit

**Double-click on a highlighted match** in the preview to open the built-in editor and jump directly to that line and column. This is the fastest way to go from a search result to editing.

---

## Built-in Editor

Click the **Edit** (pencil) button in the preview toolbar to enter editor mode.

| Feature | Description |
| --- | --- |
| Full editing | Edit the file content directly with syntax-aware text display. |
| Syntax coloring | Colors code (keywords, strings, comments, etc.) based on the file's name or extension. Supported types include C#, C/C++, Java, JavaScript/TypeScript, Python, JSON, XML/XAML, HTML, CSS, SQL, Markdown, Lua, PHP, INI/TOML, batch, and more. Toggle in Settings → Editor. |
| Save | Write changes back to disk. Creates a `.yagubak` backup first (configurable). |
| Back | Return to preview mode (prompts if unsaved changes exist). |
| Backup on save | Automatically creates `{filename}.yagubak`. Numbered backups if one already exists. |
| Saved confirmation | Shows a brief Saved confirmation after the editor successfully writes the file. |
| Large file chunking | Files over ~10 MB load in chunks with a "Load More" button. |
| Max file size | Controlled by the "Preview editor max size" setting (default 32 MB). |
| Forced wrap | Lines longer than 50,000 characters are force-wrapped for display. |

---

## Find and Replace

Open with **Ctrl+F** (find only) or **Ctrl+H** (find and replace). The find/replace
panel appears as a floating modal over the top-right of the preview/editor — it
overlays the content instead of pushing it down, positioned below the toolbar so
it does not cover the drawer buttons. Drag it anywhere within the panel using the
**grip handle** on its left edge. The modal is fully opaque while you are using it
and dims to translucent once focus moves elsewhere (its close button stays more
visible so you can always dismiss it).

| Control | Purpose |
| --- | --- |
| Grip handle | Drag to move the floating find/replace modal. |
| Find textbox | Text to search within the preview or editor. |
| Previous / Next | Navigate between matches (with wrap-around). |
| Match case (Aa) | Toggle case-sensitive find. |
| Replace textbox | Replacement text (visible in replace mode). |
| Replace | Replace the current match. |
| Replace All | Replace all matches in the current file. |
| Replace in All Files | Replace across all result files on disk (confirmation dialog first). |

> **Warning:** "Replace in All Files" writes to disk across multiple files. A confirmation dialog shows the count of occurrences and files that will be affected.

---

## Settings

Open Settings from the **gear** button in the title bar. Settings are saved to `%APPDATA%\Yagu\settings.json`. Reset and Use default buttons are disabled when the current value already matches the default. If you close Settings with unsaved changes, Yagu asks whether to save, discard, or keep editing.

Use the search box at the top of Settings to filter settings by tab name, setting label, helper text, current value, or available option text.

### Search Defaults Tab

| Setting | What It Controls |
| --- | --- |
| Context lines | Default match context lines stored in result rows. |
| Preview context lines | Default match context lines shown in preview. |
| Default include pattern mode | Whether default include patterns are interpreted as Glob or Regex. |
| Default include patterns | Include filter applied by default on app start. Leave blank to include every eligible file. |
| Default exclude pattern mode | Whether default exclude patterns are interpreted as Glob or Regex. |
| Default exclude patterns | Exclude filter applied by default before content scanning. |
| .gitignore vs Include filter precedence | Which side wins when a file is both matched by your Include filter and excluded by .gitignore (only relevant when Obey .gitignore is on). Choose **Ask me each time** (default), **.gitignore wins**, or **Include filter wins**. The precedence prompt's **Don't ask again** option also updates this setting. |
| Default to Traditional search mode | Overrides the hardware-based startup mode. When your machine has a GPU/NPU that can run Semantic search, the search bar defaults to **Semantic**; check this to default to **Traditional** instead. Greyed out and unset on machines with no supported GPU/NPU (those always default to Traditional). You can still switch modes any time from the Search-button chevron. |

### Search Limits Tab

| Setting | What It Controls |
| --- | --- |
| Max results | Stops after this many matches. 0 = unlimited, subject to the hard ceiling and memory safeguards. |
| Max results ceiling | Hard cap applied to Max results. Values below 1,000 are not allowed. |
| Default file size filter | Minimum and maximum MB applied by default. Both 0 = any size. |
| Default created date filter | Created-after and created-before defaults for Advanced Options. Blank = any date. |
| Default modified date filter | Modified-after and modified-before defaults for Advanced Options. Blank = any date. |
| Clear date defaults | Clears all saved created/modified date defaults. |
| Search binary files | Includes files detected as binary by null bytes or magic bytes. Off by default. |
| Search hidden files | Default for the Advanced Options ▸ Content options "Search hidden files" toggle. On by default — items with the Windows Hidden attribute are included. System files are always skipped by the file walker. |
| Skip admin-protected paths | Excludes system directories that deny access when not elevated. |
| Admin-protected path segments | Custom path segments to skip (semicolon-separated). |
| Skip extensions | Extensions skipped before contents are read. Use semicolon-separated names without dots. |
| Binary extensions | Extensions that remain classified as binary/build artifacts, and populate the Binary ext dropdown when binary search is enabled. |
| Reset binary extensions | Restores the default binary extension list. |
| Archive extensions | Extensions treated as ZIP-like containers when archive search is on. Detection still checks file-header magic bytes. |
| Max archive nesting depth | How deep to recurse into nested archives. 0 = default 5. |
| Max archive entry size (MB) | Largest individual entry to extract from an archive. 0 = default 64 MB. |

### Performance Tab

| Setting | What It Controls |
| --- | --- |
| File-listing backend | Auto, Everything SDK, `es.exe`, or .NET enumeration. |
| Content-search parallelism | Concurrent file scan workers: Safe cap, 1 thread, Half cores, 2x cores, or All cores. |
| Limit parallelism on HDD | When the search target is on a rotational drive, warn and force 1 thread. |
| SDK channel buffer size | Number of file paths buffered between Everything SDK discovery and search workers. |
| Search result temp-file drive | Drive used for disk-backed result temp files during memory-saving mode. Only writable drives with enough free space are listed. |
| Temp-drive full warning threshold (%) | Active searches are terminated when the search result temp-file drive is more than this full. Default 98%; valid range 1-99. Checked every 30 seconds. |
| System memory pressure limit (%) | System RAM usage threshold for memory-saving mode. 0 = disabled. |
| Process memory hard cap (MB) | Working-set limit before memory-saving activates. |
| Max matches per file | Cap on stored matches per file (0 = unlimited). |
| Content-search file size ceiling (MB) | Max individual file size for content search when no explicit max-size filter is set. 0 = no ceiling. |
| MMF concurrency limit | Max concurrent memory-mapped file views. 0 = default 16. |
| Native scanner concurrency limit | Max concurrent Rust native scanner operations. 0 = default `min(64, CPU cores x 2)`. |

### Display Tab

| Setting | What It Controls |
| --- | --- |
| Theme | Auto follows Windows app theme; Dark and Light pin Yagu to that theme. |
| Line truncation length | Result-list line cap for UI responsiveness with very long lines. 0 = disabled. |
| Results list match text font family | Typeface used by match lines in the left results pane. |
| Results list match text font size | Base size used by match lines in the left results pane. |
| Highlighted match text | Color of the matched substring inside each result-list match line. |
| Preview layout | Default layout: Concatenated or Multi-highlight. |
| Word wrap | Default word-wrap state in preview. |
| Preview text font family | Typeface used by preview pane line text and line-number gutters. |
| Preview text font size | Base size used by preview pane line text and line-number gutters. |
| Selected preview content background | Background for the active preview section body. |
| Unselected preview content background | Background for inactive preview section bodies. |
| Preview gutter text | Color of preview line numbers and separator pipes. |
| Matched preview gutter text | Color of preview gutter line numbers for matched lines. |
| Match highlight text | Color of highlighted match text in the preview pane. |
| Active match overlay | Color of the overlay border/underline on the current navigated match. |
| Matched line text | Color of non-highlighted text on matched lines. |
| Auto-load matches on scroll | Number of matches to auto-load when scrolling (default: 50). |
| Max matches per section | Matches shown per file section before an overflow "show more" button (default: 500). |
| Preview section page size | Initial file sections loaded per page, more loaded on scroll (default: 50). |
| Full-file preview limit (MB) | Largest file size for full-file preview mode (default: 1024 MB). |
| Built-in editor font family | Typeface used by the built-in full-file editor. |
| Built-in editor font size | Base size used by the built-in full-file editor; zoom scales from this value. |
| Editor gutter text | Color of line numbers in the built-in editor gutter. |

### Editor Tab

| Setting | What It Controls |
| --- | --- |
| Editor command | External editor command. Supports `{file}` and `{line}` placeholders. Examples: `code -g {file}:{line}`, `notepad++ {file} -n{line}`. |
| Backup before save | Create `.yagubak` file before overwriting (on by default). |
| Show saved confirmation after saving | Show a brief confirmation overlay after the built-in editor successfully writes the file. |
| Syntax coloring based on file type | Color code in the built-in editor based on the file's name or extension (on by default). Applies to files opened after the change. |
| Preview editor max size (MB) | Maximum file size the built-in editor loads (default: 32 MB). |
| Preview editor max text length | Character limit for the built-in editor (default: 20 million). |
| Preview editor max line length | Single-line character limit (default: 1 million). |

### Window Tab

| Setting | What It Controls |
| --- | --- |
| Start in compact launcher mode | Launches as a small search bar when enabled, or as a traditional window when disabled. |
| Launcher focus-loss behavior | Minimize to tray, Stay open, or Always on top when the compact launcher loses focus. |
| Close to tray | Closing the window minimizes to tray instead of exiting (on by default). |
| Maximize window on startup | Starts the main window maximized instead of at the default size. |

### Interaction Tab

| Setting | What It Controls |
| --- | --- |
| Checking a file header adds it to the preview pane | Selecting a file-group checkbox immediately previews that file's matches. |
| Checking a match line adds it to the preview pane | Selecting an individual match-line checkbox immediately previews that match. |

### Terminal Emulator Tab

| Setting | What It Controls |
| --- | --- |
| Default working directory | Starting directory for the embedded terminal shell. Leave blank to use the directory Yagu was launched from. |
| Browse | Picks a terminal working directory. |
| Use default | Clears the saved terminal working directory so launch directory is used again. |

### Developer Options Tab

| Setting | What It Controls |
| --- | --- |
| Show memory pressure warning | Display the orange toolbar warning when memory-saving mode activates. Hidden by default. |
| Stats for nerds | Shows files/sec, MB/s, disk throughput sparkline, and utilization percentage in the bottom status bar. |
| Show build number in title bar | Adds the current Yagu version to the main title bar for diagnostics and screenshots. Hidden by default. |
| Show Auto-scroll checkbox | Shows the results-toolbar Auto-scroll checkbox for testing continuously appended result rows. Hidden by default. |
| Reset font contrast reminders | Allows theme/font contrast warnings to appear again after Remind me later or Don't remind me again. |
| Reset .gitignore vs include filter warning | Re-enables the precedence prompt after you chose Don't ask again or set a fixed preference in Search Defaults. |
| Reset first-time introductory tooltips | Allows the file drawer, line-number, and preview-match introductory tooltips to appear again. |
| Re-enable admin privilege warning | Re-enables the non-administrator warning after it was dismissed. Visible only after the warning has been suppressed. |
| File log level | Controls file logging: None, Critical, Warning, Info, or Verbose. Verbose can degrade performance. |
| Console log level | Controls console logging with the same levels as file logging. |
| Log file | Shows the path to the active Yagu log file. |

---

## CLI Command Generation

Click **Generate CLI command** in Advanced Options to turn the current UI state into a reproducible `Yagu.exe --cli` command. The overlay is selectable text and includes three icon buttons:

| Button | Action |
| --- | --- |
| Copy command | Copies the generated command to the clipboard and closes the overlay. |
| Send command to terminal | Opens the embedded terminal if needed, collapses Advanced Options, verifies the shell changed to the running Yagu executable directory, inserts the generated command at the prompt, and leaves it unexecuted for review. |
| Close | Closes the overlay without copying or sending. |

The **Options already saved in settings** toggle defaults to **Omit**. With Omit, Yagu compares the UI state to `%APPDATA%\Yagu\settings.json` and leaves out flags that already match saved defaults. Switch it to **Include** when you want a fully explicit command that does not rely on the current settings file.

Generated commands cover supported CLI equivalents for search behavior, filters, size/date limits, binary/archive handling, result limits, max depth, threading, memory settings, file-listing backend, and admin-protected path handling. Display-only settings, window behavior, and editor appearance are not included because they do not affect CLI search results.

---

## Embedded Terminal

Yagu includes an embedded command shell rendered with xterm.js in a WebView2 panel. Use the terminal chevron to expand or collapse it. The terminal starts on first use and uses the **Terminal Emulator -> Default working directory** setting, or the directory Yagu was launched from when that setting is blank.

The terminal supports normal typing, command history navigation, paste, and Ctrl+C cancellation. Right-click inside the terminal for:

| Menu item | Action |
| --- | --- |
| Copy | Copies the current terminal selection. |
| Paste | Pastes clipboard text at the prompt. |
| Cut | Copies the current selection and clears the terminal selection. |
| Select all | Selects the terminal buffer. |
| Clear | Sends `cls` and clears the visible terminal surface. Typing `cls` also erases the command line before the blank prompt returns. |
| Reset terminal session | Starts a fresh shell session and clears terminal state. |

When using generated CLI commands, **Send command to terminal** first verifies that the embedded shell changed to the running Yagu executable directory, then inserts the command text into the prompt without pressing Enter. This is useful for reviewing, editing, or adding shell redirection before running the command.

---

## Keyboard Shortcuts

| Shortcut | Action |
| --- | --- |
| **Enter** (search box) | Start search. |
| **Escape** (search box) | Cancel running search. |
| **Down arrow** (search box) | Open search history dropdown. |
| **F1** | Open Help window. |
| **F5** | Start search (from anywhere). |
| **Ctrl+F** | Open Find bar in preview (find only). |
| **Ctrl+H** | Open Find & Replace bar in preview. |
| **Ctrl+C** (preview) | Copy selected text without line numbers. |
| **Ctrl+A** (results) | Select all file groups. |
| **Ctrl+Shift+Delete** | Clear all results. |
| **Enter** (preview) | Jump to next match. |
| **Shift+Enter** (preview) | Jump to previous match. |
| **Ctrl+Click** Next/Prev | Bulk match jump (configurable step size). |
| **Alt+C** | Toggle case sensitive. |
| **Alt+R** | Toggle regex. |
| **Alt+E** | Toggle exact match. |
| **Double-click** (preview match) | Open built-in editor at that line. |

---

## Window Modes

The pin button in the title bar cycles through four modes:

| Mode | Behavior |
| --- | --- |
| Minimize to tray | Minimizes to system tray when window loses focus. Click tray icon to restore. |
| Stay open | Normal window behavior. |
| Always on top | Window stays above other windows. |
| Traditional window | Standard title bar and close button (default). |

---

## Drag and Drop

Drag a folder from Windows Explorer onto the Yagu window to set it as the search directory.

---

## Explorer Context Menu

When registered, the **"Search with Yagu"** entry appears in the right-click menu of any folder in Windows Explorer (and on the folder background). Clicking it launches Yagu with that folder already set as the search directory.

### How to use it

- **Right-click a folder** → **Search with Yagu** — opens Yagu targeting that folder.
- **Right-click the background** of an open folder → **Search with Yagu** — opens Yagu targeting the current folder.

If Yagu is already running, the directory is forwarded to the existing window (single-instance mode).

### Registering the context menu

The installer registers it automatically. If you're running a portable (non-installed) copy, register it manually from an elevated PowerShell prompt:

```powershell
.\scripts\register-context-menu.ps1 -ExePath "C:\path\to\Yagu.exe"
```

To remove it:

```powershell
.\scripts\register-context-menu.ps1 -Uninstall
```

No restart is required — the menu appears immediately in new Explorer windows.

---

## System Tray

When tray mode or close-to-tray is active, the system tray icon provides:

- **Left-click** — Restores the window.
- **Right-click menu:**
  - **Open (reset)** — Restores the window and clears the directory.
  - **Open (existing)** — Restores keeping the current directory.
  - **Close** — Exits the application.
- **Tooltip** — Shows search progress when a search is running.

---

## Taskbar Integration

During a search, Yagu shows progress in the Windows taskbar icon (progress bar overlay). This lets you monitor search completion even when the window is minimized.

---

## Single Instance

Yagu enforces single-instance mode. If you launch Yagu when it's already running, the existing window is brought to the foreground. Command-line arguments (`--dir`, `--query`) are forwarded to the running instance.

---

## Skipped Files

During a search, the status bar shows "Skipped: N". Click it to see a categorized breakdown:

- Access denied
- Too large (exceeded max file size)
- Binary file
- Skipped extension
- Gitignore rule
- Admin-protected path
- Archive entry too large
- Archive nesting too deep

---

## Admin Elevation

If Yagu detects access-denied errors, a banner appears:

- **Learn more…** — Explains the limitation.
- **Restart as Admin** — Relaunches with administrator privileges.
- **Don't show again** — Suppresses the banner permanently.

---

## HDD Detection

When Yagu detects the search directory is on a rotational (HDD) drive, it can automatically limit parallelism to avoid disk thrashing. A dialog informs you when this occurs. Control this with the "Limit parallelism on HDD" setting.

---

## Throughput Sparkline

When **Stats for nerds** is enabled in Settings -> Developer Options, a real-time sparkline graph appears in the status area showing I/O throughput over time. It brightens during active scanning and dims during idle periods. This helps identify bottlenecks (e.g., if throughput drops to zero, the disk may be saturated or a large file is being processed).

---

## Export and Reports

### Export Report Dialog (GUI)

Access via the Preview toolbar's **Export Report** button, or right-click a file section header → **Export**. The dialog offers:

**Format:**

| Format | Output |
| --- | --- |
| HTML report | Styled HTML with matches highlighted, line numbers, and context. |
| JSON | Structured JSON with files, matches, optional context and metadata. |
| CSV | One row per match, with optional multi-line context embedding. |

**Options:**

| Option | Effect |
| --- | --- |
| Include file sizes | Adds file size to each file entry (JSON/CSV). |
| Include file modified dates | Adds last-modified timestamp (JSON/CSV). |
| Include context lines | Exports N lines before/after each match. Adjustable count (0–50). |
| Include `<match></match>` markers | Wraps matched text in markers (JSON/CSV only). |

**CSV-specific options** (visible when CSV + context is selected):

| Option | Effect |
| --- | --- |
| Embed context with RFC 4180 newlines | Context lines embedded as quoted multi-line fields using RFC 4180 standard. Maximum compatibility with Excel and database import tools. |
| Separate lines with pipe ( \| ) | Context lines joined with pipe characters instead of embedded newlines. Better for tools that don't handle multi-line CSV fields. |

### Other Export Actions

| Feature | How to Access | Output |
| --- | --- | --- |
| Copy selected file paths | Right-click file group → Copy Selected File Paths | Clipboard: one path per line |
| Copy files with content | Right-click → Copy Selected Files With Content | Clipboard: paths + matched lines |
| Save file paths to file | Right-click → Save Selected File Paths… | Text file |
| Save files with content | Right-click → Save Selected Files With Content… | Text file |
---

## Performance Overview

Yagu is designed around a streaming search pipeline:

1. **File discovery** finds candidate paths (Everything SDK or .NET enumeration).
2. **Filters** remove files that don't need to be opened (extension, size, date, binary, gitignore, admin-protected).
3. **Content workers** scan files concurrently using the native Rust scanner or managed C# fallback.
4. **Results stream** to the UI in batches for responsive display.
5. **Memory-pressure mode** pages result payloads to disk when memory thresholds are exceeded.

### Fastest Configuration

- voidtools Everything running → Everything SDK backend for near-instant file discovery.
- Native Rust scanner (`yagu_core.dll`) for content search.
- Release build of Yagu.
- Literal (non-regex) query.
- Tight include/exclude filters.
- Search binary: off.
- Practical max file size limit.

### What Makes Searches Slow

Broad scans that open/inspect huge numbers of files. On large drives, discovery can produce millions of candidates. Avoiding unnecessary file opens (via filters, extensions, and size limits) is the biggest win.

---

## Files Per Second

After a search completes or is canceled, the status bar shows a rate like `12,345.6 files/sec`. Use it to compare settings on the same machine and dataset:

1. Run the same query against the same directory.
2. Change one setting at a time.
3. Compare the final files/sec value.
4. Also compare match count, skipped count, and whether the search was truncated.

Do not compare rates across unrelated directories — a tree of tiny source files behaves differently from large logs or archives.

---

## Performance Tuning Recipes

### Fast Code Search

- Search mode: Content only (or File name, then content).
- Literal query (not regex).
- Include: `cs;ts;js;py;rs;go;java;cpp;h`.
- Exclude: `bin;obj;node_modules;.git;target;dist;__pycache__`.
- Search binary: off.
- Parallelism: All cores (try 2× cores on fast NVMe SSDs).

### Large Log Search

- Include: `log;txt;json;csv`.
- Raise max file size if your logs exceed the default.
- Set max results to a bounded number if you only need examples.
- Regex only for patterns literal search can't express.

### Whole Drive / Very Large Tree

- Install and run voidtools Everything.
- File-listing backend: Auto or Everything SDK only.
- Use include filters aggressively.
- Skip extensions for archives, databases, media, build outputs, dumps.
- Set a practical max file size.
- Watch skipped count and click for breakdown.

### HDD / Network Share

- Parallelism: 1 thread or Half cores.
- Strict include/exclude filters.
- Avoid broad regex.
- Expect lower files/sec (storage latency dominates).

---

## File Discovery Backends

| Backend | Description | When To Use |
| --- | --- | --- |
| Auto | Tries Everything SDK → `es.exe` → .NET enumeration. | Best default for all users. |
| Everything SDK only | In-process Everything API. Fastest discovery. | When Everything is installed and running. |
| `es.exe` only | voidtools command-line client. | When SDK DLL is unavailable. |
| .NET enumeration only | Built-in recursive directory scan. | No Everything dependency; slower on large trees. |

If Everything is not available, Yagu falls back automatically and shows the reason in the status area.

---

## Native Scanner and Managed Fallback

Yagu has two content search engines:

| Engine | Speed | Requirements |
| --- | --- | --- |
| Native Rust (`yagu_core.dll`) | Fastest | DLL present, correct architecture, ABI check passes. |
| Managed C# | Slower but always available | No additional requirements. |

If the native DLL is missing or incompatible, Yagu logs the reason and uses the managed scanner transparently.

---

## Memory Behavior

| Safeguard | Purpose |
| --- | --- |
| Bounded channels | Back-pressure between discovery, scanning, and UI. |
| Max results | Stops runaway result streams. |
| Max file size | Prevents accidental reads of enormous files. |
| Skip binary / extensions | Reduces unnecessary reads. |
| Memory-pressure mode | Pages result payloads to the configured search result temp-file drive. |
| Temp-drive low-space monitor | Checks the search result temp-file drive every 30 seconds during search and terminates the search if that drive is more than the configured Performance threshold full. Default 98%. |
| Process memory cap | Hard limit on working set before eviction kicks in. |
| System memory pressure | Activates when system-wide RAM usage exceeds threshold. |

If memory-saving mode appears often, reduce result volume with narrower queries, fewer context lines, or stricter filters. If a search is terminated due to low disk space, free space on the configured temp-file drive, choose a different drive in Settings -> Performance -> Search result temp-file drive, or adjust Settings -> Performance -> Temp-drive full warning threshold (%) before searching again.

---

## Logs and Diagnostics

| Data | Location |
| --- | --- |
| Settings | `%APPDATA%\Yagu\settings.json` |
| Current log | `%APPDATA%\Yagu\yagu.log` |
| Rotated log | `%APPDATA%\Yagu\yagu.log.old` |
| Crash log | `yagu-crash.log` (next to the executable) |
| Memory-pressure temp files | Configured temp-file drive under `Temp\Yagu\yagu-results-*.tmp` |
| Editor backups | `{filename}.yagubak` (same directory as original) |

Log levels: None → Critical → Warning → Info → Verbose. Use Info for normal troubleshooting. Verbose adds overhead during large searches.

---

## Troubleshooting

### No results appear

- Confirm the directory exists and is readable.
- Check the search mode (Content only vs. File names only, etc.).
- Clear all filters: Include, Exclude, Filter files textbox, date range.
- Turn off Regex if the query should be literal text.
- Check the status area for error messages (invalid regex, access denied, etc.).
- Ensure the query isn't too short for Exact match mode.

### Search is slower than expected

- Install and run voidtools Everything for fast discovery.
- File-listing backend: Auto.
- Use include filters and skip extensions to reduce file opens.
- Search binary: off.
- Avoid broad regex when a literal query works.
- Log verbosity: Warning or Info (not Verbose).
- Compare files/sec after changing one setting at a time.

### Access denied or missing files

- Some directories require admin rights.
- Click "Restart as Admin" in the banner.
- Cloud-synced, offline, locked, or protected files may still be skipped.
- Click the skipped count for a categorized breakdown.

### Search gets truncated

Max results reached. Set a higher value or use 0 (unlimited). Memory-pressure protections still apply.

### Memory-saving mode appears

High process or system memory pressure detected. Results paged to disk. Narrow the query, reduce context lines, or lower max results for better responsiveness.

### Everything is not used

- Verify voidtools Everything is installed and running.
- Backend: Auto or Everything SDK only.
- Check if `es.exe` is on PATH as a fallback.
- Status area shows the reason when Everything is unavailable.

### Preview shows "Load More" button

The file exceeds the preview section page size. Click "Load More" to render additional sections, or click "Expand All" in the toolbar to render everything at once.

### Editor won't open a large file

The file exceeds the "Preview editor max size" setting (default 32 MB). Increase it in Settings → Editor, or use the external editor command instead.

---

## Command-Line Interface (CLI Mode)

Yagu includes a full CLI mode for scripting and pipeline integration:

```
Yagu.exe --cli --directory <path> PATTERN [OPTIONS]
```

### Required

| Argument | Description |
| --- | --- |
| `--directory <path>` | Directory to search recursively. |
| `PATTERN` (positional) or `--pattern <pat>` | Search pattern (literal by default). |

### Matching Options

| Flag | Description |
| --- | --- |
| `-e`, `--regex` | Treat pattern as regex. |
| `--no-regex` | Literal string (default). |
| `-s`, `--case-sensitive` | Case-sensitive match. |
| `-i`, `--ignore-case` | Case-insensitive (default). |
| `-C`, `--context <n>` | Context lines around matches (default: 3). |
| `--search-mode <mode>` | `both`, `content`, `filenames`, `filename-then-content`. |
| `--exact-match` | Match whole words only (default). |
| `--no-exact-match` | Allow substring matches. |

### Semantic Search (local AI)

Describe the search in plain language and let a local on-device model fill in the
flags. The query never leaves the machine; the model is downloaded once via Microsoft
Foundry Local and auto-selected for your hardware (prefers the less-quantized GPU build
for accuracy, falling back to NPU then CPU).

| Flag | Description |
| --- | --- |
| `-SP`, `--semantic-pattern <text>` | Natural-language request translated into the search flags (directory, globs, dates, sizes, search mode) and then executed. Replaces the positional `PATTERN`; `--directory` becomes optional (defaults to the current directory). |
| `--semantic-model <alias>` | Force a specific Foundry Local model, by family alias (e.g. `phi-4-mini`) or by exact variant id (e.g. `Phi-4-mini-instruct-cuda-gpu:5`). Default: auto-pick the best small model for this machine's hardware, preferring the less-quantized GPU build for accuracy. Skips the first-run model-download prompt. |
| `--accept-model-download` | Auto-download the recommended model without prompting — for scripts and non-interactive consoles. Without it, a redirected console falls back to Traditional search instead of downloading. |
| `--explain` | With `--semantic-pattern`, print the interpreted search parameters and exit **without** searching (a dry-run). Also reports the selected model and the model's raw JSON output (to stderr) to help diagnose interpretation. |

**First-run model prompt.** The first time you run a semantic query (and no model has been
downloaded yet), Yagu lists the local models suited to your hardware — the recommended pick
first, with smaller/lower-ranked options flagged `(!) may give less accurate results` and
already-downloaded models tagged. Press **Enter** to download the recommended model, type a
**number** to choose another, or **n** to decline. Declining (or a non-interactive console
without `--accept-model-download`) falls back to a literal **Traditional** search of your text.
Your choice is saved, so later runs skip the prompt. Pass `--semantic-model <alias>` to choose
up front and bypass the prompt entirely.

Explicit flags always win over the model's choices, so you can override any part of the
interpretation (e.g. add `--directory` or `--search-mode`). Progress, the model prompt, and the
interpreted plan are written to stderr so stdout stays clean for piping.

```
Yagu.exe --cli --semantic-pattern "find png files on the C drive modified in the past year, ignore mov files"
Yagu.exe --cli --semantic-pattern "large pdf reports created since January" --explain
Yagu.exe --cli --semantic-pattern "config files under the repo" --semantic-model "qwen2.5-1.5b-instruct-generic-cpu"
Yagu.exe --cli --semantic-pattern "log files changed this week" --accept-model-download
Yagu.exe --cli --semantic-pattern "find all files on C:\ with invoice2024 in the name, sort by file name and group by directory"
```

Semantic requests can also set **sorting** and **grouping** (e.g. *"sort by file name"*, *"group by directory"*). As with traditional `--sort`/`--group`, the results are collected and rendered after the scan completes rather than streamed. See [Sort (CLI)](#sort-cli) and [Group (CLI)](#group-cli) for the underlying flags.

### File Filtering

| Flag | Description |
| --- | --- |
| `-g`, `--glob <glob>` | Include files matching GLOB (repeatable). |
| `--exclude-glob <glob>` | Exclude files matching GLOB (repeatable). |
| `--include-regex` / `--include-glob` | Interpret include patterns as regex or glob. |
| `--exclude-regex` / `--exclude-glob-mode` | Interpret exclude patterns as regex or glob. |
| `--min-filesize <size>` | Skip files smaller than SIZE (e.g. `1M`, `10K`). |
| `--max-filesize <size>` | Skip files larger than SIZE. |
| `--binary` / `--no-binary` | Include or skip binary files. |
| `--skip-extensions <ext>` | Semicolon-separated extensions to skip. |
| `--created-after/before <date>` | Filter by creation date (ISO 8601). |
| `--modified-after/before <date>` | Filter by modification date. |

### Gitignore

| Flag | Description |
| --- | --- |
| `--obey-gitignore` | Respect `.gitignore` exclusions. |
| `--no-obey-gitignore` | Ignore `.gitignore` files (default). |
| `--gitignore-precedence` | Gitignore wins over include filters. |

### Performance

| Flag | Description |
| --- | --- |
| `--threads <n>` | Worker threads (0 = service-selected safe cap). |
| `--memory-limit <MB>` | Process memory cap. |
| `--memory-pressure <n>` | System memory threshold 0–100. |
| `--file-lister-backend <n>` | 0=Auto, 1=SDK, 2=es.exe, 3=Managed. |
| `--max-matches-per-file <n>` | Cap matches per file (0 = unlimited). |
| `--max-depth <n>` | Max recursion depth (0 = unlimited). |

### Archive Search

| Flag | Description |
| --- | --- |
| `--search-archives` | Search inside ZIP-like archives. |
| `--archive-extensions <ext>` | Semicolon-separated archive extensions. |

### Content Options

| Flag | Description |
| --- | --- |
| `--hidden` (aliases `--search-hidden`) | Include files/folders carrying the Windows Hidden attribute (default; falls back to the **Search hidden files** setting). |
| `--no-hidden` (aliases `--no-search-hidden`) | Exclude hidden files/folders. System files are always skipped by the file walker regardless of this flag. |

### Output

| Flag | Description |
| --- | --- |
| `--max-results <n>` | Stop after N matches (default: 50000). |
| `--line-truncation <n>` | Truncate lines to N characters (0 = no limit). |

### Export (CLI)

| Flag | Description |
| --- | --- |
| `--export <path>` | Export results to a file (triggers export mode). |
| `--export-format <fmt>` | Export format: `html`, `json`, `csv` (default: inferred from file extension). |
| `--export-context <n>` | Context lines in exported report (default: 3, 0 = none). |
| `--export-file-sizes` | Include file sizes in export. |
| `--export-modified-dates` | Include file modified dates in export. |
| `--export-no-markers` | Omit `<match></match>` markers in JSON/CSV exports. |
| `--export-csv-embed-context` | Embed context as multi-line CSV fields (RFC 4180). |
| `--export-csv-pipe-separator` | Use pipe ( \| ) to separate context lines instead of embedded newlines. Implies embed context. |

### Replace (CLI)

Search and replace text across all matched files directly from the command line. Mirrors the GUI's **Ctrl+H → Replace in All Files** feature.

| Flag | Description |
| --- | --- |
| `-r`, `--replace <text>` | Replace all occurrences of the search pattern with `<text>` in matched files. |
| `--replace-dry-run`, `--dry-run` | Show what would be replaced without modifying any files. |
| `--replace-no-backup` | Do not create `.yagubak` backup files before replacing. |

By default, each file is backed up to `{filename}.yagubak` before writing (numbered backups if one already exists). The replacement respects the `--case-sensitive` / `--ignore-case` flag for matching.

**Example — dry run:**

```
Yagu.exe --cli --directory src "oldFunction" --replace "newFunction" --dry-run
```

**Example — replace with backup:**

```
Yagu.exe --cli --directory src "oldFunction" --replace "newFunction"
```

**Example — replace without backup:**

```
Yagu.exe --cli --directory src "oldFunction" --replace "newFunction" --replace-no-backup
```

> **Warning:** `--replace` writes to disk. Always use `--dry-run` first to preview changes. Use include/exclude filters to limit the scope.

### Sort (CLI)

Sort CLI output by file attributes. Useful for reviewing results in a specific order or combining with `--export`.

| Flag | Description |
| --- | --- |
| `--sort <key>` | Sort results by: `matches`, `date`, `size`, `name`, `directory`, `path`. Default: unsorted (arrival order). |
| `--sort-desc` | Sort in descending order. |
| `--sort-asc` | Sort in ascending order (default). |

When `--sort` is specified, results are collected, sorted by file group, and then output in ripgrep format. This buffers all results before printing (unlike the default streaming mode).

**Example — most matches first:**

```
Yagu.exe --cli --directory src "TODO" --sort matches --sort-desc
```

**Example — newest files first:**

```
Yagu.exe --cli --directory logs "error" --sort date --sort-desc
```

### Group (CLI)

Group CLI output into buckets by a file attribute. Like `--sort`, grouping collects the whole result set and renders it **after** the scan completes — grouped output is never streamed live. Each group is printed under a header showing the bucket label and its file/match counts. Combine with `--sort` to order the files **within** each group.

| Flag | Description |
| --- | --- |
| `--group <key>` | Group results by: `directory`, `extension`, `size`, `modified`, `created`, `date`, `none`. Default: ungrouped. |
| `--group-desc` | Reverse the natural group order (Z–A / oldest / largest first). |
| `--group-asc` | Natural group order: A–Z / recent / smallest first (default). |

Natural group ordering depends on the key: directory/extension buckets sort A–Z, size buckets smallest-first, and date buckets most-recent-first. `--group-desc` reverses whichever orientation applies.

**Example — group matches by folder:**

```
Yagu.exe --cli --directory src "TODO" --group directory
```

**Example — group by file type, biggest files first within each group:**

```
Yagu.exe --cli --directory src "TODO" --group extension --sort size --sort-desc
```

**Example — group by modified date, oldest groups first:**

```
Yagu.exe --cli --directory logs "ERROR" --group modified --group-desc
```

### Help

| Flag | Description |
| --- | --- |
| `--help`, `-h`, `-?` | Print usage information and exit. |

### Exit Codes

| Code | Meaning |
| --- | --- |
| 0 | One or more matches found. |
| 1 | No matches found. |
| 2 | Usage error. |
| 130 | Cancelled (Ctrl+C). |

### Local Settings File

If `.yagu.json` exists in the current working directory, it is used as the base configuration. If not, Yagu checks the running process launch directory next, then falls back to global AppData settings. CLI flags always override file settings.

---

## GUI Command-Line Arguments

When launching Yagu in GUI mode (without `--cli`):

| Argument | Description |
| --- | --- |
| `--dir <path>` | Set initial search directory. |
| `--query <text>` | Set initial query (auto-starts search if `--dir` is also provided). |
| `--window-mode <mode>` | Window behavior: `0`/`minimize`/`tray`, `1`/`stay-open`, `2`/`always-on-top`, `3`/`traditional`/`desktop`. |

---

## Practical Defaults

For most users:

| Setting | Recommended |
| --- | --- |
| File-listing backend | Auto |
| Parallelism | Auto |
| Search binary | Off |
| Skip extensions | Keep the default broad list |
| Max file size | Keep a practical limit |
| Max results | 0 (unlimited) |
| Log verbosity | Warning or Info |
| Archive search | Off unless needed |
| Search hidden files | On (matches Everything's default; turn off to skip dotfiles/hidden trees) |
| Close to tray | On (keeps Yagu available) |

Then tune filters and query until the result set is manageable.