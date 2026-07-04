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
   - Click the **★ pin** to the left of Browse to keep the current folder as the startup default so it is pre-filled the next time Yagu opens. By default the box starts empty (which searches all drives); click the pin again to unpin and clear the saved folder. Pinning snapshots the folder when you click it — changing the box afterward does not change the pin.
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
| Directory bar | The folder to search, with auto-complete, a pin (★) to keep the current folder as the startup default, Browse, and recent history. |
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

> **Tip — open a specific file directly.** If you paste a complete file path (and nothing else) into the Traditional search box and press Enter, Yagu skips the normal scan and shows just that one file. This works no matter what's in the Directory box. Surrounding quotes are allowed (e.g. `"C:\path with spaces\app.log"`).

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

**More things you can ask.** Semantic mode understands file types, content vs. file-name matches, dates, sizes, exclusions, structured patterns, and sorting/grouping. A few examples:

- **By file type:** *"all PDF files in D:\docs larger than 5 MB"* · *"log files changed this week"* · *"images created in the last 30 days"*
- **Find text inside files:** *"search for TODO in all python files in the current folder"* · *"all files on C:\ that have \"1111-1111-1111\" in them"* (the phrase *in them* / *inside* means file **contents**, not names)
- **Office documents** — because `.docx`/`.xlsx`/`.pptx` files are ZIP containers, Yagu turns on **Search archives** automatically: *"word documents with \"Andrew\" in them"* (→ `*.docx`, `*.doc`) · *"excel spreadsheets containing revenue"* (→ `*.xlsx`, `*.xls`)
- **By file name:** *"files named invoice2024 on C:\"* · *"anything with backup in the name"*
- **Structured patterns (Yagu writes the regex for you):** *"find email addresses in C:\dump"* · *"IP addresses in the logs folder"* · *"GUIDs in the source folder"* · *"lines that start with ERROR"* · *"files where the word andrew appears at least twice"*
- **Exclusions:** *"png files on C:, ignore mov files and anything named thumbnail"*
- **Sort & group:** *"search src for TODO, biggest files first, grouped by file type"*

For relative dates just say the phrase (*"in the past year"*, *"last 7 days"*, *"since January"*, *"yesterday"*) and Yagu resolves it against today's date; sizes accept plain units (*"5 MB"*, *"larger than 1 GB"*). Keep each request to a single search — one directory, one thing to find.

Notes:

