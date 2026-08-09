using System.Diagnostics;
using System.Threading;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Yagu.Helpers;
using Yagu.Services;
using Yagu.Services.Index;

namespace Yagu;

/// <summary>
/// The dedicated <b>Indexing</b> settings tab (plan §6.1/§6.2, Phase 2 GUI integration). It is the
/// authoritative home for every user-tunable content-index behavior plus all management actions
/// (build/rebuild/validate/delete/clear/open/restore-defaults). Controls read and write the
/// <c>Index*</c> fields on the live <see cref="MainViewModel.Settings"/> through the shared
/// <c>AppSettings.Normalize*</c> validators, so persistence (via <c>PersistSettingsAsync</c>) and the
/// window's cancel/restore work exactly like every other tab. Management actions call the pure
/// <see cref="ContentIndexManager"/> off the UI thread. Correctness/security invariants are exposed as
/// read-only reasons, never as unsafe switches (plan §6.1).
/// </summary>
public sealed partial class SettingsWindow
{
    // Cross-call state for the management actions. The build is cancellable; only one action runs at a time.
    private CancellationTokenSource? _indexBuildCts;
    private bool _indexActionInProgress;
    private TextBlock? _indexStatusText;

    private void BuildIndexingTab()
    {
        var g = AddTab("Indexing");

        var featureGroup = AddSettingsGroupBox(g, "Content Index");
        var manageGroup = AddSettingsGroupBox(g, "Manage Indexes");
        var accelerationGroup = AddSettingsGroupBox(g, "Query Acceleration");
        var scopeGroup = AddSettingsGroupBox(g, "Scope & Ingestion");
        var storageGroup = AddSettingsGroupBox(g, "Storage");
        var sizeGroup = AddSettingsGroupBox(g, "Index Size Management");
        var scheduleGroup = AddSettingsGroupBox(g, "Build Scheduling");
        var resourcesGroup = AddSettingsGroupBox(g, "Build Resources");
        var presentationGroup = AddSettingsGroupBox(g, "Status & Provenance");

        // Reseed callbacks let "Restore indexing defaults" push the reset values back into every control.
        var reseed = new List<Action>();

        static TextBlock Description(string text) => new()
        {
            Text = text,
            FontSize = 11,
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap,
        };

        ToggleSwitch AddIndexToggle(StackPanel group, string label, Func<AppSettings, bool> get, Action<AppSettings, bool> set, string description)
        {
            var toggle = new ToggleSwitch
            {
                OnContent = label,
                OffContent = label,
                IsOn = get(_viewModel.Settings),
                MinWidth = 0,
            };
            toggle.Toggled += (_, _) => set(_viewModel.Settings, toggle.IsOn);
            group.Children.Add(toggle);
            group.Children.Add(Description(description));
            reseed.Add(() => toggle.IsOn = get(_viewModel.Settings));
            return toggle;
        }

        NumberBox AddIndexNumber(StackPanel group, string label, Func<AppSettings, int> get, Action<AppSettings, int> setNormalized, double min, double max, string description)
        {
            group.Children.Add(NextSearchLabel(label));
            var box = new NumberBox
            {
                Value = get(_viewModel.Settings),
                Minimum = min,
                Maximum = max,
                HorizontalAlignment = HorizontalAlignment.Left,
                Width = NumericSettingBoxWidth,
            };
            box.ValueChanged += (_, args) =>
            {
                if (!double.IsNaN(args.NewValue))
                    setNormalized(_viewModel.Settings, (int)args.NewValue);
            };
            group.Children.Add(box);
            group.Children.Add(Description(description));
            reseed.Add(() => box.Value = get(_viewModel.Settings));
            return box;
        }

        ComboBox AddIndexCombo(StackPanel group, string label, (string Tag, string Display)[] options, Func<AppSettings, string> get, Action<AppSettings, string> setNormalized, string description)
        {
            group.Children.Add(NextSearchLabel(label));
            var combo = new ComboBox { MinWidth = 260, HorizontalAlignment = HorizontalAlignment.Left };
            foreach (var (tag, display) in options)
                combo.Items.Add(new ComboBoxItem { Content = display, Tag = tag });

            void Select(string value)
            {
                foreach (var item in combo.Items)
                {
                    if (item is ComboBoxItem ci && string.Equals(ci.Tag?.ToString(), value, StringComparison.OrdinalIgnoreCase))
                    {
                        combo.SelectedItem = item;
                        return;
                    }
                }
            }

            Select(get(_viewModel.Settings));
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedItem is ComboBoxItem { Tag: string tag })
                    setNormalized(_viewModel.Settings, tag);
            };
            group.Children.Add(combo);
            group.Children.Add(Description(description));
            reseed.Add(() => Select(get(_viewModel.Settings)));
            return combo;
        }

        TextBox AddIndexText(StackPanel group, string label, Func<AppSettings, string> get, Action<AppSettings, string> set, string placeholder, string description)
        {
            group.Children.Add(NextSearchLabel(label));
            var box = new TextBox
            {
                Text = get(_viewModel.Settings),
                PlaceholderText = placeholder,
                Width = 460,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            box.TextChanged += (_, _) => set(_viewModel.Settings, box.Text ?? string.Empty);
            group.Children.Add(box);
            group.Children.Add(Description(description));
            reseed.Add(() => box.Text = get(_viewModel.Settings));
            return box;
        }

        // ── Feature ──
        featureGroup.Children.Add(new TextBlock
        {
            Text = "Yagu can build an optional, on-device content index so repeated content searches over an indexed folder can skip files that cannot contain a match and verify only the rest live. It is opt-in, never sends anything off the machine, and always falls back to a full live scan when the index is missing, stale, disabled, or unsafe — results never change.",
            FontSize = 12,
            Opacity = 0.8,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6),
        });

        ToggleSwitch useByDefaultToggle = null!;
        var masterToggle = AddIndexToggle(
            featureGroup,
            "Enable content indexing",
            s => s.EnableContentIndex,
            (s, v) => s.EnableContentIndex = v,
            "Master switch. On by default, but no folder is indexed until you add one below. Turning it off stops all new build/update work but never silently deletes existing index data — use the actions below to remove data explicitly.");

        useByDefaultToggle = AddIndexToggle(
            featureGroup,
            "Use the index for searches by default",
            s => s.UseContentIndexByDefault,
            (s, v) => s.UseContentIndexByDefault = v,
            "When on (and the master switch is enabled), searches use the index automatically. Individual searches can still opt out with the Advanced Options ▸ Use content index toggle or the CLI --no-index flag. Effectively off while the master switch is disabled.");

        var masterSummary = Description(ContentIndexUiStatus.MasterStateSummary(
            _viewModel.Settings.EnableContentIndex, _viewModel.Settings.UseContentIndexByDefault));
        featureGroup.Children.Add(masterSummary);

        void UpdateMasterSummary() => masterSummary.Text = ContentIndexUiStatus.MasterStateSummary(
            _viewModel.Settings.EnableContentIndex, _viewModel.Settings.UseContentIndexByDefault);

        // ── Query Acceleration ──
        AddIndexToggle(accelerationGroup, "Accelerate literal searches",
            s => s.IndexAccelerateLiterals, (s, v) => s.IndexAccelerateLiterals = v,
            "Allow the index to accelerate plain substring searches. These controls can only narrow the safety gate — they never force an unsafe query onto the index.");
        AddIndexToggle(accelerationGroup, "Accelerate whole-word / exact searches",
            s => s.IndexAccelerateWholeWord, (s, v) => s.IndexAccelerateWholeWord = v,
            "Allow the index to accelerate whole-word and exact-phrase searches (word boundaries are still verified live).");
        AddIndexToggle(accelerationGroup, "Accelerate regular-expression searches",
            s => s.IndexAccelerateRegex, (s, v) => s.IndexAccelerateRegex = v,
            "Allow the index to accelerate the conservatively supported subset of regular expressions. Unsupported patterns always live-scan.");
        AddIndexToggle(accelerationGroup, "Accelerate multiline searches",
            s => s.IndexAccelerateMultiline, (s, v) => s.IndexAccelerateMultiline = v,
            "Allow the index to accelerate multiline searches. Case-insensitive searches are accelerated for ASCII queries; non-ASCII case-insensitive searches live-scan (results are unaffected).");
        AddIndexToggle(accelerationGroup, "Isolate index maintenance and candidate evaluation",
            s => s.IndexUseNativeWorker, (s, v) => s.IndexUseNativeWorker = v,
            "Runs full builds, incremental refreshes, compaction, validation, PDF index population, and legacy candidate-set evaluation in isolated workers. The legacy query path still opens and classifies the index inside Yagu, so the in-process size limit below still applies. This is not the mapped query-session setting. Default on.");
        var workerQuerySessionsToggle = AddIndexToggle(
            accelerationGroup,
            "Use memory-mapped worker query sessions (format-v3)",
            s => s.IndexUseWorkerQuerySessions,
            (s, v) => s.IndexUseWorkerQuerySessions = v,
            "Moves the complete candidate/path-classification and pruning session into the long-lived isolated worker, so large indexes do not have to be opened in Yagu's process and are governed by the mapped-worker size limit instead of the in-process limit. Requires the complete set of format-v3 query files in every active layer; new builds create those files by default. If any required file is missing, or the worker/freshness check fails, Yagu safely reads files live. Default on.");

        AddIndexNumber(accelerationGroup, "Query startup budget (ms):",
            s => s.IndexQueryStartupBudgetMs,
            (s, v) => s.IndexQueryStartupBudgetMs = AppSettings.NormalizeIndexQueryStartupBudgetMs(v),
            25, 2000,
            "Maximum time a search spends opening/catching-up the index before it gives up and live-scans this search. File discovery never waits for it. Range 25–2000; default 200.");
        AddIndexNumber(accelerationGroup, "Candidate bypass threshold (%):",
            s => s.IndexMaxCandidatePercent,
            (s, v) => s.IndexMaxCandidatePercent = AppSettings.NormalizeIndexMaxCandidatePercent(v),
            1, 100,
            "If the index would select more than this percentage of the folder as candidates, the search live-scans instead (the index isn't helping). It can only choose live scan, never force pruning. Range 1–100; default 25.");
        AddIndexNumber(accelerationGroup, "Query memory budget (MB, 0 = automatic):",
            s => s.IndexQueryMemoryBudgetMB,
            (s, v) => s.IndexQueryMemoryBudgetMB = AppSettings.NormalizeIndexQueryMemoryBudgetMB(v),
            0, 4096,
            $"Memory the index query may use before a search bypasses it. 0 = automatic ({_viewModel.Settings.EffectiveIndexQueryMemoryBudgetMB} MB on this build).");
        AddIndexNumber(accelerationGroup, "Query worker parallelism (0 = automatic):",
            s => s.IndexQueryWorkerParallelism,
            (s, v) => s.IndexQueryWorkerParallelism = AppSettings.NormalizeIndexQueryWorkerParallelism(v),
            0, AppSettings.MaximumIndexWorkerParallelism,
            $"Mapped path classification lanes. 0 = automatic from logical processors (currently {IndexWorkerParallelism.ResolveQueryDegree(0, Environment.ProcessorCount, false, false)}). Performance ▸ “Limit disk-intensive parallelism on HDDs” is authoritative: an HDD search root always uses one lane.");
        AddIndexNumber(accelerationGroup, "Load index in-process only under (MB, 0 = always live-scan):",
            s => s.IndexMaxInProcessSizeMB,
            (s, v) => s.IndexMaxInProcessSizeMB = AppSettings.NormalizeIndexMaxInProcessSizeMB(v),
            0, 1_048_576,
            "An index whose on-disk size exceeds this is never loaded into memory — that scope always live-scans instead. Loading a multi-GB index leaves a large resident footprint that can make searches slower than a plain live scan, so lower this to force big folders (e.g. a whole drive) onto the fast live-scan path. 0 = never load any index in-process; default 2048.");
        AddIndexNumber(accelerationGroup, "Mapped worker query size limit (MB, 0 = disabled):",
            s => s.IndexMaxWorkerQuerySizeMB,
            (s, v) => s.IndexMaxWorkerQuerySizeMB = AppSettings.NormalizeIndexMaxWorkerQuerySizeMB(v),
            0, AppSettings.MaximumIndexMaxWorkerQuerySizeMB,
            "Maximum format-v3 data the isolated mapped query worker may open for one active index. This applies only when memory-mapped worker query sessions are enabled. 0 disables mapped worker queries; default 30720 MB (30 GB).");

        // ── Scope & Ingestion ──
        scopeGroup.Children.Add(Description(
            "These control what a build ingests. They are not per-search filters — a file omitted from the index is simply live-scanned whenever a later search permits it. Changing them does not change search results."));
        AddIndexToggle(scopeGroup, "Index hidden files",
            s => s.IndexIncludeHiddenFiles, (s, v) => s.IndexIncludeHiddenFiles = v,
            "Whether index builds ingest files with the Hidden attribute. A hidden file left unindexed is still live-scanned when a search includes it.");
        AddIndexToggle(scopeGroup, "Follow reparse points (junctions / symlinks)",
            s => s.IndexFollowReparsePoints, (s, v) => s.IndexFollowReparsePoints = v,
            "Off by default. When on, a build indexes a reparse point only when its final target stays on the same local volume and inside the indexed folder; every other target is live-only.");
        AddIndexToggle(scopeGroup, "Build a PDF-text index to skip non-matching PDFs",
            s => s.IndexBuildPdfTextExtendedSource, (s, v) => s.IndexBuildPdfTextExtendedSource = v,
            "Off by default. When on, an index build extracts PDF text (via the bundled pdftotext) so a later PDF-text search can skip PDFs whose text cannot contain a match. Only skips when the extractor is proven repeatable and the PDF is unchanged; matching PDFs are always read live.");
        AddIndexToggle(scopeGroup, "Build an image-text index to prioritize likely OCR matches",
            s => s.IndexBuildImageTextExtendedSource, (s, v) => s.IndexBuildImageTextExtendedSource = v,
            "Off by default. A full index build runs the selected OCR engine over eligible images and stores only positive trigram postings — never recognized text. Because OCR output is non-deterministic, this index prioritizes likely candidates but never skips OCR for a non-matching, changed, unknown, or fingerprint-mismatched image. Enabling it recommends rebuilding existing indexes and can make whole-drive builds substantially longer.");
        var produceV3Toggle = AddIndexToggle(scopeGroup, "Produce format-v3 query structures",
            s => s.IndexProduceV3QueryStructures, (s, v) => s.IndexProduceV3QueryStructures = v,
            "On by default. Builds write additional memory-map-friendly files for postings, paths/identities, and deletion markers alongside each index layer. Mapped isolated-worker query sessions actively use these files, and the optional in-process v3 reader can also use them. Cost: additional build I/O/time and disk space; every active layer needs the complete file set, so older indexes require rebuilding before the all-v3 worker path can serve them.");
        var useV3Toggle = AddIndexToggle(scopeGroup, "Use format-v3 reader for in-process queries (experimental)",
            s => s.IndexUseV3QueryReader, (s, v) => s.IndexUseV3QueryReader = v,
            "Controls only the in-process candidate reader. When on, candidates come from memory-mapped v3 postings instead of a deserialized posting index, reducing host allocations. The isolated mapped query-worker path uses v3 independently of this switch. If the required format-v3 query files are missing or incompatible, or a read fails, Yagu safely falls back or reads files live; results are identical.");
        var v3ModeStatus = Description(string.Empty);
        v3ModeStatus.Opacity = 0.8;
        scopeGroup.Children.Add(v3ModeStatus);

        void UpdateV3ModeStatus()
        {
            AppSettings settings = _viewModel.Settings;
            v3ModeStatus.Text = settings.IndexUseWorkerQuerySessions
                ? settings.IndexProduceV3QueryStructures
                    ? "Active query path: the isolated mapped query worker is using format-v3 structures for eligible roots. The in-process-reader switch above may remain off; it does not disable this worker path."
                    : "Mapped query-worker mode is enabled, but v3 production is off. It can use only existing layers that already have the complete set of format-v3 query files; any root with a missing layer safely reads files live."
                : settings.IndexUseV3QueryReader
                    ? "Active query path: the in-process format-v3 candidate reader is selected where all required format-v3 query files are available."
                    : settings.IndexProduceV3QueryStructures
                        ? "Additional format-v3 query files are being produced alongside each index layer, but the current query mode is not using them. They remain available for mapped-worker or in-process-v3 use after that mode is enabled."
                        : "Format-v3 structures are neither produced nor selected. Searches use the compatible content.bin/in-process path or safely live-scan.";
        }

        produceV3Toggle.Toggled += (_, _) => UpdateV3ModeStatus();
        useV3Toggle.Toggled += (_, _) => UpdateV3ModeStatus();
        workerQuerySessionsToggle.Toggled += (_, _) => UpdateV3ModeStatus();
        reseed.Add(UpdateV3ModeStatus);
        UpdateV3ModeStatus();
        AddIndexCombo(scopeGroup, "Removable-drive policy:",
            new[] { ("Never", "Never index removable drives"), ("ExplicitRootsOnly", "Only explicitly chosen removable roots") },
            s => s.IndexRemovableDrivePolicy,
            (s, v) => s.IndexRemovableDrivePolicy = AppSettings.NormalizeIndexRemovableDrivePolicy(v),
            "Yagu never silently indexes newly attached media. Default: Never.");
        AddIndexNumber(scopeGroup, "Maximum file size to index (MB):",
            s => s.IndexMaxFileSizeMB,
            (s, v) => s.IndexMaxFileSizeMB = AppSettings.NormalizeIndexMaxFileSizeMB(v),
            1, 100000,
            "Hard per-file cap. Files larger than this are not indexed and are live-scanned when queried. Default 100.");
        AddIndexText(scopeGroup, "Excluded globs (build-time):",
            s => s.IndexExcludedGlobs, (s, v) => s.IndexExcludedGlobs = v?.Trim() ?? string.Empty,
            "e.g. **/node_modules/**; **/bin/**",
            "Comma/semicolon-separated globs excluded from ingestion. Empty by default. Excluded files still live-scan when a search permits them.");
        AddIndexText(scopeGroup, "Excluded extensions (build-time):",
            s => s.IndexExcludedExtensions, (s, v) => s.IndexExcludedExtensions = v?.Trim() ?? string.Empty,
            "e.g. .min.js, .map",
            "Comma/semicolon-separated extensions excluded from ingestion. Empty by default.");
        scopeGroup.Children.Add(new TextBlock
        {
            Text = "Fixed safety policies (not overridable in this release): case-sensitive directories are always live-scanned, and online-only cloud files are never indexed or hydrated. These are exposed as reasons, not switches, so an unsafe folder can't be forced onto the index.",
            FontSize = 11,
            Opacity = 0.55,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        });

        // ── Storage ──
        var storageBox = AddIndexText(storageGroup, "Index data location (where indexes are saved; empty = default):",
            s => s.IndexStorageDirectory,
            (s, v) => s.IndexStorageDirectory = AppSettings.NormalizeIndexStorageDirectory(v),
            "%LOCALAPPDATA%\\Yagu\\content-index",
            "Where Yagu saves the index files it builds — this is NOT a folder whose contents get indexed (choose that under Manage Indexes). A custom location must be a fixed local NTFS volume that Yagu can write to. Installation, UNC, mapped-network, removable, and cloud-backed locations are rejected. Leave empty for the per-user default.");
        var browseStorage = new Button
        {
            Content = "Browse…",
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 0, 0, 4),
        };
        browseStorage.Click += (_, _) =>
        {
            string? folder = Win32FileDialog.SelectFolder(_settingsHwnd, "Select Index Storage Folder");
            if (!string.IsNullOrWhiteSpace(folder))
                storageBox.Text = folder;
        };
        storageGroup.Children.Add(browseStorage);

        AddIndexNumber(storageGroup, "Size budget per index (MB, 0 = no limit):",
            s => s.IndexMaxDiskSizeMB,
            (s, v) => s.IndexMaxDiskSizeMB = AppSettings.NormalizeIndexMaxDiskSizeMB(v),
            0, 1048576,
            $"The storage ceiling for a single index, overridable per folder under Manage Indexes ▸ Size. On reaching it, Yagu reclaims what it safely can and then pauses updates for that index rather than letting it grow — searches still return every match, because files the index no longer covers are read live. Rebuild the index to reclaim its space. 0 = no limit; blank uses the automatic default ({ContentIndexUiStatus.FormatMegabytes(_viewModel.Settings.EffectiveIndexMaxDiskSizeMB)}).");
        AddIndexNumber(storageGroup, "Reserved free space floor (MB):",
            s => s.IndexMinimumFreeSpaceMB,
            (s, v) => s.IndexMinimumFreeSpaceMB = AppSettings.NormalizeIndexMinimumFreeSpaceMB(v),
            0, 1048576,
            "Builds stop before the volume's free space would drop below this. Default 2048.");
        AddIndexNumber(storageGroup, "Stop indexing when the drive is this % full:",
            s => s.IndexMaxDiskUsagePercent,
            (s, v) => s.IndexMaxDiskUsagePercent = AppSettings.NormalizeIndexMaxDiskUsagePercent(v),
            AppSettings.MinimumIndexMaxDiskUsagePercent, AppSettings.MaximumIndexMaxDiskUsagePercent,
            "An index build in progress stops when the index drive reaches this used-space percentage; the partial index already written is kept. Default 90.");
        AddIndexNumber(storageGroup, "Retained generations (including current):",
            s => s.IndexRetainedGenerationCount,
            (s, v) => s.IndexRetainedGenerationCount = AppSettings.NormalizeIndexRetainedGenerationCount(v),
            1, 10,
            "How many index generations are kept per folder, including the current one. Minimum 1; default 2 (current + one prior).");
        AddIndexNumber(storageGroup, "Stale temporary-build cleanup (hours):",
            s => s.IndexStaleTemporaryHours,
            (s, v) => s.IndexStaleTemporaryHours = AppSettings.NormalizeIndexStaleTemporaryHours(v),
            1, 8760,
            "Abandoned temporary build folders older than this are cleaned up. Default 24.");
        AddIndexNumber(storageGroup, "Quarantine retention (days):",
            s => s.IndexQuarantineRetentionDays,
            (s, v) => s.IndexQuarantineRetentionDays = AppSettings.NormalizeIndexQuarantineRetentionDays(v),
            1, 365,
            "How long a failed/quarantined generation is retained for diagnostics. Default 7.");

        // ── Build Scheduling ──
        // Build trigger(s): checkboxes so several automatic triggers can be active at once (e.g. build
        // at startup AND on a schedule). None checked = Manual — indexing only runs when you ask.
        scheduleGroup.Children.Add(NextSearchLabel("Build trigger:"));
        var triggerFlags = new (string Flag, string Display)[]
        {
            ("WhenEnabled", "When enabled — build on enabling the feature"),
            ("AtStartup", "At startup"),
            ("WhenIdle", "When the machine is idle"),
            ("Continuous", "Continuously while Yagu is open"),
            ("OnSchedule", "On a schedule"),
        };
        var triggerChecks = new CheckBox[triggerFlags.Length];
        var triggerColumn = new StackPanel { Spacing = 2 };

        void ApplyTriggerSelection()
        {
            var selected = new List<string>(triggerChecks.Length);
            for (int i = 0; i < triggerChecks.Length; i++)
                if (triggerChecks[i].IsChecked == true)
                    selected.Add(triggerFlags[i].Flag);
            _viewModel.Settings.IndexBuildTrigger = AppSettings.NormalizeIndexBuildTrigger(string.Join(",", selected));
        }

        for (int i = 0; i < triggerFlags.Length; i++)
        {
            var cb = new CheckBox
            {
                Content = triggerFlags[i].Display,
                MinWidth = 0,
                IsChecked = AppSettings.IndexBuildTriggerHas(_viewModel.Settings.IndexBuildTrigger, triggerFlags[i].Flag),
            };
            // Handlers are attached below, once the schedule sub-panel (which UpdateScheduleVisibility
            // toggles) has been created.
            triggerChecks[i] = cb;
            triggerColumn.Children.Add(cb);
        }
        scheduleGroup.Children.Add(triggerColumn);
        scheduleGroup.Children.Add(Description(
            "When Yagu automatically maintains the folders you added under Manage Indexes. “Continuously” uses the same safe path as idle maintenance but treats the PC as always idle; the interval below prevents a busy loop. To keep existing indexes current, pair it with Automatic incremental (recommended) or Automatic full rebuild when changed — Manual full rebuild only creates missing indexes. You can enable more than one trigger. With none selected, indexing is Manual."));


        // Schedule sub-panel — shown only when the trigger is "On a schedule". Lets the user pick an
        // interval (every N minutes) or specific weekdays at a set time. The schedule only fires while
        // Yagu is running, and a pass still only (re)builds folders that are missing or (in an automatic
        // update mode) changed, so a short interval mostly does nothing.
        var schedulePanel = new StackPanel { Spacing = 6, Margin = new Thickness(12, 0, 0, 6) };
        scheduleGroup.Children.Add(schedulePanel);

        // Assigned once the update-mode combo below exists; the trigger checkboxes re-evaluate the warning
        // because the misconfiguration depends on BOTH the trigger and the update mode.
        Action? refreshStaleUpdateModeWarning = null;

        var scheduleModeCombo = AddIndexCombo(schedulePanel, "Schedule:",
            new[]
            {
                ("Interval", "Every N minutes"),
                ("Weekly", "On chosen days at a set time"),
            },
            s => s.IndexScheduleMode,
            (s, v) => s.IndexScheduleMode = AppSettings.NormalizeIndexScheduleMode(v),
            "Repeat on a fixed interval, or run on specific weekdays at a time you choose.");

        var intervalPanel = new StackPanel { Spacing = 6 };
        schedulePanel.Children.Add(intervalPanel);
        AddIndexNumber(intervalPanel, "Run every (minutes):",
            s => s.IndexScheduleIntervalMinutes,
            (s, v) => s.IndexScheduleIntervalMinutes = AppSettings.NormalizeIndexScheduleIntervalMinutes(v),
            AppSettings.MinimumIndexScheduleIntervalMinutes, AppSettings.MaximumIndexScheduleIntervalMinutes,
            "How often a build pass runs (5 minutes – 1 week; default 60).");

        // Weekly: days-of-week + time-of-day. Get/set lambdas keep the exact "s.IndexSchedule*" references
        // that the CLI/UI parity check requires while driving the custom day checkboxes and time picker.
        Func<AppSettings, int> getDaysMask = s => s.IndexScheduleDaysOfWeekMask;
        Action<AppSettings, int> setDaysMask = (s, v) => s.IndexScheduleDaysOfWeekMask = AppSettings.NormalizeIndexScheduleDaysOfWeekMask(v);
        Func<AppSettings, string> getTime = s => s.IndexScheduleTimeOfDay;
        Action<AppSettings, string> setTime = (s, v) => s.IndexScheduleTimeOfDay = AppSettings.NormalizeIndexScheduleTimeOfDay(v);

        var weeklyPanel = new StackPanel { Spacing = 6 };
        schedulePanel.Children.Add(weeklyPanel);
        weeklyPanel.Children.Add(NextSearchLabel("Days:"));
        string[] dayNames = { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
        var dayChecks = new CheckBox[7];
        var daysRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        for (int d = 0; d < 7; d++)
        {
            int bit = 1 << d;
            var cb = new CheckBox
            {
                Content = dayNames[d],
                MinWidth = 0,
                IsChecked = (getDaysMask(_viewModel.Settings) & bit) != 0,
            };
            cb.Checked += (_, _) => setDaysMask(_viewModel.Settings, getDaysMask(_viewModel.Settings) | bit);
            cb.Unchecked += (_, _) => setDaysMask(_viewModel.Settings, getDaysMask(_viewModel.Settings) & ~bit);
            dayChecks[d] = cb;
            daysRow.Children.Add(cb);
        }
        weeklyPanel.Children.Add(daysRow);
        weeklyPanel.Children.Add(NextSearchLabel("Time:"));
        var timePicker = new TimePicker
        {
            ClockIdentifier = "24HourClock",
            MinWidth = 140,
            HorizontalAlignment = HorizontalAlignment.Left,
            Time = ContentIndexScheduleEvaluator.ParseTimeOfDay(getTime(_viewModel.Settings)),
        };
        timePicker.TimeChanged += (_, e) =>
            setTime(_viewModel.Settings, e.NewTime.ToString(@"hh\:mm", System.Globalization.CultureInfo.InvariantCulture));
        weeklyPanel.Children.Add(timePicker);
        weeklyPanel.Children.Add(Description("The build runs at this time on each chosen day (24-hour clock)."));

        var idleDelayPanel = new StackPanel { Spacing = 6 };
        var continuousIntervalPanel = new StackPanel { Spacing = 6 };

        void UpdateScheduleVisibility()
        {
            bool onSchedule = AppSettings.IndexBuildTriggerHas(_viewModel.Settings.IndexBuildTrigger, "OnSchedule");
            schedulePanel.Visibility = onSchedule ? Visibility.Visible : Visibility.Collapsed;
            bool weekly = string.Equals(_viewModel.Settings.IndexScheduleMode, "Weekly", StringComparison.OrdinalIgnoreCase);
            intervalPanel.Visibility = weekly ? Visibility.Collapsed : Visibility.Visible;
            weeklyPanel.Visibility = weekly ? Visibility.Visible : Visibility.Collapsed;
            idleDelayPanel.Visibility = AppSettings.IndexBuildTriggerHas(
                _viewModel.Settings.IndexBuildTrigger, "WhenIdle")
                ? Visibility.Visible
                : Visibility.Collapsed;
            continuousIntervalPanel.Visibility = AppSettings.IndexBuildTriggerHas(
                _viewModel.Settings.IndexBuildTrigger, "Continuous")
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        scheduleModeCombo.SelectionChanged += (_, _) => UpdateScheduleVisibility();
        // Now that the schedule sub-panel exists, wire the trigger checkboxes: each toggle rebuilds the
        // combined IndexBuildTrigger value and re-evaluates whether the schedule sub-panel is shown.
        foreach (var cb in triggerChecks)
        {
            cb.Checked += (_, _) => { ApplyTriggerSelection(); UpdateScheduleVisibility(); refreshStaleUpdateModeWarning?.Invoke(); };
            cb.Unchecked += (_, _) => { ApplyTriggerSelection(); UpdateScheduleVisibility(); refreshStaleUpdateModeWarning?.Invoke(); };
        }
        reseed.Add(() =>
        {
            string resetTrigger = _viewModel.Settings.IndexBuildTrigger;
            for (int i = 0; i < triggerChecks.Length; i++)
                triggerChecks[i].IsChecked = AppSettings.IndexBuildTriggerHas(resetTrigger, triggerFlags[i].Flag);
            for (int d = 0; d < 7; d++)
                dayChecks[d].IsChecked = (getDaysMask(_viewModel.Settings) & (1 << d)) != 0;
            timePicker.Time = ContentIndexScheduleEvaluator.ParseTimeOfDay(getTime(_viewModel.Settings));
            UpdateScheduleVisibility();
            refreshStaleUpdateModeWarning?.Invoke();
        });
        UpdateScheduleVisibility();

        var updateModeCombo = AddIndexCombo(scheduleGroup, "Update mode:",
            new[]
            {
                ("ManualFullRebuild", "Manual full rebuild — only build missing indexes"),
                ("AutomaticFullRebuildWhenDirty", "Automatic full rebuild when changed"),
                ("AutomaticIncremental", "Automatic incremental — apply small delta updates when changed"),
            },
            s => s.IndexUpdateMode,
            (s, v) => s.IndexUpdateMode = AppSettings.NormalizeIndexUpdateMode(v),
            "What an automatic pass does. “When changed” rebuilds the whole folder; “Incremental” applies small append-only delta updates and periodically compacts them into a fresh index. Both fall back to a live scan when the index is stale or unsafe.");

        // Inline warning for the footgun combination: an automatic trigger paired with Manual full rebuild
        // only ever CREATES missing indexes, so existing indexes go stale and searches quietly live-scan.
        // A Grid (not a horizontal StackPanel) so the message column can actually wrap.
        var staleUpdateModeWarning = new Grid { Margin = new Thickness(0, 2, 0, 6), Visibility = Visibility.Collapsed };
        staleUpdateModeWarning.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        staleUpdateModeWarning.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var staleWarningIcon = new FontIcon
        {
            Glyph = "\uE7BA",
            FontSize = 14,
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.DarkOrange),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 8, 0),
        };
        Grid.SetColumn(staleWarningIcon, 0);
        staleUpdateModeWarning.Children.Add(staleWarningIcon);
        var staleWarningText = new TextBlock
        {
            Text = "Automatic trigger(s) are selected, but “Manual full rebuild” only creates missing indexes — "
                 + "existing indexes are never refreshed, so they go stale and searches fall back to a live scan. "
                 + "Choose “Automatic incremental” (recommended) or “Automatic full rebuild when changed”.",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.DarkOrange),
        };
        Grid.SetColumn(staleWarningText, 1);
        staleUpdateModeWarning.Children.Add(staleWarningText);
        scheduleGroup.Children.Add(staleUpdateModeWarning);
        refreshStaleUpdateModeWarning = () => staleUpdateModeWarning.Visibility =
            ContentIndexBuildScheduler.IsStaleAutomaticCombination(
                _viewModel.Settings.IndexBuildTrigger, _viewModel.Settings.IndexUpdateMode)
                ? Visibility.Visible
                : Visibility.Collapsed;
        updateModeCombo.SelectionChanged += (_, _) => refreshStaleUpdateModeWarning();
        refreshStaleUpdateModeWarning();

        AddIndexNumber(idleDelayPanel, "Idle delay (minutes):",
            s => s.IndexIdleDelayMinutes,
            (s, v) => s.IndexIdleDelayMinutes = AppSettings.NormalizeIndexIdleDelayMinutes(v),
            1, 120,
            "Required time without keyboard or mouse input before idle maintenance runs. Range 1–120; default 5.");
        scheduleGroup.Children.Add(idleDelayPanel);
        AddIndexNumber(continuousIntervalPanel, "Continuous interval (minutes):",
            s => s.IndexContinuousIntervalMinutes,
            (s, v) => s.IndexContinuousIntervalMinutes = AppSettings.NormalizeIndexContinuousIntervalMinutes(v),
            1, 120,
            "Minimum time between continuous maintenance passes while Yagu remains open. Range 1–120; default 5.");
        scheduleGroup.Children.Add(continuousIntervalPanel);
        UpdateScheduleVisibility();

        // ── Build Resources ──
        AddIndexNumber(resourcesGroup, "Build memory budget (MB, 0 = automatic):",
            s => s.IndexBuildMemoryBudgetMB,
            (s, v) => s.IndexBuildMemoryBudgetMB = AppSettings.NormalizeIndexBuildMemoryBudgetMB(v),
            0, 8192,
            $"Total memory a build worker may use. 0 = automatic ({ContentIndexUiStatus.FormatMegabytes(_viewModel.Settings.EffectiveIndexBuildMemoryBudgetMB)} on this build).");
        AddIndexNumber(resourcesGroup, "Build worker parallelism (0 = automatic):",
            s => s.IndexBuildWorkerParallelism,
            (s, v) => s.IndexBuildWorkerParallelism = AppSettings.NormalizeIndexBuildWorkerParallelism(v),
            0, AppSettings.MaximumIndexWorkerParallelism,
            $"Concurrent file-read/classification lanes inside one folder build. 0 = automatic from physical cores and the memory budget (currently {IndexWorkerParallelism.ResolveBuildDegree(0, Environment.ProcessorCount, IndexWorkerParallelism.DetectedPhysicalCoreCount, _viewModel.Settings.EffectiveIndexBuildMemoryBudgetMB, false, false)}). Results are committed in crawl order. Performance ▸ “Limit disk-intensive parallelism on HDDs” is authoritative: an HDD build always uses one lane.");
        AddIndexToggle(resourcesGroup, "Pause building while a search is running",
            s => s.IndexPauseDuringForegroundSearch, (s, v) => s.IndexPauseDuringForegroundSearch = v,
            "Default on. Pauses a same-volume build while a foreground search runs, then safely resumes.");
        AddIndexToggle(resourcesGroup, "Pause building on battery",
            s => s.IndexPauseOnBattery, (s, v) => s.IndexPauseOnBattery = v,
            "Default on. Pauses builds while the device is on battery, without publishing a partial index.");
        AddIndexToggle(resourcesGroup, "Automatically repair a corrupt or incompatible index",
            s => s.IndexAutoRepair, (s, v) => s.IndexAutoRepair = v,
            "Default on. Corruption always causes an immediate live-scan fallback; when on, a rebuild is also scheduled per the build trigger. When off, it is only reported.");
        AddIndexNumber(resourcesGroup, "Post-build catch-up threshold (journal changes):",
            s => s.IndexPostBuildCatchUpThresholdChanges,
            (s, v) => s.IndexPostBuildCatchUpThresholdChanges = AppSettings.NormalizeIndexPostBuildCatchUpThresholdChanges(v),
            0, AppSettings.MaximumIndexPostBuildCatchUpThresholdChanges,
            "After a full build or rebuild, Yagu counts file-system journal changes since the crawl began. Above this threshold it applies an incremental catch-up to the staged index before publishing it. 0 catches up after any non-empty interval; default 30,000.");
        AddIndexNumber(resourcesGroup, "Foreground journal catch-up limit (MB):",
            s => s.IndexMaxJournalCatchupMB,
            (s, v) => s.IndexMaxJournalCatchupMB = AppSettings.NormalizeIndexMaxJournalCatchupMB(v),
            1, 100000,
            "How much change-journal data a search will process up-front before bypassing the index for that search. Default 64.");
        AddIndexNumber(resourcesGroup, "Foreground journal catch-up limit (records):",
            s => s.IndexMaxJournalCatchupRecords,
            (s, v) => s.IndexMaxJournalCatchupRecords = AppSettings.NormalizeIndexMaxJournalCatchupRecords(v),
            1000, 100000000,
            "Companion record cap for the catch-up limit above. Default 2,000,000.");
        AddIndexToggle(resourcesGroup, "Recover from a lost change journal by rescanning instead of rebuilding",
            s => s.IndexRescanOnJournalGap, (s, v) => s.IndexRescanOnJournalGap = v,
            "Default on. Windows keeps only a limited window of file-system changes, so an index can fall outside it while Yagu is closed. Rather than rebuilding the whole folder, Yagu then asks each file for its own last-change record and re-reads only the files that actually changed \u2014 minutes instead of hours. An elevated session uses a faster whole-volume sweep automatically. When off, a lost journal forces a full rebuild.");
        AddIndexToggle(resourcesGroup, "Use file-system watcher hints for incremental updates",
            s => s.IndexUseWatcherHints, (s, v) => s.IndexUseWatcherHints = v,
            "Default off. When on, incremental updates use a file-system watcher as a low-latency hint about which folders changed. The change journal remains authoritative, so a watcher failure never affects correctness.");
        AddIndexNumber(resourcesGroup, "Maximum delta segments before compaction:",
            s => s.IndexMaxDeltaSegments,
            (s, v) => s.IndexMaxDeltaSegments = AppSettings.NormalizeIndexMaxDeltaSegments(v),
            1, 64,
            "How many incremental delta updates may stack over a base index before they are compacted into a fresh base. Range 1–64; default 8.");
        AddIndexNumber(resourcesGroup, "Compaction size threshold (MB):",
            s => s.IndexCompactionThresholdMB,
            (s, v) => s.IndexCompactionThresholdMB = AppSettings.NormalizeIndexCompactionThresholdMB(v),
            16, 8192,
            "When accumulated delta updates exceed this size, they are compacted into a fresh base index (whichever bound is hit first). Range 16–8192; default 256.");

        // ── Index Size Management ──
        // An index only ever grows on its own: each incremental update appends a delta segment. Coalescing
        // (merging a bounded contiguous run of small segments) and compaction (folding every layer into a
        // fresh base) are the only ways storage comes back, so these settings decide which of those each
        // index may use. They can never change search results — an index that stays segmented, or one held
        // at its budget, simply prunes less, and everything it cannot prove safe to skip is read live.
        AddIndexCombo(sizeGroup, "Default size-management strategy:",
            [
                (IndexSizeManagementModes.CoalesceThenCompact, "Coalesce, then compact (recommended)"),
                (IndexSizeManagementModes.Coalesce, "Coalesce small segments only (low memory)"),
                (IndexSizeManagementModes.Compact, "Compact into a fresh base only"),
                (IndexSizeManagementModes.Off, "Off — never reorganize automatically"),
            ],
            s => s.IndexSizeManagementMode,
            (s, v) => s.IndexSizeManagementMode = IndexSizeManagementModes.Normalize(v),
            "How every index reclaims storage, unless a folder overrides it under Manage Indexes ▸ Size. Coalescing merges runs of small delta segments and never loads the base, so it stays cheap on a huge index. Compaction folds the whole index into a fresh base — far more effective, but it briefly loads the index into memory, so it is capped below. Off lets an index grow until you rebuild it.");
        AddIndexNumber(sizeGroup, "Auto-compaction size cap (MB, 0 = no cap):",
            s => s.IndexMaxAutoCompactionSizeMB,
            (s, v) => s.IndexMaxAutoCompactionSizeMB = AppSettings.NormalizeIndexMaxAutoCompactionSizeMB(v),
            0, 1048576,
            "The largest total index size the automatic compaction will fold. Compaction briefly loads the whole index into memory, so above this cap a large over-segmented index is left segmented instead (searches still use it). An index above this cap can only be reclaimed by coalescing or an explicit rebuild. 0 disables the cap. Default 512.");
        AddIndexNumber(sizeGroup, "Coalescing: largest segment to merge (MB):",
            s => s.IndexCoalesceMaxSegmentMB,
            (s, v) => s.IndexCoalesceMaxSegmentMB = AppSettings.NormalizeIndexCoalesceMaxSegmentMB(v),
            1, 8192,
            "Only delta segments at or below this size join a coalescing run. Set below your typical segment size, coalescing can never find work \u2014 which leaves a large index with no way to reclaim storage. Default 256.");
        AddIndexNumber(sizeGroup, "Coalescing: largest merge batch (MB):",
            s => s.IndexCoalesceMaxBatchMB,
            (s, v) => s.IndexCoalesceMaxBatchMB = AppSettings.NormalizeIndexCoalesceMaxBatchMB(v),
            1, 32768,
            "Total size of one merge. This is the main bound on how much memory a coalescing pass uses. Keep it at or above the run minimum multiplied by the segment cap, or a full-length run can never fit. Default 1024.");
        AddIndexNumber(sizeGroup, "Coalescing: fewest segments worth merging:",
            s => s.IndexCoalesceMinRun,
            (s, v) => s.IndexCoalesceMinRun = AppSettings.NormalizeIndexCoalesceMinRun(v),
            2, 64,
            "A run must hold at least this many neighbouring eligible segments before it is merged. Higher values do less, but more useful, work per pass. Default 4.");
        AddIndexNumber(sizeGroup, "Coalescing: merges per maintenance pass:",
            s => s.IndexCoalesceMaxRunsPerPass,
            (s, v) => s.IndexCoalesceMaxRunsPerPass = AppSettings.NormalizeIndexCoalesceMaxRunsPerPass(v),
            1, 64,
            "How many runs one maintenance pass may merge. Raise it if an index accumulates segments faster than it reclaims them. Default 8.");
        // "Share aggregate index telemetry" (ShareAggregateIndexTelemetry) is a privacy/telemetry
        // opt-in, so its toggle lives on the Privacy tab (SettingsWindow.xaml.cs). It is still a CLI
        // config key (--index-config ShareAggregateIndexTelemetry=...) for parity.

        // ── Status & Provenance ──
        AddIndexToggle(presentationGroup, "Show index status in the main window",
            s => s.ShowIndexStatusInMainWindow, (s, v) => s.ShowIndexStatusInMainWindow = v,
            "Show whether a search used the index (full / partial / bypassed) in the main window. Default on.");
        AddIndexToggle(presentationGroup, "Show build progress notifications",
            s => s.ShowIndexBuildNotifications, (s, v) => s.ShowIndexBuildNotifications = v,
            "Show non-blocking notifications while a background build runs. Default on.");
        AddIndexToggle(presentationGroup, "Show a provenance glyph on each result",
            s => s.ShowIndexProvenanceInResults, (s, v) => s.ShowIndexProvenanceInResults = v,
            "Show a small glyph on each result and the preview header indicating how the file was reached (index-accelerated / live-scanned / extracted). It is candidacy provenance only — match content is always read live from the file. Only shown when the index participated. Default on.");

        var dismissedWarningSummary = Description(string.Empty);
        var restoreLiveScanWarnings = new Button
        {
            Content = "Restore live-scan warnings",
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 8, 0, 0),
        };
        void UpdateDismissedWarningSummary()
        {
            int count = _viewModel.Settings.ContentIndexLiveScanWarningDismissedRoots.Count;
            dismissedWarningSummary.Text = count == 0
                ? "No location-specific live-scan warnings are dismissed."
                : $"Warnings are dismissed for {count:N0} unindexed location{(count == 1 ? string.Empty : "s")}.";
            restoreLiveScanWarnings.IsEnabled = count > 0;
        }
        restoreLiveScanWarnings.Click += async (_, _) =>
        {
            await _viewModel.RestoreContentIndexLiveScanWarningsAsync();
            UpdateDismissedWarningSummary();
            SetIndexStatus("Live-scan warnings restored.");
        };
        presentationGroup.Children.Add(restoreLiveScanWarnings);
        presentationGroup.Children.Add(dismissedWarningSummary);
        UpdateDismissedWarningSummary();

        // ── Manage Indexes ──
        BuildIndexManagementSection(manageGroup);

        // Dependent-enabled wiring: the "use by default" toggle and build actions require the master.
        void UpdateDependentEnabled()
        {
            bool master = _viewModel.Settings.EnableContentIndex;
            useByDefaultToggle.IsEnabled = master;
            RefreshIndexManagementButtons();
        }

        masterToggle.Toggled += (_, _) =>
        {
            UpdateMasterSummary();
            UpdateDependentEnabled();
        };
        useByDefaultToggle.Toggled += (_, _) => UpdateMasterSummary();
        UpdateDependentEnabled();

        // "Restore indexing defaults" resets every Index* setting and pushes the values back into the UI.
        var restoreDefaults = new Button
        {
            Content = "Restore indexing defaults",
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 12, 0, 0),
        };
        restoreDefaults.Click += (_, _) =>
        {
            ContentIndexConfigService.Reset(_viewModel.Settings);
            foreach (var apply in reseed)
                apply();
            UpdateMasterSummary();
            UpdateDependentEnabled();
            UpdateDismissedWarningSummary();
            MarkSettingsDirty(requireValueChanges: false);
            SetIndexStatus("Indexing settings restored to defaults. Click Save to apply.");
        };
        manageGroup.Children.Add(restoreDefaults);
        manageGroup.Children.Add(Description(
            "Resets every setting on this tab to its default. Does not delete any index data — use Clear all indexes for that. Click Save afterwards to keep the defaults."));
    }
}
