using Microsoft.Extensions.Logging;
using Yagu.Services;
using Yagu.Services.Ai;
using Yagu.Services.Index;
using Yagu.Services.Logging;

namespace Yagu;

/// <summary>
/// First-run interactive console prompts for <c>--cli</c> mode that mirror the GUI startup chain
/// (<c>MainWindow.OnContentLoaded</c>), so the CLI and the GUI reach the same first-run state. Every
/// prompt is gated by the <em>same</em> persisted <see cref="AppSettings"/> flag as its GUI counterpart,
/// so answering on either surface suppresses it on the other (true parity).
/// <para>
/// Covered: telemetry/bug-report consent, search-result temp-drive location, Explorer context-menu
/// registration, index onboarding, the CPU-only AI-search warning, and the new-Foundry-model alert.
/// Deliberately excluded: the window-mode picker and font-contrast warning (GUI-window concepts with no
/// CLI meaning), plus the Everything install offer and semantic model qualification (already handled
/// elsewhere in <see cref="CliRunner"/>). The whole sequence no-ops when stdin is redirected
/// (piped/automated) so scripted runs are never blocked on a prompt.
/// </para>
/// </summary>
internal static class CliFirstRunPrompts
{
    public static async Task RunAsync(AppSettings settings, SettingsService settingsService)
    {
        // Never block an automated/piped invocation on an interactive prompt.
        if (Console.IsInputRedirected)
        {
            YaguLog.For("CliFirstRun").LogDebug("stdin is redirected (piped/automated run); skipping first-run prompts.");
            return;
        }

        YaguLog.For("CliFirstRun").LogDebug("Running interactive first-run CLI prompts.");
        try
        {
            PromptTelemetryConsent(settings, settingsService);
            PromptResultStoreTempLocation(settings, settingsService);
            PromptContextMenu(settings, settingsService);
            PromptCpuSemanticWarning(settings, settingsService);
            PromptIndexOnboarding(settings, settingsService);
            await CheckForNewFoundryModelsAsync(settings, settingsService).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A first-run prompt must never abort the user's search.
            YaguLog.For("CliFirstRun").LogWarning(ex, "First-run prompt failed: {Error}", ex.Message);
        }
    }

    // ── Telemetry / bug-report consent (mirrors ShowTelemetryConsentIfNeededAsync) ──

    private static void PromptTelemetryConsent(AppSettings settings, SettingsService service)
    {
        if (settings.TelemetryConsentPromptShown)
        {
            YaguLog.For("CliFirstRun").LogDebug("Telemetry consent already shown; skipping prompt.");
            return;
        }

        Console.Error.WriteLine();
        Console.Error.WriteLine("Help improve Yagu?");
        Console.Error.WriteLine("Yagu can share anonymized error/performance telemetry and enable one-tap bug reports.");
        Console.Error.WriteLine("Paths, queries, and file contents are never included, and nothing is sent without consent.");
        Console.Error.Write("Enable this? [y/N] ");
        bool enabled = ReadYesNo(defaultYes: false);

        settings.TelemetryConsentPromptShown = true;
        settings.TelemetryEnabled = enabled;
        settings.BugReportingEnabled = enabled;
        if (enabled && string.IsNullOrEmpty(settings.TelemetryInstallId))
            settings.TelemetryInstallId = Guid.NewGuid().ToString("N");
        service.Save(settings);
        YaguLog.For("CliFirstRun").LogInformation("Telemetry consent answered: telemetry/bug-reporting {State}.", enabled ? "enabled" : "declined");

        Console.Error.WriteLine(enabled
            ? "Thanks! Telemetry and bug reports are enabled (change it any time in Settings)."
            : "Telemetry stays off. You can enable it later in Settings.");
    }

    // ── Search-result temp-drive location (mirrors CheckFirstRunResultStoreTempLocationAsync) ──