- **Runs entirely on your machine.** Your query is never sent off the device. The model is downloaded once via Microsoft **Foundry Local** and cached for reuse.
- **Hardware‑aware.** Yagu auto‑picks the best small instruct model your machine can run. Within that model it prefers the less‑quantized **GPU** build for accuracy, falling back to **NPU** then **CPU**, so it still works even without a dedicated GPU/NPU.
- **Smart default mode.** On machines with a supported GPU/NPU, the search bar starts in **Semantic** mode automatically; machines with no supported accelerator start in **Traditional**. You can override the default for accelerated machines under **Settings → Search Defaults → Default to Traditional search mode** (that option is greyed out when no supported GPU/NPU is present). Either way you can switch modes any time from the Search‑button chevron.
- **First run asks before downloading.** The first time you switch to Semantic, a borderless dialog lists the available models with their download sizes. The model best suited to your hardware is pre‑selected and marked **Recommended**; smaller or lower‑ranked models show a ⚠ warning that they *may be less accurate*. Choose **Use this model** to download (a progress bar shows the percentage), or **Not now** to cancel — declining switches you back to **Traditional**. Already‑cached models are tagged **Downloaded** and start instantly. After the first download the prompt is not shown again.
- **Status while translating.** A progress line appears just below the search card (in the status area above the results) while the runtime/model loads and the query is being translated, so it never pushes the search box down.
- **Transparent.** After translating, Yagu fills in the Advanced Options so you can see and tweak exactly what it set before or after searching.
- **Switching back to Traditional** (via the same Search‑button chevron menu) restores the literal/regex query behavior; the inline Case/Regex/Exact toggles apply in Traditional mode only.
- **Did you mean AI search?** While in **Traditional** mode, if you type something that reads like a natural‑language request — e.g. `files on C: containing the word test` — Yagu offers to run it as a Semantic search instead. Choose **Switch to AI search** to interpret it that way (this turns AI search on for you if you'd disabled it), or **Keep Traditional** to match your text literally. Tick **Don't remind me again** to stop the prompt for good. It only appears once a Semantic model has been downloaded.

Configure availability and the optional preferred model under **Settings → Search Defaults**. The same capability is available from the CLI via `--semantic-pattern` (see [Command-Line Interface](#command-line-interface-cli-mode)).

For a comprehensive, categorized list of example queries and how the options combine, see [Semantic Search Query Examples](#semantic-search-query-examples) (all 300 scenarios from the test suite).

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
| Search image text (OCR) | When enabled, image files (PNG, JPG, BMP, GIF, TIFF, WEBP) are run through OCR and the recognized text is searched like any other file's contents. Off by default because OCR is slower than reading text files. OCR runs on a background queue that does not block or slow the normal file scan; matches appear in the results panel as each image is processed. The OCR engine, recognition quality, and this toggle's default come from the **Settings ▸ OCR** tab (see below). |

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

A **Shell** dropdown in the terminal's toolbar lets you switch between **Command Prompt (cmd.exe)** and **PowerShell**. The choice is saved and reused the next time the terminal opens. Switching shells starts a fresh session in the selected shell — pick PowerShell if you want PowerShell cmdlets and aliases such as `cat`, `ls`, and `Get-ChildItem`.

**PowerShell is the default shell.** You can change the default under **Settings → Terminal Emulator → Default Shell**; the terminal-toolbar dropdown switches a running session live.

The PowerShell session is fully interactive: running a cmdlet that needs a mandatory parameter (for example, a bare `Get-Item`) prompts you with **Supply values for the following parameters** instead of hanging, and `Read-Host` prompts work too. Variables and the current directory persist across commands, and errors (such as a missing file or a failed download) appear as readable text.

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

## Sessions

A **session** is a snapshot of a completed search — the query, the search location, the options, and the full result set — saved to a `.yagu-session` file. Reopening a session restores those results **instantly, without rerunning the scan**. That makes sessions ideal for long searches over large folder trees, for handing an investigation to a colleague, or for picking up exactly where you left off days later.

### Saving a session

After a search finishes, open the results **⋯ (more)** menu at the top of the results pane and choose **Save session**. Pick a location and name; Yagu writes a `.yagu-session` file containing the query, options, and matched results. From the CLI, add `--save-session <path>` to any search.

### Loading a session

Click the **Load session** button — the folder icon in the search card, beside the Search/Cancel button — to open the **Load session** picker. Yagu uses Everything to find every `.yagu-session` file on your PC and lists them in a sortable table:

| Column | Description |
| --- | --- |
| Name | The session file name. |
| Directory | The folder the session file is stored in. |
| Size | The session file size. |
| Created | When the session was saved — the default sort, newest first. |

Click any column header to sort by it (click again to reverse the order). Select a session with a click, double‑click, or **Enter** to load it, or choose **Browse…** to pick a file manually with the standard Windows dialog. If Everything is not available, Yagu skips the picker and opens the Browse dialog directly.

Loading a session repopulates the results list and preview from the saved data — no files are re‑read and no scan runs, so even a session from a search that originally took minutes reopens at once. From the CLI, use `--load-session <path>` to re‑emit a saved session's results.

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
| Binary extensions | The set of file types treated as binary/build artifacts. When binary search is on, the Advanced Options ▸ Binary ext dropdown lets you pick which of these to include in the search (checked = searched; unchecked types are skipped). |
| Reset binary extensions | Restores the default binary extension list. |
| Archive extensions | Extensions treated as ZIP-like containers when archive search is on. Detection still checks file-header magic bytes. |
| Max archive nesting depth | How deep to recurse into nested archives. 0 = default 5. |
| Max archive entry size (MB) | Largest individual entry to extract from an archive. 0 = default 64 MB. |

### OCR Tab

Controls image text recognition (OCR). When OCR is on, image files (PNG, JPG, BMP, GIF, TIFF, WEBP) are recognized on a background queue and their text is searched like any other file's contents.

> **Two installer editions.** Yagu ships in two flavors: a **lite** installer that downloads the OCR engine runtime and language models the first time you actually use image-text search, and an **Offline** installer (the `x64-offline` download) that bundles those components so OCR works fully offline with no download. The Offline edition ships the **Tesseract** engine (and its English data) plus the PaddleOCR runtime and models, and it **defaults to Tesseract** so image-text search works out of the box with nothing to fetch. With the lite edition, Yagu **warns you before any external download** and only proceeds once you approve; consent is then remembered. The Offline edition also bundles the voidtools **Everything** installer, so Yagu can install Everything for fast file discovery with no download (the lite editions fetch it on demand). Installing Everything **always** requires your explicit consent — Yagu never installs it silently.

| Setting | What It Controls |
| --- | --- |
| Search image text (OCR) | Default for the Advanced Options ▸ Filters "Search image text (OCR)" toggle. Off by default. When on, image files are OCR'd on a background queue and the recognized text is searched. |
| OCR engine | PaddleSharp or Tesseract. PaddleSharp is generally more accurate and runs on the CPU (MKL-accelerated) — no GPU or NPU is required or used; Tesseract is a lighter alternative with a fixed pipeline that runs entirely from bundled data. The default is PaddleSharp on most builds, **Tesseract on the x86 build and on the Offline (`x64-offline`) edition**. With the lite installer, the selected engine's runtime and models download on first use (after you approve the warning); the Offline installer ships both engines' runtimes and data so no download is needed. |
| Quality preset | Quick presets that set the recognition model and detection resolution together: **Fast** (English v3, 640 px), **Balanced** (Chinese+English v5, 960 px), **Accurate** (Chinese+English v5, 1536 px). Switches to **Custom** when the model/resolution below don't match a preset. Applies to PaddleSharp. |
| Recognition model | PaddleSharp recognition model: English v3 (fastest), English v4, Chinese+English v4, or Chinese+English v5 (default, recommended, most accurate). Models download on first use. Ignored by Tesseract. |
| Detection resolution | Longest image side (in pixels) the image is downscaled to before detection: 640, 960, 1280, 1536, 2048, or Unlimited (native resolution). Larger finds smaller text but is slower. Ignored by Tesseract. |

### Performance Tab

| Setting | What It Controls |
| --- | --- |
| File-listing backend | Auto, Everything SDK, `es.exe`, or .NET enumeration. |
| Content-search parallelism | Concurrent file scan workers: Safe cap, 1 thread, Half cores, 2x cores, or All cores. |
| Limit parallelism on HDD | When the search target is on a rotational drive, warn and force 1 thread. |
| SDK channel buffer size | Number of file paths buffered between Everything SDK discovery and search workers. |
| Search result temp-file drive | Drive used for disk-backed result temp files during memory-saving mode. Only writable drives with enough free space are listed. |
| Temp-drive full warning threshold (%) | Active searches are terminated when the search result temp-file drive is more than this full. Default 90%; valid range 1-99. Checked every 30 seconds. |
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
| Long-line warning | What to do when opening a file with a very long line in the built-in editor: **Ask every time** (default, shows the warning dialog), **Always open without word wrap**, or **Always open with word wrap**. The dialog's "Don't remind me again" checkbox sets this automatically. |
| Preview editor max size (MB) | Maximum file size the built-in editor loads (default: 32 MB). |
| Preview editor max text length | Character limit for the built-in editor (default: 20 million). |
| Preview editor max line length | Single-line character limit (default: 1 million). |

### Window Tab

| Setting | What It Controls |
| --- | --- |
| Start in compact launcher mode | Launches as a small search bar when enabled, or as a traditional window when disabled. |
| Launcher focus-loss behavior | Minimize to tray, Stay open, or Always on top when the window loses focus. Applies in both the compact launcher and the traditional window. |
| Close to tray | Closing the window minimizes to tray instead of exiting (on by default). |
| Maximize window on startup | Starts the main window maximized instead of at the default size. |
| Traditional window launch position | Where the traditional window appears on screen at launch: Centered (default), or any of the eight edge/corner anchors (Top Left, Top Middle, Top Right, Middle Left, Middle Right, Bottom Left, Bottom Middle, Bottom Right). Ignored when Maximize window on startup is on or while in the compact launcher. |
| Compact launcher launch position | Where the compact launcher (Spotlight-style search bar) appears on screen at launch. Same nine anchors as above, defaulting to Top Middle. |

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
| Log file | Shows the path to the active Yagu log file as a clickable link — click it to open the log in Notepad. |

### Privacy Tab

Controls Yagu's two **optional, off-by-default** diagnostics features. Both are independent — you can enable either, both, or neither. The very first time Yagu starts it shows a one-time consent prompt offering both; if you decline, the prompt never appears again and you can still turn either feature on later from this tab.

> **Your searches never leave your machine.** Yagu never sends file paths, file contents, directory names, search queries, or machine identifiers through the silent telemetry channel — those are scrubbed out before anything is sent (any filesystem path in an error message is redacted to `<path>`). The only data tied to your install is a random GUID generated once, used only to count distinct installs. The bug-report channel can include more (your settings file and a log tail), but **only after you review the exact contents in a dialog and click Submit**.

> **Nothing is sent unless an endpoint is configured.** Telemetry travels to a self-hosted Azure Function proxy, not to any third party. If the build you are running has no endpoint configured, both features are completely inert and Yagu makes no network calls regardless of these toggles. Headless/CLI runs never send anything.

| Setting | What It Controls |
| --- | --- |
| Send anonymized error & performance telemetry | When on, Yagu sends a small batch of anonymized, path-scrubbed error summaries and performance measurements (e.g. startup time) to the configured proxy. No file paths, queries, contents, or personal data. Off until you opt in. |
| Offer to send a bug report on errors | When on, if Yagu hits a critical/unhandled error it opens a **bug-report dialog** that shows you exactly what would be submitted — the error and stack trace, GPU/NPU details, a copy of your `settings.json`, and a tail of your log file — plus an optional comment box. Nothing is sent unless you click **Submit report**. The same error is offered at most once per session. Off until you opt in. |
| Contact email (optional) | An email address attached to bug reports you submit, so the developer can follow up. Leave blank to stay anonymous. Only sent with reports you explicitly submit. |

The **What's Sent & Where** group on this tab summarizes the destination and shows whether a telemetry endpoint is configured for the current build.

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

Use the **Shell** dropdown in the terminal toolbar to choose between **Command Prompt (cmd.exe)** and **PowerShell**. Your selection is persisted and reused on the next launch. Changing the shell restarts the terminal session in the chosen shell — for example, select **PowerShell** to use PowerShell cmdlets and aliases (`cat`, `ls`, `Get-ChildItem`, `Select-String`, and so on). Tab completion offers the built-in commands for whichever shell is active. The PowerShell session is interactive: cmdlets that require a mandatory parameter prompt for it (rather than hanging), `Read-Host` works, and variables and the working directory persist between commands.

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
| Temp-drive low-space monitor | Checks the search result temp-file drive every 30 seconds during search and terminates the search if that drive is more than the configured Performance threshold full. Default 90%. |
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

**Force verbose logging from the very first launch.** The default file log level is Warning, and it is normally changed in Settings. When you need verbose logs of something that happens during Yagu's *first* launch — before the Settings window is reachable — run the installer with the `/VERBOSELOG` switch. This records `Verbose` to `HKCU\Software\Yagu\LogLevelOverride`, which Yagu reads at startup and applies as a minimum file log level on every run:

```
YaguSetup-<version>-<arch>.exe /VERBOSELOG
```

It works with silent installs too (`YaguSetup-<version>-<arch>.exe /VERYSILENT /VERBOSELOG`). The override stays in effect until you reinstall normally (without `/VERBOSELOG`, which clears it) or uninstall Yagu.

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
| `--semantic-batch <file>` | Translate a file of natural-language queries (one per line; blank lines and `#` comments ignored) through a **single loaded model**, printing one delimited `--explain` block per query. The model loads once and is reused for every query, so a whole query set — or a sweep across many models — can be evaluated without paying the cold-load cost per call. Always a dry-run (no search executed). |

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
| `--image-text` (aliases `--search-image-text`, `--ocr`) | OCR image files and search the recognized text. Off by default; falls back to the **Search image text (OCR)** setting. Images are processed on a background queue so the normal file scan is not blocked. |
| `--no-image-text` (aliases `--no-search-image-text`, `--no-ocr`) | Do not OCR images (default). |
| `--ocr-engine <name>` | OCR engine for `--image-text`: `paddle` (PaddleSharp, default) or `tesseract`. |
| `--ocr-model <name>` | PaddleSharp recognition model for `--image-text`: `EnglishV3`, `EnglishV4`, `ChineseV4`, or `ChineseV5` (default). Falls back to the **OCR ▸ Recognition model** setting. Ignored by the `tesseract` engine. |
| `--ocr-max-side <px>` | PaddleSharp detection resolution (longest side in pixels) for `--image-text`: default 960; `0` = unlimited (native resolution). Falls back to the **OCR ▸ Detection resolution** setting. Ignored by the `tesseract` engine. |
| `--allow-ocr-download` | Consent, in advance, to the one-time download of the OCR engine runtime and/or language models that `--image-text` needs on first use (the lite installer ships without them; the OCR-bundled installer ships them so nothing downloads). Without this flag, a non-interactive run that needs the download is refused and an interactive run prompts before downloading. Consent is remembered for future runs. |

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
---

## Semantic Search Query Examples

Yagu's query engine is verified by a catalog of **300 scenarios** in the test project
(`SemanticSearchQueryCatalog`), each pairing a query with specific search options and asserting an
exact result. They double as a comprehensive reference of *what* you can search for and *how* the
options combine — literal / substring / whole‑word / regex matching, case sensitivity, search modes,
include and exclude filters, size and date ranges, result and depth limits, binary / hidden / archive
handling, and multi‑term queries. Every row below is a real, passing test case; the **Scenario** column
is its name in the test catalog.

### Literal, substring, whole-word & case sensitivity (59)

| # | Query | Search settings | Scenario |
| ---: | --- | --- | --- |
| 1 | `hello` | substring | `literal-substring-finds-two-files` |
| 2 | `brown` | substring | `literal-substring-single-file` |
| 3 | `token` | substring | `literal-substring-all-three` |
| 4 | `ell` | substring | `literal-substring-partial-word` |
| 5 | `config` | substring | `literal-substring-prefix` |
| 6 | `ing` | substring | `literal-substring-suffix` |
| 7 | `gamma` | substring | `literal-substring-none-present` |
| 8 | `NEEDLE` | substring | `literal-substring-embedded` |
| 9 | `hello` | substring | `literal-case-insensitive-three` |
| 10 | `hello` | substring, case-sensitive | `literal-case-sensitive-lower` |
| 11 | `HELLO` | substring, case-sensitive | `literal-case-sensitive-upper` |
| 12 | `Error` | substring, case-sensitive | `literal-case-sensitive-titlecase` |
| 13 | `MyClassName` | substring, case-sensitive | `literal-case-sensitive-mixed-token` |
| 14 | `myclassname` | substring | `literal-case-insensitive-mixed-token` |
| 15 | `async` | whole word | `wholeword-excludes-partial` |
| 16 | `cat` | whole word | `wholeword-matches-boundaries` |
| 17 | `cat` | substring | `substring-matches-inside-words` |
| 18 | `value` | whole word | `wholeword-punctuation-adjacent` |
| 19 | `max_count` | whole word | `wholeword-token-with-underscore` |
| 20 | `8080` | whole word | `wholeword-digits-boundary` |
| 21 | `foo bar` | substring | `multiterm-or-two` |
| 22 | `red green blue` | substring | `multiterm-or-three` |
| 23 | `cat dog` | substring | `multiterm-or-overlap-counts` |
| 24 | `Cat Dog` | substring, case-sensitive | `multiterm-or-case-sensitive` |
| 25 | `test 123` | whole word | `phrase-wholeword-exact` |
| 26 | `hello world` | whole word | `phrase-wholeword-not-split` |
| 27 | `+=` | substring | `symbol-plus-equals` |
| 28 | `=>` | substring | `symbol-arrow` |
| 29 | `::` | substring | `symbol-namespace-colons` |
| 30 | `$100` | substring | `symbol-currency` |
| 31 | `#TODO` | substring | `symbol-hashtag` |
| 32 | `needle` | substring | `count-three-matching-lines` |
| 33 | `needle` | substring | `count-one-of-many-lines` |
| 34 | `needle` | substring | `count-two-files-total` |
| 35 | `omega` | substring | `count-zero-when-absent` |
| 36 | `2024` | substring | `numeric-token-substring` |
| 37 | `42` | whole word | `numeric-token-whole-word` |
| 38 | `3.14` | substring | `numeric-decimal` |
| 39 | `café` | substring | `unicode-accented` |
| 40 | `検索` | substring | `unicode-cjk` |
| 41 | `привет` | substring | `unicode-cyrillic` |
| 42 | `value` | substring | `tab-separated-token` |
| 43 | `  term  ` | whole word | `leading-trailing-query-trimmed-wholeword` |
| 44 | `needle` | substring | `nested-dirs-found` |
| 45 | `compile` | substring | `nested-dirs-distinct-tokens` |
| 46 | *(filter only)* | substring | `empty-query-no-results` |
| 47 | `   ` | substring | `whitespace-only-query-no-results` |
| 48 | `MIDDLE` | substring | `matched-text-substring-token` |
| 49 | `wholeword` | whole word | `matched-text-wholeword-token` |
| 50 | `function` | substring | `matched-text-case-preserved` |
| 51 | `log` | substring | `repeated-token-distinct-lines` |
| 52 | `edge` | substring | `token-at-line-edges` |
| 53 | `international nation` | substring | `longer-of-two-overlapping-terms` |
| 54 | `treasure` | substring | `token-only-in-deep-file` |
| 55 | `marker` | substring | `mixed-extensions-content-token` |
| 56 | `file.name` | substring | `literal-dot-is-literal-not-regex` |
| 57 | `a*b` | substring | `literal-star-is-literal-not-regex` |
| 58 | `func()` | substring | `literal-parens-are-literal` |
| 59 | `arr[0]` | substring | `literal-bracket-is-literal` |

### Regular expressions (60)

| # | Query | Search settings | Scenario |
| ---: | --- | --- | --- |
| 60 | `foo\d+` | regex | `regex-digits-quantifier` |
| 61 | `ab*c` | regex | `regex-star-zero-or-more` |
| 62 | `colou?r` | regex | `regex-optional` |
| 63 | `\d{3}` | regex | `regex-exact-repeat` |
| 64 | `\d{2,4}` | regex | `regex-range-repeat` |
| 65 | `[a-z]+` | regex, case-sensitive | `regex-plus-letters-cs` |
| 66 | `^start` | regex | `regex-line-anchor-start` |
| 67 | `END$` | regex | `regex-line-anchor-end` |
| 68 | `^exact$` | regex | `regex-anchored-full-line` |
| 69 | `^\d` | regex | `regex-start-digit` |
| 70 | `\.$` | regex | `regex-line-ends-with-period` |
| 71 | `^\d+$` | regex | `regex-whole-line-digits` |
| 72 | `\d$` | regex | `regex-anchor-end-digit` |
| 73 | `#[0-9a-f]{6}` | regex | `regex-char-class-hex` |
| 74 | `[^0-9]+` | regex | `regex-negated-digit-class` |
| 75 | `\d` | regex | `regex-digit-class` |
| 76 | `\w+` | regex | `regex-word-class` |
| 77 | `\s` | regex | `regex-whitespace-class` |
| 78 | `\W` | regex | `regex-non-word` |
| 79 | `\D` | regex | `regex-non-digit` |
| 80 | `a.c` | regex | `regex-dot-any` |
| 81 | `[A-Z]{2,}` | regex, case-sensitive | `regex-case-sensitive-uppercase-class` |
| 82 | `[aeiou]+` | regex | `regex-class-vowels` |
| 83 | `[a-z0-9]+` | regex, case-sensitive | `regex-class-alnum-cs` |
| 84 | `cat\|dog` | regex | `regex-alternation` |
| 85 | `x\|y\|z` | regex | `regex-alternation-three` |
| 86 | `(ab)+` | regex | `regex-group-quantifier` |
| 87 | `^(foo\|bar)` | regex | `regex-alternation-anchored` |
| 88 | `gr(a\|e)y` | regex | `regex-group-alternation` |
| 89 | `(un)?lock` | regex | `regex-optional-group` |
| 90 | `(cat\|dog)s?` | regex | `regex-nested-group-optional-s` |
| 91 | `(ab\|cd)+` | regex | `regex-alternation-with-quantifier` |
| 92 | `(foo)?bar` | regex | `regex-grouped-optional-prefix` |
| 93 | `\bcat\b` | regex | `regex-word-boundary` |
| 94 | `\bpre` | regex | `regex-word-boundary-prefix` |
| 95 | `foo\.bar` | regex | `regex-escaped-dot` |
| 96 | `\(\)` | regex | `regex-escaped-paren` |
| 97 | `a\+b` | regex | `regex-escaped-plus` |
| 98 | `\[x\]` | regex | `regex-escaped-bracket` |
| 99 | `error` | regex | `regex-case-insensitive-default` |
| 100 | `error` | regex, case-sensitive | `regex-case-sensitive-flag` |
| 101 | `^The` | regex | `regex-anchored-start-word` |
| 102 | `\w+@\w+` | regex | `regex-email-like` |
| 103 | `\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}` | regex | `regex-ip-like` |
| 104 | `\d{3}-\d{4}` | regex | `regex-phone` |
| 105 | `\d{2}:\d{2}` | regex | `regex-time` |
| 106 | `v\d+\.\d+` | regex | `regex-version` |
| 107 | `[0-9a-f]{8}` | regex | `regex-hex8` |
| 108 | `\$\d+` | regex | `regex-currency` |
| 109 | `\d+%` | regex | `regex-percentage` |
| 110 | `#\w+` | regex | `regex-hashtag` |
| 111 | `@\w+` | regex | `regex-mention` |
| 112 | `https?://\w+` | regex | `regex-url-like` |
| 113 | `\w+\d` | regex | `regex-word-then-digit` |
| 114 | `aa+` | regex | `regex-double-letter` |
| 115 | `a+?` | regex | `regex-lazy-quantifier` |
| 116 | `^item` | regex | `regex-multiline-anchor-count` |
| 117 | `\d+` | regex | `regex-count-digit-lines` |
| 118 | `foo.*bar` | regex | `regex-dotstar-between` |
| 119 | `xyz\d{5}` | regex | `regex-no-match` |

### Include / exclude path & type filters (50)

| # | Query | Search settings | Scenario |
| ---: | --- | --- | --- |
| 120 | `needle` | substring, include *.cs | `include-ext-star-cs` |
| 121 | `needle` | substring, include cs | `include-ext-bare-cs` |
| 122 | `needle` | substring, include txt | `include-ext-txt-bare` |
| 123 | `needle` | substring, include *.cs,*.js | `include-ext-comma-two` |
| 124 | `needle` | substring, include *.cs *.js | `include-ext-two-args` |
| 125 | `needle` | substring, include *.CS | `include-ext-uppercase` |
| 126 | `needle` | substring, include json | `include-ext-json` |
| 127 | `needle` | substring, include *.md | `include-ext-no-match` |
| 128 | `needle` | substring, include *.a,*.b,*.c | `include-ext-three` |
| 129 | `needle` | substring, include *.tsx | `include-ext-tsx-not-ts` |
| 130 | `needle` | substring, include md | `include-ext-bare-md` |
| 131 | `needle` | substring, include *.env | `include-ext-env` |
| 132 | `needle` | substring, include *.cs js | `include-ext-and-bare-mixed` |
| 133 | `needle` | substring, include src/**/*.cs | `include-glob-src-cs` |
| 134 | `needle` | substring, include **/*.cs | `include-glob-double-star-cs` |
| 135 | `needle` | substring, include file?.txt | `include-glob-question` |
| 136 | `needle` | substring, include logs/*.txt | `include-glob-folder-file` |
| 137 | `needle` | substring, include app* | `include-glob-prefix-star` |
| 138 | `needle` | substring, include data?.json | `include-glob-data-question-json` |
| 139 | `needle` | substring, include regex \.ts$ | `include-regex-ts` |
| 140 | `needle` | substring, include regex \.(cs\|js)$ | `include-regex-ext-alternation` |
| 141 | `needle` | substring, include regex \d+\.txt$ | `include-regex-digit-name` |
| 142 | `needle` | substring, include regex /src/ | `include-regex-folder` |
| 143 | `needle` | substring, include regex /SRC/ | `include-regex-folder-case-insensitive` |
| 144 | `needle` | substring, include regex /main\.[a-z]+$ | `include-regex-anchored-leaf` |
| 145 | `needle` | substring, exclude log | `exclude-ext-bare-log` |
| 146 | `needle` | substring, exclude *.tmp | `exclude-ext-glob-tmp` |
| 147 | `needle` | substring, exclude *.log,*.tmp | `exclude-ext-comma-two` |
| 148 | `needle` | substring, exclude bak | `exclude-ext-keeps-multiple` |
| 149 | `needle` | substring, exclude *.min.txt | `exclude-ext-min-txt` |
| 150 | `needle` | substring, exclude node_modules | `exclude-segment-node-modules` |
| 151 | `needle` | substring, exclude vendor | `exclude-segment-vendor` |
| 152 | `needle` | substring, exclude coverage | `exclude-segment-coverage` |
| 153 | `needle` | substring, exclude node_modules,coverage | `exclude-two-segments` |
| 154 | `needle` | substring, exclude packages | `exclude-segment-packages` |
| 155 | `needle` | substring, exclude build_out | `exclude-segment-build-out` |
| 156 | `needle` | substring, exclude **/bin/** | `exclude-glob-bin-double-star` |
| 157 | `needle` | substring, exclude regex [\\/]tmp[\\/] | `exclude-regex-tmp-folder` |
| 158 | `needle` | substring, exclude regex \.bak$ | `exclude-regex-bak` |
| 159 | `needle` | substring, exclude regex \.test\.txt$ | `exclude-regex-test-files` |
| 160 | `needle` | substring, exclude regex \.(log\|tmp)$ | `exclude-regex-ext-alternation` |
| 161 | `needle` | substring, exclude regex /v\d+/ | `exclude-regex-numeric-dir` |
| 162 | `needle` | substring, exclude regex /dist/ | `exclude-regex-dist-dir` |
| 163 | `needle` | substring, include *.cs, exclude node_modules | `include-ext-exclude-segment` |
| 164 | `needle` | substring, include txt, exclude *.min.txt | `include-ext-exclude-ext` |
| 165 | `needle` | substring, include src/**/*.txt, exclude *.skip.txt | `include-glob-exclude-ext` |
| 166 | `needle` | substring, include *.cs, exclude *.cs | `exclude-wins-over-include` |
| 167 | `needle` | substring, include *.cs | `include-filters-before-content` |
| 168 | `needle` | substring, exclude *.log | `exclude-filters-before-content` |
| 169 | `needle` | substring, include *.cs *.js | `include-multiple-content-subset` |

### Search modes (file names vs contents) (35)

| # | Query | Search settings | Scenario |
| ---: | --- | --- | --- |
| 170 | `needle` | substring | `mode-content-only-ignores-filename` |
| 171 | `report` | substring | `mode-content-default-ignores-filename` |
| 172 | `hit` | substring | `mode-content-multiple-files` |
| 173 | `hit` | substring | `mode-content-counts-lines` |
| 174 | `needle` | substring, file names only | `mode-filenames-only` |
| 175 | `config` | substring, file names only | `mode-filenames-substring-leaf` |
| 176 | `data` | substring, file names only | `mode-filenames-token-in-name` |
| 177 | `match` | substring, file names only | `mode-filenames-empty-content-name-match` |
| 178 | `readme` | substring, file names only | `mode-filenames-case-insensitive` |
| 179 | `readme` | substring, case-sensitive, file names only | `mode-filenames-case-sensitive` |
| 180 | `log` | whole word, file names only | `mode-filenames-whole-word` |
| 181 | `v\d` | regex, file names only | `mode-filenames-regex` |
| 182 | `needle` | substring, file names only | `mode-filenames-count-one-per-file` |
| 183 | `foo bar` | substring, file names only | `mode-filenames-multiterm` |
| 184 | `zzz` | substring, file names only | `mode-filenames-no-match` |
| 185 | `needle` | substring, file names only | `mode-filenames-dir-token-ignored` |
| 186 | `rep` | substring, file names only | `mode-filenames-total-equals-files` |
| 187 | `config` | substring, file names only | `mode-filenames-substring-version` |
| 188 | `needle` | substring, file names + contents | `mode-both-name-and-content` |
| 189 | `tag` | substring, file names + contents | `mode-both-name-only` |
| 190 | `tag` | substring, file names + contents | `mode-both-content-only` |
| 191 | `tag` | substring, file names + contents | `mode-both-name-plus-content-rows` |
| 192 | `find` | substring, file names + contents | `mode-both-distinct-files` |
| 193 | `err\d` | regex, file names + contents | `mode-both-regex` |
| 194 | `err` | substring, case-sensitive, file names + contents | `mode-both-case-sensitive` |
| 195 | `alpha beta` | substring, file names + contents | `mode-both-multiterm` |
| 196 | `target` | substring, names, then contents | `mode-filename-then-content` |
| 197 | `keep` | substring, names, then contents | `mode-ftc-requires-name` |
| 198 | `name` | substring, names, then contents | `mode-ftc-name-match-no-content` |
| 199 | `data` | substring, names, then contents | `mode-ftc-content-rows-only` |
| 200 | `log\d` | regex, names, then contents | `mode-ftc-regex` |
| 201 | `alpha beta` | substring, names, then contents | `mode-ftc-multiterm` |
| 202 | `needle` | substring, names, then contents | `mode-ftc-no-name-match` |
| 203 | `data` | substring, case-sensitive, names, then contents | `mode-ftc-case-sensitive` |
| 204 | `hit` | substring, file names + contents | `mode-both-content-counts` |

### Size & date ranges (35)

| # | Query | Search settings | Scenario |
| ---: | --- | --- | --- |
| 205 | `needle` | substring, ≥ 10 B, ≤ 40 B | `size-range-min-and-max` |
| 206 | `needle` | substring, ≥ 20 B | `size-min-only` |
| 207 | `needle` | substring, ≤ 20 B | `size-max-only` |
| 208 | `needle` | substring, ≥ 25 B, ≤ 35 B | `size-exact-band` |
| 209 | `needle` | substring, ≥ 100 B | `size-min-excludes-all` |
| 210 | `needle` | substring, ≤ 10 B | `size-max-excludes-all` |
| 211 | `needle` | substring | `size-min-zero-includes-all` |
| 212 | `needle` | substring, ≥ 500 B | `size-large-threshold` |
| 213 | `needle` | substring, ≥ 20 B, ≤ 60 B | `size-band-two-pass` |
| 214 | `needle` | substring, ≤ 50 B | `size-tiny-vs-big` |
| 215 | `needle` | substring, ≥ 200 B | `size-min-boundary-margin` |
| 216 | `needle` | substring, ≤ 200 B | `size-max-boundary-margin` |
| 217 | `needle` | substring, ≥ 100 B, ≤ 200 B | `size-range-single-pass` |
| 218 | `needle` | substring, ≤ 1000 B | `size-all-below-max` |
| 219 | `needle` | substring, ≥ 10 B | `size-all-above-min` |
| 220 | `needle` | substring, ≥ 50 B | `size-content-and-size` |
| 221 | `needle` | substring, include *.cs, ≥ 50 B | `size-with-include-ext` |
| 222 | `needle` | substring, ≥ 30 B, ≤ 70 B | `size-range-excludes-both-ends` |
| 223 | `needle` | substring, modified after 2023-01-01, modified before 2025-01-01 | `modified-date-range` |
| 224 | `needle` | substring, created after 2020-01-01 | `created-date-after` |
| 225 | `needle` | substring, modified after 2020-01-01 | `modified-after-only` |
| 226 | `needle` | substring, modified before 2020-01-01 | `modified-before-only` |
| 227 | `needle` | substring, created before 2020-01-01 | `created-before-only` |
| 228 | `needle` | substring, created after 2020-01-01, created before 2025-01-01 | `created-date-range` |
| 229 | `needle` | substring, modified after 2020-01-01 | `modified-after-excludes-all` |
| 230 | `needle` | substring, modified before 2020-01-01 | `modified-before-excludes-all` |
| 231 | `needle` | substring, modified after 2020-01-01, modified before 2025-01-01 | `modified-range-two-pass` |
| 232 | `needle` | substring, created after 2000-01-01 | `created-after-includes-all` |
| 233 | `needle` | substring, modified after 2030-01-01 | `modified-recent-vs-old` |
| 234 | `needle` | substring, modified after 2023-01-01 | `date-and-content` |
| 235 | `needle` | substring, modified after 2020-01-01, modified before 2024-01-01 | `modified-range-single` |
| 236 | `needle` | substring, created after 2020-01-01, created before 2024-01-01 | `created-range-excludes-ends` |
| 237 | `needle` | substring, ≥ 50 B, modified after 2023-01-01 | `modified-and-size` |
| 238 | `needle` | substring, modified after 2100-01-01 | `modified-after-future-excludes-all` |
| 239 | `needle` | substring, created after 2020-01-01, modified before 2030-01-01 | `created-after-and-modified-before` |

### Result count, matches-per-file & depth limits (20)

| # | Query | Search settings | Scenario |
| ---: | --- | --- | --- |
| 240 | `needle` | substring, max 2 match(es)/file | `max-matches-per-file-caps-rows` |
| 241 | `needle` | substring, max 1 match(es)/file | `max-matches-per-file-one` |
| 242 | `needle` | substring, max 3 match(es)/file | `max-matches-per-file-three` |
| 243 | `needle` | substring, max 10 match(es)/file | `max-matches-per-file-above-count` |
| 244 | `needle` | substring, max 2 match(es)/file | `max-matches-per-file-two-files` |
| 245 | `needle` | substring, max 3 match(es)/file | `max-matches-per-file-exact-equal` |
| 246 | `\d` | regex, max 2 match(es)/file | `max-matches-per-file-with-regex` |
| 247 | `cat dog` | substring, max 2 match(es)/file | `max-matches-per-file-multiterm` |
| 248 | `needle` | substring, max 2 match(es)/file | `max-matches-among-mixed-lines` |
| 249 | `needle` | substring, max 100 result(s), max 2 match(es)/file | `max-results-and-maxmatches-combo` |
| 250 | `needle` | substring, max 100 result(s) | `max-results-above-count-returns-all` |
| 251 | `needle` | substring, max 1000 result(s) | `max-results-large-returns-all` |
| 252 | `needle` | substring | `max-results-unlimited-zero` |
| 253 | `needle` | substring, max 100 result(s) | `max-results-high-with-many-lines` |
| 254 | `needle` | substring, max 50 result(s) | `max-results-above-total-multi-file` |
| 255 | `needle` | substring, depth 1 | `max-depth-1-includes-first-level` |
| 256 | `needle` | substring, depth 2 | `max-depth-2-includes-second-level` |
| 257 | `needle` | substring, depth 1 | `max-depth-1-excludes-deeper-contains` |
| 258 | `needle` | substring | `max-depth-unlimited-finds-all` |
| 259 | `needle` | substring, depth 2 | `max-depth-2-flat-tree-all` |

### Binary, hidden files, archives & multi-term (41)

| # | Query | Search settings | Scenario |
| ---: | --- | --- | --- |
| 260 | `needle` | substring | `binary-nul-skipped-by-default` |
| 261 | `needle` | substring, search binaries | `binary-nul-searched-with-search-binary` |
| 262 | `needle` | substring | `binary-png-skipped-by-default` |
| 263 | `needle` | substring | `binary-nul-only-skipped-empty` |
| 264 | `needle` | substring, search binaries | `binary-two-nul-searched-with-search-binary` |
| 265 | `needle` | substring | `binary-png-and-nul-default-skips-both` |
| 266 | `needle` | substring | `hidden-included-by-default` |
| 267 | `needle` | substring, exclude hidden files | `hidden-excluded-when-no-hidden` |
| 268 | `needle` | substring, exclude hidden files | `hidden-only-no-hidden-empty` |
| 269 | `needle` | substring, exclude hidden files | `hidden-nested-excluded-when-no-hidden` |
| 270 | `needle` | substring | `hidden-mixed-counts-default` |
| 271 | `needle` | substring, skip .log | `skip-extension-excludes-files` |
| 272 | `needle` | substring, skip .log, .tmp | `skip-extension-multiple` |
| 273 | `needle` | substring, skip .log | `skip-extension-keeps-others` |
| 274 | `needle` | substring, skip .log | `skip-extension-case-insensitive` |
| 275 | `needle` | substring, skip .log, .tmp | `skip-extension-only-one-left` |
| 276 | `cat dog` | substring | `multiterm-substring-is-or` |
| 277 | `needle` | substring | `no-matches-returns-empty` |
| 278 | `red green blue` | substring | `multiterm-three-or` |
| 279 | `nation international` | substring | `multiterm-overlapping-terms` |
| 280 | `foo\d+` | regex, include *.cs | `regex-with-include-ext` |
| 281 | `async` | whole word, exclude node_modules | `wholeword-with-exclude-segment` |
| 282 | `Error` | substring, case-sensitive, include *.cs | `case-sensitive-with-include` |
| 283 | `err` | regex, case-sensitive, depth 1 | `regex-case-sensitive-with-depth` |
| 284 | `needle` | substring, include *.cs, ≥ 80 B | `substring-with-size-and-ext` |
| 285 | `match` | substring, file names only, include *.cs | `mode-filenames-with-include-ext` |
| 286 | `v\d` | regex, exclude regex /skipdir/ | `regex-with-exclude-regex` |
| 287 | `cat dog` | substring, max 2 match(es)/file | `multiterm-with-maxmatches` |
| 288 | `needle` | substring, include *.cs | `hidden-with-include-ext` |
| 289 | `foo\d` | regex, skip .log | `skipext-with-regex` |
| 290 | `needle` | substring | `crlf-line-endings` |
| 291 | `needle` | substring | `very-long-line` |
| 292 | `needle` | substring | `token-at-eof-no-newline` |
| 293 | `needle` | substring | `blank-lines-between-matches` |
| 294 | `needle` | substring | `many-files-same-token` |
| 295 | `needle` | substring | `deeply-nested-single-match` |
| 296 | `needle` | substring | `mixed-case-corpus-insensitive` |
| 297 | `a+b` | substring | `special-chars-literal-substring` |
| 298 | `test` | substring | `unicode-content-ascii-query` |
| 299 | `needle` | substring | `empty-file-no-match` |
| 300 | `needle` | substring | `whitespace-content-no-token` |