    private static void PromptResultStoreTempLocation(AppSettings settings, SettingsService service)
    {
        if (settings.HasChosenSearchResultTempDirectory &&
            ResultStoreTempLocationService.IsUsableTempDirectory(settings.SearchResultTempDirectory, requireMinimumFreeSpace: false))
        {
            YaguLog.For("CliFirstRun").LogDebug("Search-result temp directory already chosen and usable; skipping prompt.");
            return;
        }

        string? launchDrive = ResultStoreTempLocationService.GetLaunchDriveRoot();
        var options = ResultStoreTempLocationService.GetWritableDriveOptions(launchDrive);
        if (options.Count == 0)
        {
            YaguLog.For("CliFirstRun").LogDebug("No writable drive options for search-result temp files; skipping prompt.");
            return;
        }

        var preferred = ResultStoreTempLocationService.ChoosePreferredOption(options, settings.SearchResultTempDirectory, launchDrive)
                        ?? options[0];
        int defaultIndex = 0;
        for (int i = 0; i < options.Count; i++)
            if (ReferenceEquals(options[i], preferred)) { defaultIndex = i; break; }

        Console.Error.WriteLine();
        Console.Error.WriteLine("Where should Yagu store temporary search-result files?");
        Console.Error.WriteLine("Large searches page results to disk; pick a drive with plenty of free space.");
        for (int i = 0; i < options.Count; i++)
            Console.Error.WriteLine($"  [{i + 1}] {options[i].DisplayName}");
        Console.Error.Write($"Choose a drive [1-{options.Count}] (default {defaultIndex + 1}, or Enter 's' to skip): ");

        string? answer = Console.ReadLine()?.Trim();
        if (string.Equals(answer, "s", StringComparison.OrdinalIgnoreCase))
        {
            YaguLog.For("CliFirstRun").LogDebug("Search-result temp location prompt skipped; will re-offer next launch.");
            return; // skipped — re-offer next launch (matches the GUI cancel behavior)
        }

        ResultStoreTempDriveOption chosen;
        if (string.IsNullOrWhiteSpace(answer))
            chosen = options[defaultIndex];
        else if (int.TryParse(answer, out int pick) && pick >= 1 && pick <= options.Count)
            chosen = options[pick - 1];
        else
            chosen = options[defaultIndex];

        settings.SearchResultTempDirectory = chosen.TempDirectory;
        settings.HasChosenSearchResultTempDirectory = true;
        service.Save(settings);
        YaguLog.For("CliFirstRun").LogInformation("Search-result temp directory set to '{TempDirectory}'.", chosen.TempDirectory);
        Console.Error.WriteLine($"Temporary search-result files will be stored under {chosen.TempDirectory}.");
    }

    // ── Explorer context-menu registration (mirrors CheckFirstRunContextMenuAsync) ──

    private static void PromptContextMenu(AppSettings settings, SettingsService service)
    {
        if (settings.HasCompletedFirstRun)
        {
            YaguLog.For("CliFirstRun").LogDebug("First run already completed; skipping context-menu prompt.");
            return;
        }

        // Mark first run complete regardless of the choice (mirrors the GUI), then persist.
        settings.HasCompletedFirstRun = true;
        service.Save(settings);

        if (ExplorerContextMenu.IsRegistered())
        {
            YaguLog.For("CliFirstRun").LogDebug("Explorer context menu already registered; not prompting.");
            return;
        }

        Console.Error.WriteLine();
        Console.Error.WriteLine("Add a \"Search with Yagu\" entry to the Windows Explorer right-click menu?");
        Console.Error.WriteLine("This lets you search any folder by right-clicking it.");
        Console.Error.Write("Add it? [y/N] ");
        if (!ReadYesNo(defaultYes: false))
            return;

        try
        {
            ExplorerContextMenu.Register();
            YaguLog.For("CliFirstRun").LogInformation("Explorer context menu registered from the first-run prompt.");
            Console.Error.WriteLine("Added. Right-click any folder in Explorer and choose \"Search with Yagu\".");
        }
        catch (Exception ex)
        {
            YaguLog.For("CliFirstRun").LogWarning(ex, "Failed to register the Explorer context menu: {Error}", ex.Message);
            Console.Error.WriteLine($"Could not register the context menu: {ex.Message}");
        }
    }

    // ── CPU-only AI-search warning (mirrors ShowCpuSemanticWarningIfNeededAsync) ──

    private static void PromptCpuSemanticWarning(AppSettings settings, SettingsService service)
    {
        // Gate mirrors MainViewModel.ShouldShowCpuSemanticWarning: AI search enabled, no GPU/NPU, once only.
        if (!settings.SemanticSearchEnabled || settings.CpuSemanticWarningShown)
        {
            YaguLog.For("CliFirstRun").LogDebug("CPU AI-search warning not applicable (AI search off or already shown); skipping.");
            return;
        }
        if (HasHardwareAcceleration())
        {
            YaguLog.For("CliFirstRun").LogDebug("GPU/NPU detected; skipping the CPU AI-search warning.");
            return;
        }

        settings.CpuSemanticWarningShown = true;

        Console.Error.WriteLine();
        Console.Error.WriteLine("AI (Semantic) search will run on your CPU.");
        Console.Error.WriteLine("No compatible GPU/NPU was detected, so AI search may be slow and results may vary.");
        Console.Error.Write("Keep AI search available? (answering No makes Traditional search the default) [Y/n] ");
        bool keepAi = ReadYesNo(defaultYes: true);

        if (keepAi)
        {
            // Mirror DismissCpuSemanticWarningAsync(useTraditionalDefault: false).
            settings.DefaultToTraditionalSearchMode = false;
            settings.HasChosenQueryMode = true;
            Console.Error.WriteLine("Keeping AI search available (use --semantic-pattern to run it).");
        }
        else
        {
            // Mirror DismissCpuSemanticWarningAsync(useTraditionalDefault: true): turn AI search off.
            settings.SemanticSearchEnabled = false;
            settings.DefaultToTraditionalSearchMode = true;
            Console.Error.WriteLine("AI search turned off. You can re-enable it in Settings \u25b8 AI.");
        }
        service.Save(settings);
        YaguLog.For("CliFirstRun").LogInformation("CPU AI-search warning answered: {Outcome}.", keepAi ? "kept AI search available" : "disabled AI search (traditional default)");
    }

    // ── Index onboarding (mirrors CheckFirstRunIndexOnboardingAsync + AddFoldersToIndexAndBuildAsync) ──

    private static void PromptIndexOnboarding(AppSettings settings, SettingsService service)
    {
        if (settings.HasPromptedIndexOnboarding)
        {
            YaguLog.For("CliFirstRun").LogDebug("Index onboarding already prompted; skipping.");
            return;
        }

        if (string.IsNullOrWhiteSpace(settings.IndexStorageDirectory)
            && DefaultContentIndexPathProvider.TryGetPreservedStorageDirectory(out string preservedStorageDirectory))
        {
            settings.IndexStorageDirectory = preservedStorageDirectory;
            service.Save(settings);
            DefaultContentIndexPathProvider.ClearPreservedStorageDirectory();
        }

        if (settings.IndexedRoots.Count > 0)
        {
            settings.HasPromptedIndexOnboarding = true;
            service.Save(settings);
            YaguLog.For("CliFirstRun").LogDebug("Registered content-index roots found; skipping first-run onboarding.");
            return;
        }

        IReadOnlyList<string> reusableRoots = new ContentIndexManager(
            DefaultContentIndexPathProvider.Create(settings.IndexStorageDirectory),
            settings.IndexRetainedGenerationCount).GetReusableStoredIndexRoots();
        if (reusableRoots.Count > 0)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("Existing content indexes found:");
            foreach (string root in reusableRoots)
                Console.Error.WriteLine($"  {root}");
            Console.Error.Write("Use these indexes again without rebuilding them? [Y/n] ");
            if (ReadYesNo(defaultYes: true))
            {
                foreach (string root in reusableRoots)
                    settings.IndexedRoots = IndexedRootsPolicy.Add(settings.IndexedRoots, root);
                settings.EnableContentIndex = true;
                settings.UseContentIndexByDefault = true;
                settings.HasPromptedIndexOnboarding = true;
                service.Save(settings);
                Console.Error.WriteLine("Existing content indexes restored.");
                return;
            }

            Console.Error.WriteLine("Continuing with content-index setup. Existing index data is left untouched.");
        }

        // Mark shown regardless of the choice so it never nags again (mirrors the GUI).
        settings.HasPromptedIndexOnboarding = true;
        service.Save(settings);

        Console.Error.WriteLine();
        Console.Error.WriteLine("Speed up searches with a content index?");
        Console.Error.WriteLine("Yagu can index folders you search often so future searches skip files that cannot match.");
        Console.Error.WriteLine("Matching files are always still read live from disk.");
        Console.Error.WriteLine("Enter one or more folder paths to index (one per line). Leave blank to finish or skip.");

        // Collect one or more folders (parity with the GUI's multi-select onboarding dialog).
        var effectiveRoots = new List<string>();
        int entryNumber = 1;
        while (true)
        {
            Console.Error.Write($"  Folder #{entryNumber} (blank to finish): ");
            string? folder = Console.ReadLine()?.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(folder))
                break;
            if (!Directory.Exists(folder))
            {
                YaguLog.For("CliFirstRun").LogWarning("Index onboarding folder not found: '{Folder}'.", folder);
                Console.Error.WriteLine($"    Folder not found: {folder}");
                continue;
            }

            string? existingCover = IndexedRootsPolicy.FindBestCoveringRoot(settings.IndexedRoots, folder);
            if (existingCover is not null)
            {
                Console.Error.WriteLine($"    {folder} is already covered by indexed root {existingCover}; not added again.");
                continue;
            }

            settings.IndexedRoots = IndexedRootsPolicy.Add(settings.IndexedRoots, folder);
            string effectiveRoot = IndexedRootsPolicy.FindBestCoveringRoot(settings.IndexedRoots, folder) ?? folder;
            if (!effectiveRoots.Contains(effectiveRoot, StringComparer.OrdinalIgnoreCase))
            {
                effectiveRoots.Add(effectiveRoot);
                Console.Error.WriteLine($"    Added {effectiveRoot}.");
            }
            entryNumber++;
        }

        if (effectiveRoots.Count == 0)
        {
            YaguLog.For("CliFirstRun").LogDebug("Index onboarding skipped (no folder entered).");
            return;
        }

        // Opt in: turn the master feature on and default the per-search toggle on.
        settings.EnableContentIndex = true;
        settings.UseContentIndexByDefault = true;

        // Build trigger(s): the same combinable choices as the GUI dialog / Settings ▸ Indexing.
        settings.IndexBuildTrigger = PromptIndexBuildTrigger(settings.IndexBuildTrigger);
        service.Save(settings);

        Console.Error.WriteLine($"Building content index for {effectiveRoots.Count} folder(s)\u2026 (this can take a while for large folders)");
        using var buildCts = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = true; buildCts.Cancel(); };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            var coordinator = new IndexBuildCoordinator();
            foreach (string effectiveRoot in effectiveRoots)
            {
                if (buildCts.IsCancellationRequested)
                    break;
                YaguLog.For("CliFirstRun").LogInformation(
                    "Index onboarding: registered effective root '{EffectiveRoot}' and starting a synchronous build.",
                    effectiveRoot);
                Console.Error.WriteLine($"  Indexing {effectiveRoot}\u2026");
                IndexBuildOperation operation = IndexBuildOperationFactory.CreateBuild(settings, effectiveRoot, rebuild: false);
                IndexBuildSuccess result = coordinator.BuildFullScopePreferWorkerAsync(
                    operation, settings.IndexUseNativeWorker, buildCts.Token).GetAwaiter().GetResult();
                YaguLog.For("CliFirstRun").LogInformation("Onboarding index build complete for '{Folder}': {Summary}", effectiveRoot, result.Summary);
                Console.Error.WriteLine($"  Content index built for {effectiveRoot}: {result.Summary}");
                if (result.PdfStatus is not null)
                    Console.Error.WriteLine($"    PDF-text index: {result.PdfStatus} ({result.PdfAdmitted}/{result.PdfsSeen} PDF(s)).");
            }
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Index build cancelled. The previous index (if any) is unchanged.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Index build failed: {ex.Message} (the folder is registered; build it later with --build-index).");
            YaguLog.For("CliFirstRun").LogWarning(ex, "Onboarding index build failed: {Error}", ex.Message);
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    /// <summary>Prompts for which automatic build trigger(s) keep the new indexes up to date, mirroring
    /// the GUI onboarding dialog. Returns the normalized combined trigger value ("Manual" if none).</summary>
    private static string PromptIndexBuildTrigger(string currentTrigger)
    {
        var options = new (string Flag, string Display)[]
        {
            ("AtStartup", "When Yagu starts"),
            ("WhenIdle", "When the machine is idle"),
            ("Continuous", "Continuously while Yagu is open"),
            ("OnSchedule", "On a schedule (configure in Settings)"),
        };

        Console.Error.WriteLine();
        Console.Error.WriteLine("Keep the index up to date automatically? Choose trigger(s):");
        for (int i = 0; i < options.Length; i++)
            Console.Error.WriteLine($"  [{i + 1}] {options[i].Display}");
        Console.Error.Write($"Enter number(s) separated by commas (e.g. 1,3), or blank for Manual: ");

        string? answer = Console.ReadLine()?.Trim();
        if (string.IsNullOrWhiteSpace(answer))
        {
            Console.Error.WriteLine("Indexing is Manual — it only runs when you ask (change it in Settings \u25b8 Indexing).");
            return AppSettings.NormalizeIndexBuildTrigger(string.Empty);
        }

        var selected = new List<string>();
        foreach (string token in answer.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(token, out int pick) && pick >= 1 && pick <= options.Length)
                selected.Add(options[pick - 1].Flag);
        }
        string trigger = AppSettings.NormalizeIndexBuildTrigger(string.Join(",", selected));
        Console.Error.WriteLine($"Build trigger set to: {trigger}.");
        return trigger;
    }

    // ── New Foundry model alert (mirrors CheckForNewFoundryModelsAsync) ──

    private static async Task CheckForNewFoundryModelsAsync(AppSettings settings, SettingsService service)
    {
        // Cheap self-gates first (mirror the VM): alerts on, AI enabled, user has already used semantic
        // search, and not checked within the throttle window. Only then pay for a catalog query.
        if (!settings.FoundryModelUpdateAlertsEnabled || !settings.SemanticSearchEnabled || !settings.SemanticModelDownloaded)
        {
            YaguLog.For("CliFirstRun").LogDebug("New-Foundry-model check gated off (alerts disabled, AI search off, or no model downloaded).");
            return;
        }
        if (!FoundryModelUpdateChecker.ShouldCheck(
                settings.LastFoundryModelCheckUtc, DateTimeOffset.UtcNow, FoundryModelUpdateChecker.DefaultCheckInterval))
        {
            YaguLog.For("CliFirstRun").LogDebug("New-Foundry-model check throttled (checked recently); skipping.");
            return;
        }

        try
        {
            string? modelAlias = string.IsNullOrWhiteSpace(settings.SemanticModelAlias) ? null : settings.SemanticModelAlias;
            await using var translator = new FoundryLocalSemanticQueryTranslator(enabled: true, modelOverrideAlias: modelAlias);
            ApplyDetectedAccelerators(translator);
            if (!translator.IsAvailable)
            {
                YaguLog.For("CliFirstRun").LogDebug("Foundry Local not available; skipping the new-model check.");
                return;
            }

            var options = await translator.ListModelOptionsAsync(null, CancellationToken.None).ConfigureAwait(false);
            var current = options
                .Where(o => !string.IsNullOrEmpty(o.Id))
                .Select(o => new FoundryModelDescriptor(o.Id!, o.Alias, o.DeviceLabel, o.SizeBytes))
                .ToList();

            // An empty/failed catalog query must not clobber the baseline.
            if (current.Count == 0)
                return;

            bool hasBaseline = settings.LastFoundryModelCheckUtc is not null || settings.KnownFoundryModelIds.Count > 0;
            var result = FoundryModelUpdateChecker.Detect(settings.KnownFoundryModelIds, current, hasBaseline);

            settings.KnownFoundryModelIds = result.CurrentIds.ToList();
            settings.LastFoundryModelCheckUtc = DateTimeOffset.UtcNow;
            if (result.Changes.Count > 0)
                settings.LastFoundryModelAlertUtc = DateTimeOffset.UtcNow;
            service.Save(settings);

            if (result.Changes.Count == 0)
            {
                YaguLog.For("CliFirstRun").LogDebug("New-Foundry-model check: no new models since last check.");
                return;
            }

            YaguLog.For("CliFirstRun").LogInformation("New-Foundry-model check: {ChangeCount} change(s) detected.", result.Changes.Count);
            Console.Error.WriteLine();
            Console.Error.WriteLine(result.Changes.Count == 1
                ? "A new on-device AI model is available for AI (Semantic) search:"
                : $"{result.Changes.Count} new on-device AI models are available for AI (Semantic) search:");
            foreach (var change in result.Changes)
            {
                var parts = new List<string> { change.Alias };
                if (!string.IsNullOrWhiteSpace(change.DeviceLabel)) parts.Add(change.DeviceLabel!);
                string tag = change.Kind == FoundryModelChangeKind.New ? "new" : "updated";
                Console.Error.WriteLine($"  \u2022 {string.Join("  \u00b7  ", parts)}  [{tag}]");
            }
            Console.Error.WriteLine("Manage models in Settings \u25b8 AI, or pass --semantic-model <alias> to the CLI.");
        }
        catch (Exception ex)
        {
            // Offline / Foundry unavailable — leave the baseline unchanged and stay quiet.
            YaguLog.For("CliFirstRun").LogWarning(ex, "Foundry model update check failed: {Error}", ex.Message);
        }
    }

    // ── Helpers ──

    /// <summary>Reads a yes/no answer from the console. Empty input returns <paramref name="defaultYes"/>.</summary>
    private static bool ReadYesNo(bool defaultYes)
    {
        string? answer = Console.ReadLine();
        Console.Error.WriteLine();
        if (string.IsNullOrWhiteSpace(answer))
            return defaultYes;
        char c = char.ToLowerInvariant(answer.Trim()[0]);
        return c == 'y';
    }

    /// <summary>True when a GPU or NPU is detected (so AI search would be hardware-accelerated). CPU-only
    /// fallback on any detection failure, matching <c>MainViewModel.SafeDetectAcceleratedHardware</c>.</summary>
    private static bool HasHardwareAcceleration()
    {
        try
        {
            var capability = new GpuNpuCapabilityDetector();
            return capability.HasGpu() || capability.HasNpu();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Tells the translator which accelerators exist so AUTO model selection matches the GUI (and
    /// never picks a GPU/NPU build on CPU-only hardware). Detection failure falls back to CPU-only.</summary>
    private static void ApplyDetectedAccelerators(FoundryLocalSemanticQueryTranslator translator)
    {
        bool hasGpu = false, hasNpu = false;
        long gpuMemoryBytes = 0;
        try
        {
            var capability = new GpuNpuCapabilityDetector();
            hasGpu = capability.HasGpu();
            hasNpu = capability.HasNpu();
            gpuMemoryBytes = capability.GetMaxDedicatedGpuMemoryBytes();
        }
        catch { /* CPU-only fallback */ }
        translator.SetAvailableAccelerators(hasGpu, hasNpu);
        translator.SetGpuMemoryBytes(gpuMemoryBytes);
    }
}
