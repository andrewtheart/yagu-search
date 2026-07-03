using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using Yagu.Models;
using Yagu.Services;
using Yagu.Services.Ai;

namespace Yagu;

/// <summary>
/// Implements <c>--cli</c> mode: attaches to the parent console, parses command-line
/// arguments, loads settings (current-directory <c>.yagu.json</c> → process launch-directory
/// <c>.yagu.json</c> → global AppData → CLI overrides),
/// and streams search results to stdout in ripgrep-compatible format while writing
/// warnings and the completion summary to stderr.
/// </summary>
internal static class CliRunner
{
    private const string LocalSettingsFileName = ".yagu.json";

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateFile(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        nint lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, nint hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetStdHandle(uint nStdHandle, nint hHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetStdHandle(uint nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(nint hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(nint hConsoleHandle, uint dwMode);

    [DllImport("kernel32.dll")]
    private static extern uint GetFileType(nint hFile);

    private const uint FILE_TYPE_CHAR = 0x0002; // interactive console handle

    private const uint GENERIC_WRITE    = 0x40000000;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING    = 3;
    private const uint STD_OUTPUT_HANDLE = unchecked((uint)-11);
    private const uint STD_ERROR_HANDLE  = unchecked((uint)-12);
    private const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;
    private const uint ENABLE_PROCESSED_OUTPUT            = 0x0001;

    // -----------------------------------------------------------------------
    // Public entry-point
    // -----------------------------------------------------------------------

    /// <summary>
    /// Attaches to the parent console and prints help text. Called from Program.Main
    /// when a help flag is detected without requiring --cli.
    /// </summary>
    public static void RunHelp()
    {
        EnsureConsole();
        PrintHelp();
    }

    public static int Run(string[] rawArgs)
    {
        bool vtEnabled = EnsureConsole();

        var args = CliArgs.Parse(rawArgs);

        if (args.ShowHelp)
        {
            PrintHelp();
            return 0;
        }

        // --load-session short-circuits the entire search pipeline: just
        // read the file and re-emit results in ripgrep-compatible format.
        if (!string.IsNullOrWhiteSpace(args.LoadSessionPath))
            return RunLoadSession(args.LoadSessionPath!, vtEnabled);

        // --semantic-batch: translate a file of natural-language queries through a single loaded model
        // and print one delimited --explain block per query (evaluation/benchmark harness). This is a
        // dry-run — it never executes a search — so it short-circuits the pipeline like --load-session.
        if (!string.IsNullOrWhiteSpace(args.SemanticBatch))
        {
            var batchSettings = LoadEffectiveSettings(args);
            return RunSemanticBatchAsync(args, batchSettings).GetAwaiter().GetResult();
        }

        // --semantic-pattern: translate the natural-language request into search flags via the
        // local model, folding the result into `args` before the usual validation runs.
        if (!string.IsNullOrWhiteSpace(args.SemanticPattern))
        {
            var semanticSettings = LoadEffectiveSettings(args);
            var (semCode, stop) = RunSemanticAsync(args, semanticSettings).GetAwaiter().GetResult();
            if (stop) return semCode;
        }

        // An omitted --directory means "search all drives". A specified directory must exist.
        if (!string.IsNullOrWhiteSpace(args.Directory) && !Directory.Exists(args.Directory))
        {
            WriteError($"error: directory does not exist: {args.Directory}");
            return 2;
        }

        if (string.IsNullOrWhiteSpace(args.Pattern))
        {
            WriteError("error: a search pattern is required (positional arg or --pattern).");
            WriteError("usage: Yagu.exe --cli --directory <path> PATTERN [options]");
            return 2;
        }

        WarnIfNotAdmin(args);

        if (vtEnabled)
            OfferEverythingSetupAsync().GetAwaiter().GetResult();

        var settings = LoadEffectiveSettings(args);

        // Apply CLI overrides to settings used outside of SearchOptions.
        if (args.LogLevelIndex.HasValue)         settings.LogLevelIndex = args.LogLevelIndex.Value;
        if (args.ConsoleLogLevelIndex.HasValue)  settings.ConsoleLogLevelIndex = args.ConsoleLogLevelIndex.Value;
        if (args.FileListerBackendIndex.HasValue) settings.FileListerBackendIndex = args.FileListerBackendIndex.Value;

        // Configure the file-lister backend from settings (same as App() constructor).
        FileLister.Backend = (FileListerBackend)settings.FileListerBackendIndex;
        LogService.InitFromSettings((LogLevel)settings.LogLevelIndex, LogLevel.Critical);

        // Seed the OCR download consent gate from settings and register a console-based warning so
        // image-text (OCR) search never downloads the engine/models without explicit consent.
        Yagu.Services.Ocr.OcrDownloadGate.ConsentGranted = settings.OcrDownloadConsented;
        Yagu.Services.Ocr.OcrDownloadGate.PromptAsync =
            requirement => Task.FromResult(ConfirmOcrDownloadOnConsole(requirement, args, vtEnabled));

        // Headless mode: never emit telemetry or surface the bug-report modal from the CLI, regardless
        // of persisted consent (those are interactive-app features only).
        Yagu.Services.Telemetry.TelemetryGate.Headless = true;

        var perRootOptions = BuildPerRootSearchOptions(args, settings);
        if (perRootOptions.Count == 0)
        {
            WriteError("error: no drives are available to search.");
            return 2;
        }

        return RunSearchAsync(perRootOptions, args, vtEnabled).GetAwaiter().GetResult();
    }

    // -----------------------------------------------------------------------
    // Semantic translation (--semantic-pattern)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Runs the local model to translate <see cref="CliArgs.SemanticPattern"/> into concrete search
    /// flags and folds them into <paramref name="args"/>. Returns <c>Stop=true</c> when the caller
    /// should return <c>Code</c> immediately (a failure, or an <c>--explain</c> dry-run); otherwise
    /// the normal search pipeline continues with the populated args.
    /// </summary>
    private static async Task<(int Code, bool Stop)> RunSemanticAsync(CliArgs args, AppSettings settings)
    {
        if (!settings.SemanticSearchEnabled)
        {
            WriteError("error: semantic search is disabled (SemanticSearchEnabled = false in settings).");
            return (2, true);
        }

        bool explicitModel = !string.IsNullOrWhiteSpace(args.SemanticModel);
        string? modelAlias = explicitModel
            ? args.SemanticModel
            : (string.IsNullOrWhiteSpace(settings.SemanticModelAlias) ? null : settings.SemanticModelAlias);

        await using var translator = new FoundryLocalSemanticQueryTranslator(enabled: true, modelOverrideAlias: modelAlias);

        // Match the GUI: never select a GPU/NPU model build on a machine that lacks one (a DirectML
        // "generic-gpu" build can load yet crash during inference on CPU-only hardware). Detection
        // failure falls back to CPU-only, the safe choice.
        bool cliHasGpu = false, cliHasNpu = false;
        try { var capability = new GpuNpuCapabilityDetector(); cliHasGpu = capability.HasGpu(); cliHasNpu = capability.HasNpu(); } catch { /* CPU-only fallback */ }
        translator.SetAvailableAccelerators(cliHasGpu, cliHasNpu);

        var context = new SemanticTranslationContext
        {
            Now = DateTimeOffset.Now,
            DefaultDirectory = !string.IsNullOrWhiteSpace(args.Directory) ? args.Directory : Environment.CurrentDirectory,
            OriginalQuery = args.SemanticPattern?.Trim(),
            // Drop a model-hallucinated directory that doesn't exist instead of failing the search.
            DirectoryExists = static d => Directory.Exists(d),
        };

        // Progress goes to stderr so stdout stays ripgrep-clean.
        string? lastProgress = null;
        var progress = new Progress<SemanticTranslationProgress>(p =>
        {
            string msg = p.Message;
            if (!string.Equals(msg, lastProgress, StringComparison.Ordinal)) { WriteError(msg); lastProgress = msg; }
        });

        // First run: offer to pick/download a local model (mirrors the GUI download modal). Skipped when
        // an explicit --semantic-model was given (consent implied) or a model was already downloaded.
        if (!explicitModel && !settings.SemanticModelDownloaded)
        {
            var setup = await EnsureSemanticModelReadyAsync(translator, args, settings, progress, CancellationToken.None)
                .ConfigureAwait(false);
            switch (setup)
            {
                case SemanticModelSetup.Ready:
                    break; // model downloaded + persisted — continue to translate
                case SemanticModelSetup.Declined:
                    return FallBackToTraditional(args);
                default: // Failed
                    return (2, true);
            }
        }

        SemanticTranslationResult result;
        try
        {
            result = await translator.TranslateAsync(args.SemanticPattern!.Trim(), context, progress, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            WriteError($"error: semantic translation failed: {ex.Message}");
            return (2, true);
        }

        if (!result.Success || result.Plan is null)
        {
            if (args.Explain)
            {
                WriteError($"model: {translator.SelectedModelAlias ?? "(unknown)"}");
                if (!string.IsNullOrWhiteSpace(result.RawModelOutput))
                {
                    WriteError("raw model output:");
                    WriteError(result.RawModelOutput);
                }
            }
            WriteError($"error: {result.Error ?? "could not interpret the request."}");
            return (2, true);
        }

        var resolved = SemanticPlanApplier.Resolve(result.Plan, context);
        args.ApplySemanticOverlay(SemanticPlanApplier.ToOverlay(resolved));

        foreach (var w in resolved.Warnings)
            WriteError($"warning: {w}");

        if (args.Explain)
        {
            WriteError($"model: {translator.SelectedModelAlias ?? "(unknown)"}");
            if (!string.IsNullOrWhiteSpace(result.RawModelOutput))
            {
                WriteError("raw model output:");
                WriteError(result.RawModelOutput);
            }
            PrintSemanticExplanation(args, resolved);
            return (0, true); // dry-run: stop before searching
        }

        if (!string.IsNullOrWhiteSpace(args.Pattern) || resolved.SearchMode is not null)
            WriteError($"interpreted: {SemanticPlanApplier.BuildExplanation(resolved)}");

        return (0, false);
    }

    // -----------------------------------------------------------------------
    // Semantic batch evaluation (--semantic-batch)
    // -----------------------------------------------------------------------

    private const string BatchQueryMarker = "===QUERY===";
    private const string BatchEndMarker   = "===END===";

    /// <summary>
    /// Translates a file of natural-language queries (one per line; blank lines and lines starting with
    /// '#' are ignored) through a SINGLE loaded model, emitting one delimited <c>--explain</c> block per
    /// query to stdout. The model is loaded once and reused for every query, so a whole query set — or a
    /// sweep across many models — can be evaluated without paying the cold-load cost on every call.
    /// Always a dry-run: no search is executed. Progress and per-query errors go to stderr; the plan
    /// blocks (which the benchmark harness parses) go to stdout.
    /// </summary>
    private static async Task<int> RunSemanticBatchAsync(CliArgs args, AppSettings settings)
    {
        if (!settings.SemanticSearchEnabled)
        {
            WriteError("error: semantic search is disabled (SemanticSearchEnabled = false in settings).");
            return 2;
        }

        string batchPath = args.SemanticBatch!;
        if (!File.Exists(batchPath))
        {
            WriteError($"error: batch query file does not exist: {batchPath}");
            return 2;
        }

        var queries = File.ReadAllLines(batchPath)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToList();
        if (queries.Count == 0)
        {
            WriteError($"error: batch query file contains no queries: {batchPath}");
            return 2;
        }

        string? modelAlias = string.IsNullOrWhiteSpace(args.SemanticModel)
            ? (string.IsNullOrWhiteSpace(settings.SemanticModelAlias) ? null : settings.SemanticModelAlias)
            : args.SemanticModel;

        await using var translator = new FoundryLocalSemanticQueryTranslator(enabled: true, modelOverrideAlias: modelAlias);

        // Match the GUI/CLI: never pick a GPU/NPU build on hardware that lacks one.
        bool cliHasGpu = false, cliHasNpu = false;
        try { var capability = new GpuNpuCapabilityDetector(); cliHasGpu = capability.HasGpu(); cliHasNpu = capability.HasNpu(); } catch { /* CPU-only fallback */ }
        translator.SetAvailableAccelerators(cliHasGpu, cliHasNpu);

        string? lastProgress = null;
        var progress = new Progress<SemanticTranslationProgress>(p =>
        {
            if (!string.Equals(p.Message, lastProgress, StringComparison.Ordinal)) { WriteError(p.Message); lastProgress = p.Message; }
        });

        string directory = !string.IsNullOrWhiteSpace(args.Directory) ? args.Directory! : Environment.CurrentDirectory;
        var o = Console.Out;
        int failures = 0;

        for (int q = 0; q < queries.Count; q++)
        {
            string query = queries[q];
            WriteError($"[{q + 1}/{queries.Count}] {query}");

            var context = new SemanticTranslationContext
            {
                Now = DateTimeOffset.Now,
                DefaultDirectory = directory,
                OriginalQuery = query,
                DirectoryExists = static d => Directory.Exists(d),
            };

            o.WriteLine($"{BatchQueryMarker} {query}");
            o.WriteLine($"  model          : {translator.SelectedModelAlias ?? modelAlias ?? "(auto)"}");
            try
            {
                var result = await translator.TranslateAsync(query, context, progress, CancellationToken.None).ConfigureAwait(false);
                if (!result.Success || result.Plan is null)
                {
                    o.WriteLine($"  error          : {result.Error ?? "could not interpret the request."}");
                }
                else
                {
                    var resolved = SemanticPlanApplier.Resolve(result.Plan, context);
                    PrintSemanticBatchPlan(o, directory, resolved);
                }
            }
            catch (Exception ex)
            {
                o.WriteLine($"  error          : {ex.GetType().Name}: {ex.Message}");
                failures++;
            }
            o.WriteLine(BatchEndMarker);
            o.Flush();
        }

        WriteError($"batch complete: {queries.Count} queries, {failures} hard failures, model='{translator.SelectedModelAlias ?? modelAlias ?? "(auto)"}'.");
        return 0;
    }

    /// <summary>
    /// Prints the resolved plan fields for one batch query in the same key layout as
    /// <see cref="PrintSemanticExplanation"/> (so the benchmark harness can parse either), reading
    /// straight from the <see cref="ResolvedSearchPlan"/> rather than mutating a shared args instance.
    /// </summary>
    private static void PrintSemanticBatchPlan(TextWriter o, string directory, ResolvedSearchPlan resolved)
    {
        o.WriteLine($"  summary        : {SemanticPlanApplier.BuildExplanation(resolved, directory)}");
        o.WriteLine($"  directory      : {resolved.Directory ?? directory}");
        o.WriteLine($"  pattern        : {(string.IsNullOrEmpty(resolved.Pattern) ? "(none)" : resolved.Pattern)}");
        if (resolved.SearchMode is { } sm)        o.WriteLine($"  search-mode    : {sm}");
        if (resolved.UseRegex is { } rx)          o.WriteLine($"  regex          : {rx}");
        if (resolved.CaseSensitive is { } cs)     o.WriteLine($"  case-sensitive : {cs}");
        if (resolved.ExactMatch is { } em)        o.WriteLine($"  exact-match    : {em}");
        if (resolved.IncludeGlobs is { } inc)     o.WriteLine($"  include        : {string.Join(", ", inc)}");
        if (resolved.ExcludeGlobs is { } exc)     o.WriteLine($"  exclude        : {string.Join(", ", exc)}");
        if (resolved.MinFileSizeBytes is { } mn)  o.WriteLine($"  min-size       : {mn:N0} bytes");
        if (resolved.MaxFileSizeBytes is { } mx)  o.WriteLine($"  max-size       : {mx:N0} bytes");
        if (resolved.CreatedAfterDate is { } ca)  o.WriteLine($"  created-after  : {ca:yyyy-MM-dd}");
        if (resolved.CreatedBeforeDate is { } cb) o.WriteLine($"  created-before : {cb:yyyy-MM-dd}");
        if (resolved.ModifiedAfterDate is { } ma) o.WriteLine($"  modified-after : {ma:yyyy-MM-dd}");
        if (resolved.ModifiedBeforeDate is { } mb)o.WriteLine($"  modified-before: {mb:yyyy-MM-dd}");
        if (resolved.SortModeIndex is { } smi)
        {
            string sortKey = smi switch { 1 => "matches", 2 => "date", 3 => "size", 4 => "name", 5 => "directory", _ => smi.ToString() };
            o.WriteLine($"  sort           : {sortKey} {(resolved.SortDirectionIndex == 1 ? "asc" : "desc")}");
        }
        if (resolved.GroupMode is { } gm)         o.WriteLine($"  group          : {gm}{(resolved.GroupSortDirectionIndex == 1 ? " (reversed)" : "")}");
        if (resolved.SearchHiddenFiles is { } sh) o.WriteLine($"  hidden         : {sh}");
        if (resolved.SearchInsideArchives is { } ar) o.WriteLine($"  archives       : {ar}");
        if (resolved.SearchBinary is { } sb)      o.WriteLine($"  binary         : {sb}");
        if (resolved.SearchImageText is { } sit)  o.WriteLine($"  image-text     : {sit}");
        if (resolved.Warnings.Count > 0)          o.WriteLine($"  warnings       : {string.Join(" | ", resolved.Warnings)}");
    }

    /// <summary>Result of the first-run model-selection step.</summary>
    private enum SemanticModelSetup { Ready, Declined, Failed }

    /// <summary>
    /// When the user declines to download a model, drop to Traditional search: reuse the typed
    /// semantic text as a literal search pattern and default the directory. With <c>--explain</c>
    /// there is nothing to interpret, so we stop instead of running a real search.
    /// </summary>
    private static (int Code, bool Stop) FallBackToTraditional(CliArgs args)
    {
        if (args.Explain)
        {
            WriteError("No model selected — semantic translation skipped (nothing to explain).");
            return (0, true);
        }

        args.FallBackSemanticToTraditional();
        WriteError($"Using Traditional search for: {args.Pattern}");
        return (0, false); // continue the normal pipeline as a literal search
    }

    /// <summary>
    /// Mirrors the GUI download modal for the CLI: lists the hardware-appropriate models, lets the
    /// user pick one or decline, downloads the choice, and persists it so later runs skip the prompt.
    /// Returns <see cref="SemanticModelSetup.Declined"/> when the user opts out (caller falls back to
    /// Traditional search) and <see cref="SemanticModelSetup.Failed"/> on an unrecoverable error.
    /// </summary>
    private static async Task<SemanticModelSetup> EnsureSemanticModelReadyAsync(
        FoundryLocalSemanticQueryTranslator translator, CliArgs args, AppSettings settings,
        IProgress<SemanticTranslationProgress> progress, CancellationToken ct)
    {
        // A redirected/non-interactive stdin means we cannot prompt. Only auto-download when the
        // user explicitly opted in with --accept-model-download; otherwise fall back to Traditional.
        bool interactive = !Console.IsInputRedirected;
        if (!interactive && !args.AcceptModelDownload)
        {
            WriteError("Semantic search needs a local AI model (first-time setup), but this console");
            WriteError("cannot prompt for confirmation. Re-run interactively, pass --semantic-model <alias>,");
            WriteError("or add --accept-model-download to download the recommended model automatically.");
            return SemanticModelSetup.Declined;
        }

        WriteError("Semantic search needs a local AI model (first-time setup). Checking available models…");

        IReadOnlyList<SemanticModelOption> options;
        try
        {
            options = await translator.ListModelOptionsAsync(progress, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            WriteError($"error: could not list local models: {ex.Message}");
            return SemanticModelSetup.Failed;
        }

        if (options.Count == 0)
        {
            WriteError("error: no compatible local model is available for this machine.");
            return SemanticModelSetup.Failed;
        }

        var recommended = options.FirstOrDefault(o => o.IsRecommended) ?? options[0];

        bool chosenIsRecommended;
        string chosenAlias;
        if (!interactive)
        {
            // --accept-model-download on a non-interactive console: take the recommended pick.
            chosenIsRecommended = true;
            chosenAlias = recommended.Alias;
            WriteError($"Auto-accepting the recommended model: {recommended.DisplayName} ({FormatModelSize(recommended.SizeBytes)}).");
        }
        else
        {
            var pick = PromptForModelChoice(options, recommended);
            if (pick is null) return SemanticModelSetup.Declined;
            chosenIsRecommended = pick.IsRecommended;
            chosenAlias = pick.Alias;
        }

        try
        {
            // A null alias tells the translator to use the recommended/auto pick, matching the GUI so
            // Yagu keeps tracking the best model for this machine.
            await translator.PrepareModelAsync(chosenIsRecommended ? null : chosenAlias, progress, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            WriteError($"error: model download failed: {ex.Message}");
            return SemanticModelSetup.Failed;
        }

        PersistSemanticModelChoice(chosenIsRecommended ? string.Empty : chosenAlias);
        settings.SemanticModelDownloaded = true;
        return SemanticModelSetup.Ready;
    }

    /// <summary>
    /// Prints the model menu to stderr and reads a choice from stdin. Returns the chosen option,
    /// the recommended option on an empty/"y" answer, or null when the user declines or gives up.
    /// </summary>
    private static SemanticModelOption? PromptForModelChoice(
        IReadOnlyList<SemanticModelOption> options, SemanticModelOption recommended)
    {
        WriteError("");
        WriteError("Choose a local AI model for semantic search:");
        for (int idx = 0; idx < options.Count; idx++)
        {
            var o = options[idx];
            var tags = new List<string>();
            if (o.IsRecommended) tags.Add("recommended");
            if (o.IsCached) tags.Add("already downloaded");
            if (!string.IsNullOrWhiteSpace(o.DeviceLabel)) tags.Add(o.DeviceLabel!);
            string suffix = tags.Count > 0 ? $"  [{string.Join(", ", tags)}]" : string.Empty;
            string warn = o.ExceedsAvailableMemory
                ? "  (!) too large for this PC's memory - will fail to load"
                : o.IsBelowRecommended ? "  (!) may give less accurate results" : string.Empty;
            WriteError($"  {idx + 1,2}) {o.DisplayName,-28} {FormatModelSize(o.SizeBytes),8}{suffix}{warn}");
        }

        WriteError("");
        WriteError($"[Enter] download recommended ({recommended.DisplayName}, {FormatModelSize(recommended.SizeBytes)})   " +
                   "[1-N] choose another   [n] no, use Traditional search");

        for (int attempt = 0; attempt < 5; attempt++)
        {
            Console.Error.Write("> ");
            string? line = Console.ReadLine();
            if (line is null) return null; // EOF / closed stdin
            line = line.Trim();
            if (line.Length == 0) return recommended;
            if (line.Equals("n", StringComparison.OrdinalIgnoreCase) || line.Equals("no", StringComparison.OrdinalIgnoreCase))
                return null;
            if (line.Equals("y", StringComparison.OrdinalIgnoreCase) || line.Equals("yes", StringComparison.OrdinalIgnoreCase))
                return recommended;
            if (int.TryParse(line, out int n) && n >= 1 && n <= options.Count)
                return options[n - 1];
            WriteError("Please enter a number, press Enter for the recommended model, or 'n' to decline.");
        }

        WriteError("No valid selection — using Traditional search.");
        return null;
    }

    /// <summary>Formats an approximate model size for the picker (GB/MB), or "size n/a" when unknown.</summary>
    private static string FormatModelSize(long? bytes)
    {
        if (bytes is not { } b || b <= 0) return "size n/a";
        double gb = b / 1024d / 1024d / 1024d;
        if (gb >= 1.0) return $"{gb:0.0} GB";
        double mb = b / 1024d / 1024d;
        return $"{mb:0} MB";
    }

    /// <summary>
    /// Warns (to stderr, keeping stdout ripgrep-clean) before the first OCR asset download and decides
    /// whether to proceed. Honors <c>--allow-ocr-download</c> (non-interactive opt-in); otherwise, on an
    /// interactive console, asks y/N. Declining (or a non-interactive run without the flag) returns
    /// false, so image-text search reports the components are unavailable rather than downloading them.
    /// </summary>
    private static bool ConfirmOcrDownloadOnConsole(
        Yagu.Services.Ocr.OcrAssetRequirement requirement, CliArgs args, bool interactive)
    {
        string components = requirement.MissingComponents.Count > 0
            ? " (" + string.Join(", ", requirement.MissingComponents) + ")"
            : string.Empty;

        WriteError("");
        WriteError($"Image-text (OCR) search needs a one-time download of about {requirement.ApproxMb} MB{components}.");
        WriteError("The files come from public package feeds (nuget.org and GitHub).");

        if (args.AllowOcrDownload)
        {
            WriteError("Proceeding because --allow-ocr-download was specified.");
            PersistOcrDownloadConsent();
            return true;
        }

        if (!interactive)
        {
            WriteError("Re-run with --allow-ocr-download to permit the download (skipping image-text search for now).");
            return false;
        }

        WriteError("[y] download now   [N] skip image-text search");
        for (int attempt = 0; attempt < 5; attempt++)
        {
            Console.Error.Write("> ");
            string? line = Console.ReadLine();
            if (line is null) return false; // EOF / closed stdin
            line = line.Trim();
            if (line.Length == 0) return false;
            if (line.Equals("y", StringComparison.OrdinalIgnoreCase) || line.Equals("yes", StringComparison.OrdinalIgnoreCase))
            {
                PersistOcrDownloadConsent();
                return true;
            }
            if (line.Equals("n", StringComparison.OrdinalIgnoreCase) || line.Equals("no", StringComparison.OrdinalIgnoreCase))
                return false;
            WriteError("Please enter 'y' to download or 'n' to skip image-text search.");
        }

        WriteError("No valid answer — skipping image-text search.");
        return false;
    }

    /// <summary>Persists OCR download consent to the shared settings store so later runs never re-ask.</summary>
    private static void PersistOcrDownloadConsent()
    {
        try
        {
            var service = new SettingsService();
            var global = service.Load();
            global.OcrDownloadConsented = true;
            service.Save(global);
        }
        catch (Exception ex)
        {
            // Consent still applies this run even if persistence fails.
            LogService.Instance.Warning("OcrConsent", $"Unable to persist OCR download consent: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Persists the chosen model to the global settings store the GUI also uses, so later runs (CLI or
    /// GUI) skip the first-run prompt. An empty alias means "track the recommended/auto pick".
    /// </summary>
    private static void PersistSemanticModelChoice(string aliasToPersist)
    {
        try
        {
            var service = new SettingsService();
            var global = service.Load();
            global.SemanticModelAlias = aliasToPersist ?? string.Empty;
            global.SemanticModelDownloaded = true;
            service.Save(global);
        }
        catch (Exception ex)
        {
            WriteError($"warning: could not save the model choice: {ex.Message}");
        }
    }

    /// <summary>Prints the interpreted search parameters to stdout for an <c>--explain</c> dry-run.</summary>
    private static void PrintSemanticExplanation(CliArgs args, ResolvedSearchPlan resolved)
    {
        var o = Console.Out;
        o.WriteLine("Semantic query interpreted as:");
        o.WriteLine($"  summary        : {SemanticPlanApplier.BuildExplanation(resolved)}");
        o.WriteLine($"  directory      : {args.Directory}");
        o.WriteLine($"  pattern        : {(string.IsNullOrEmpty(args.Pattern) ? "(none)" : args.Pattern)}");
        if (resolved.SearchMode is { } sm)        o.WriteLine($"  search-mode    : {sm}");
        if (resolved.UseRegex is { } rx)          o.WriteLine($"  regex          : {rx}");
        if (resolved.CaseSensitive is { } cs)     o.WriteLine($"  case-sensitive : {cs}");
        if (resolved.ExactMatch is { } em)        o.WriteLine($"  exact-match    : {em}");
        if (resolved.IncludeGlobs is { } inc)     o.WriteLine($"  include        : {string.Join(", ", inc)}");
        if (resolved.ExcludeGlobs is { } exc)     o.WriteLine($"  exclude        : {string.Join(", ", exc)}");
        if (resolved.MinFileSizeBytes is { } mn)  o.WriteLine($"  min-size       : {mn:N0} bytes");
        if (resolved.MaxFileSizeBytes is { } mx)  o.WriteLine($"  max-size       : {mx:N0} bytes");
        if (resolved.CreatedAfterDate is { } ca)  o.WriteLine($"  created-after  : {ca:yyyy-MM-dd}");
        if (resolved.CreatedBeforeDate is { } cb) o.WriteLine($"  created-before : {cb:yyyy-MM-dd}");
        if (resolved.ModifiedAfterDate is { } ma) o.WriteLine($"  modified-after : {ma:yyyy-MM-dd}");
        if (resolved.ModifiedBeforeDate is { } mb)o.WriteLine($"  modified-before: {mb:yyyy-MM-dd}");
        if (resolved.MaxSearchDepth is { } depth) o.WriteLine($"  max-depth      : {(depth <= 0 ? "unlimited" : depth.ToString(System.Globalization.CultureInfo.InvariantCulture))}");
        if (resolved.ObeyGitignore is { } gi)     o.WriteLine($"  obey-gitignore : {gi}");
        if (resolved.SearchInsideArchives is { } arc) o.WriteLine($"  archives       : {arc}");
        if (resolved.SearchHiddenFiles is { } sh) o.WriteLine($"  hidden         : {sh}");
        if (args.SearchImageText is { } img) o.WriteLine($"  image-text     : {img}{(img ? $" ({AppSettings.NormalizeImageOcrEngine(args.ImageOcrEngine)})" : string.Empty)}");
        if (!string.IsNullOrWhiteSpace(args.SortBy))
            o.WriteLine($"  sort           : {args.SortBy} ({(args.SortDescending ? "descending" : "ascending")})");
        if (!string.IsNullOrWhiteSpace(args.GroupBy))
            o.WriteLine($"  group          : {args.GroupBy}{(args.GroupDescending ? " (reversed)" : string.Empty)}");
        o.WriteLine();
        o.WriteLine("(--explain dry-run: no search executed. Re-run without --explain to search.)");
    }

    // -----------------------------------------------------------------------
    // Console attachment
    // -----------------------------------------------------------------------

    // Returns true when stdout is an interactive VT-capable terminal (colours enabled).
    private static bool EnsureConsole()
    {
        const uint ATTACH_PARENT_PROCESS = unchecked((uint)-1);

        // Ensure consistent UTF-8 output (no BOM) so non-ASCII chars survive piping.
        var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        Console.OutputEncoding = utf8NoBom;
        Console.InputEncoding  = utf8NoBom;

        // Snapshot whether stdout/stderr were already connected (piped/redirected
        // by the shell) BEFORE we do anything to the handles.
        nint hOutBefore = GetStdHandle(STD_OUTPUT_HANDLE);
        nint hErrBefore = GetStdHandle(STD_ERROR_HANDLE);
        // A handle is "piped" only when it's a VALID handle pointing at a pipe or file.
        // Invalid handles (0/-1) mean we're a GUI app with no console - need AttachConsole.
        bool outHasHandle = hOutBefore != 0 && hOutBefore != -1;
        bool errHasHandle = hErrBefore != 0 && hErrBefore != -1;
        bool outPiped = outHasHandle && GetFileType(hOutBefore) != FILE_TYPE_CHAR;
        bool errPiped = errHasHandle && GetFileType(hErrBefore) != FILE_TYPE_CHAR;

        if (!outPiped || !errPiped)
        {
            // Need a console for at least one stream - attach to parent or allocate.
            if (!AttachConsole(ATTACH_PARENT_PROCESS))
                AllocConsole();
        }

        bool vtEnabled = false;

        // Reinitialise Console.Out - use the inherited pipe handle when piped,
        // or CONOUT$ for interactive terminal output.
        try
        {
            if (outPiped)
            {
                var s = Console.OpenStandardOutput();
                Console.SetOut(new StreamWriter(s, utf8NoBom, 4096, leaveOpen: true) { AutoFlush = true });
            }
            else
            {
                var h = CreateFile("CONOUT$", GENERIC_WRITE, FILE_SHARE_WRITE, 0, OPEN_EXISTING, 0, 0);
                if (h != -1 && h != 0)
                {
                    // Enable ANSI/VT escape-sequence processing on this console handle.
                    if (GetConsoleMode(h, out uint mode))
                        SetConsoleMode(h, mode | ENABLE_VIRTUAL_TERMINAL_PROCESSING | ENABLE_PROCESSED_OUTPUT);

                    SetStdHandle(STD_OUTPUT_HANDLE, h);
                    var fs = new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(h, ownsHandle: false), FileAccess.Write, 4096);
                    Console.SetOut(new StreamWriter(fs, utf8NoBom, 4096, leaveOpen: true) { AutoFlush = true });
                    vtEnabled = true;
                }
            }
        }
        catch { /* best-effort */ }

        try
        {
            if (errPiped)
            {
                var s = Console.OpenStandardError();
                Console.SetError(new StreamWriter(s, utf8NoBom, 4096, leaveOpen: true) { AutoFlush = true });
            }
            else
            {
                var h = CreateFile("CONOUT$", GENERIC_WRITE, FILE_SHARE_WRITE, 0, OPEN_EXISTING, 0, 0);
                if (h != -1 && h != 0)
                {
                    if (GetConsoleMode(h, out uint mode))
                        SetConsoleMode(h, mode | ENABLE_VIRTUAL_TERMINAL_PROCESSING | ENABLE_PROCESSED_OUTPUT);

                    SetStdHandle(STD_ERROR_HANDLE, h);
                    var fs = new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(h, ownsHandle: false), FileAccess.Write, 4096);
                    Console.SetError(new StreamWriter(fs, utf8NoBom, 4096, leaveOpen: true) { AutoFlush = true });
                }
            }
        }
        catch { /* best-effort */ }

        return vtEnabled;
    }

    // -----------------------------------------------------------------------
    // Everything Search: offer to start or install (once per machine, tracked by marker file)
    // -----------------------------------------------------------------------

    private static readonly string EverythingMarkerPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "Yagu", ".everything-prompted");

    private static async Task OfferEverythingSetupAsync()
    {
        // Only ask once - marker file records that we've already prompted.
        if (File.Exists(EverythingMarkerPath)) return;

        bool running  = Process.GetProcessesByName("Everything").Length > 0;

        // Everything is running - nothing to do (don't write the marker;
        // if Everything is later uninstalled we still want to ask again).
        if (running) return;
        // Check if Everything.exe can be located (meaning the full app is installed).
        var esPath        = FileLister.FindEsExe();
        var everythingExe = esPath != null ? FindEverythingExe(esPath) : null;

        // Also check standard install paths directly in case es.exe is a standalone tool.
        if (everythingExe == null)
        {
            foreach (var p in new[]
            {
                @"C:\Program Files\Everything\Everything.exe",
                @"C:\Program Files (x86)\Everything\Everything.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Everything", "Everything.exe"),
            })
            {
                if (File.Exists(p)) { everythingExe = p; break; }
            }
        }

        try
        {
            if (everythingExe != null)
            {
                // Full Everything app is installed but not running — offer to start.
                Console.Error.WriteLine();
                Console.Error.Write("Everything Search is installed but not running. Start it now for fast file discovery? [Y/n] ");
                var answer = Console.ReadLine();
                Console.Error.WriteLine();

                if (IsYes(answer))
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo { FileName = everythingExe, UseShellExecute = true });
                        await Task.Delay(1500); // brief wait so it begins indexing before the search
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"Could not start Everything: {ex.Message}");
                    }
                }
                return;
            }

            // Not installed — offer to download and install.
            Console.Error.WriteLine();
            Console.Error.Write("Everything Search by voidtools is not installed. Install it for significantly faster file discovery? [Y/n] ");
            var installAnswer = Console.ReadLine();
            Console.Error.WriteLine();

            if (!IsYes(installAnswer)) return;

            bool is64 = Environment.Is64BitOperatingSystem;

            // Offline edition: run the pre-bundled voidtools Everything setup instead of downloading
            // it. Consent was already given above; signature verification and elevation still apply
            // (a tampered bundle is refused just like a tampered download).
            string? bundledInstaller = EverythingAssetPaths.BundledInstallerPath(is64);
            bool installerFromBundle = bundledInstaller is not null;
            string installerPath;

            try
            {
                if (installerFromBundle)
                {
                    installerPath = bundledInstaller!;
                    Console.Error.WriteLine("Using the bundled Everything Search installer (offline edition)...");
                }
                else
                {
                    string url = EverythingAssetPaths.DownloadUrl(is64);
                    installerPath = Path.Combine(Path.GetTempPath(), EverythingAssetPaths.SetupFileName(is64));

                    Console.Error.WriteLine("Downloading Everything Search installer...");
                    using var http = new HttpClient();
                    var data = await http.GetByteArrayAsync(new Uri(url));
                    await File.WriteAllBytesAsync(installerPath, data);
                }

                // Refuse to run the installer elevated unless it carries a valid Authenticode
                // signature from voidtools. HTTPS alone does not guarantee the payload is untampered
                // (compromised mirror / MITM with a trusted cert) — OWASP A08 software integrity.
                if (!AuthenticodeVerifier.IsTrustedPublisher(installerPath, EverythingAssetPaths.TrustedPublisher, out string signatureFailure))
                {
                    if (!installerFromBundle) { try { File.Delete(installerPath); } catch { /* best effort */ } }
                    Console.Error.WriteLine($"Everything installer failed signature verification ({signatureFailure}); not running it. Continuing with built-in file enumeration.");
                    return;
                }

                Console.Error.WriteLine("Running installer \u2014 please complete the setup wizard...");

                var proc = Process.Start(new ProcessStartInfo
                {
                    FileName       = installerPath,
                    Verb           = "runas",
                    UseShellExecute = true,
                });
                if (proc != null) await proc.WaitForExitAsync();

                var installedEsPath = FileLister.FindEsExe();
                if (installedEsPath != null && Process.GetProcessesByName("Everything").Length == 0)
                {
                    var postInstallExe = FindEverythingExe(installedEsPath);
                    if (postInstallExe != null)
                    {
                        try { Process.Start(new ProcessStartInfo { FileName = postInstallExe, UseShellExecute = true }); }
                        catch { /* ignore */ }
                        await Task.Delay(2000);
                    }
                }

                Console.Error.WriteLine("Everything installed. Proceeding with search...");
                Console.Error.WriteLine();
            }
            catch (System.ComponentModel.Win32Exception)
            {
                Console.Error.WriteLine("Installation was cancelled.");
                Console.Error.WriteLine();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Installation failed: {ex.Message}");
                Console.Error.WriteLine();
            }
        }
        finally
        {
            // Write marker after the first prompt so we never pester again.
            // (The "already running" early-return above skips the try block entirely,
            //  so this finally only runs when we actually showed a prompt.)
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(EverythingMarkerPath)!);
                await File.WriteAllTextAsync(EverythingMarkerPath, DateTime.UtcNow.ToString("o"));
            }
            catch { /* best-effort */ }
        }
    }

    private static bool IsYes(string? answer)
        => string.IsNullOrWhiteSpace(answer) || answer.Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase);

    private static string? FindEverythingExe(string esPath)
    {
        var dir = Path.GetDirectoryName(esPath);
        if (dir != null)
        {
            var candidate = Path.Combine(dir, "Everything.exe");
            if (File.Exists(candidate)) return candidate;
        }

        // Check registry-discovered install directories
        foreach (var registryDir in FileLister.GetEverythingInstallDirsFromRegistry())
        {
            var candidate = Path.Combine(registryDir, "Everything.exe");
            if (File.Exists(candidate)) return candidate;
        }

        foreach (var path in new[]
        {
            @"C:\Program Files\Everything\Everything.exe",
            @"C:\Program Files (x86)\Everything\Everything.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Everything", "Everything.exe"),
        })
        {
            if (File.Exists(path)) return path;
        }
        return null;
    }

    // -----------------------------------------------------------------------
    // Admin privilege warning (mirrors the UI InfoBar banner)
    // -----------------------------------------------------------------------

    private static void WarnIfNotAdmin(CliArgs args)
    {
        if (args.SuppressAdminWarning) return;

        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        if (!principal.IsInRole(WindowsBuiltInRole.Administrator))
        {
            WriteError("warning: Yagu is not running with administrator privileges.");
            WriteError("         Some files may not be readable. Run as administrator for full access.");
            WriteError(string.Empty);
        }
    }

    // -----------------------------------------------------------------------
    // Settings: .yagu.json in CWD → process launch directory → global AppData, then CLI overrides
    // -----------------------------------------------------------------------

    private static AppSettings LoadEffectiveSettings(CliArgs args)
    {
        // 1. Prefer a .yagu.json beside the current working directory.
        var cwdSettings = Path.Combine(Directory.GetCurrentDirectory(), LocalSettingsFileName);
        if (File.Exists(cwdSettings))
            return new SettingsService(cwdSettings).Load();

        // 2. Next try a .yagu.json beside the running process. This lets a portable
        // Yagu install carry a local CLI defaults file even when invoked from another CWD.
        var launchSettings = ResolveProcessLaunchSettingsPath();
        if (!string.IsNullOrWhiteSpace(launchSettings) && File.Exists(launchSettings))
            return new SettingsService(launchSettings).Load();

        // 3. Fall back to global AppData settings.
        return new SettingsService().Load();
    }

    private static string? ResolveProcessLaunchSettingsPath()
    {
        string? launchDirectory = null;
        try
        {
            launchDirectory = !string.IsNullOrWhiteSpace(Environment.ProcessPath)
                ? Path.GetDirectoryName(Environment.ProcessPath)
                : null;

            if (string.IsNullOrWhiteSpace(launchDirectory))
                launchDirectory = AppContext.BaseDirectory;
        }
        catch
        {
            launchDirectory = AppContext.BaseDirectory;
        }

        if (string.IsNullOrWhiteSpace(launchDirectory))
            return null;

        try
        {
            var cwd = Directory.GetCurrentDirectory();
            if (Path.GetFullPath(launchDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Equals(Path.GetFullPath(cwd).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }
        catch
        {
            // If paths cannot be normalized, still try the launch directory.
        }

        return Path.Combine(launchDirectory, LocalSettingsFileName);
    }

    // -----------------------------------------------------------------------
    // Build SearchOptions: merge base settings with CLI overrides
    // -----------------------------------------------------------------------

    // Builds one SearchOptions per target root. With an explicit --directory there is a single root;
    // when it is omitted ("search all drives") every eligible drive becomes a root, and HDD roots are
    // forced to parallelism 1 while other drives keep the configured value — all in one search.
    private static List<SearchOptions> BuildPerRootSearchOptions(CliArgs args, AppSettings s)
    {
        if (!string.IsNullOrWhiteSpace(args.Directory))
            return new List<SearchOptions> { BuildSearchOptions(args, s) };

        var roots = Yagu.Services.DriveEnumerator.GetSearchRoots(
            s.SearchAllDrivesIncludesNetwork, s.SearchAllDrivesIncludesRemovable, s.SearchAllDrivesIncludesCloud);
        int baseParallelism = args.Parallelism ?? SearchOptions.ResolveContentSearchParallelism(s.ParallelismIndex, Environment.ProcessorCount);
        // Backend stays Auto per root (fast Everything where it indexes the drive, automatic managed
        // fallback where it does not) unless the user opts into a full scan of every drive.
        FileListerBackend? backendOverride = s.SearchAllDrivesForceFullScan ? FileListerBackend.Managed : null;
        var list = new List<SearchOptions>(roots.Count);
        foreach (var root in roots)
        {
            int p = baseParallelism;
            if (s.LimitParallelismOnHdd && Yagu.Helpers.DiskTypeDetector.IsHardDisk(root)) p = 1;
            list.Add(BuildSearchOptions(args, s, root, p, backendOverride));
        }
        return list;
    }

    private static SearchOptions BuildSearchOptions(CliArgs args, AppSettings s, string? directoryOverride = null, int? parallelismOverride = null, FileListerBackend? backendOverride = null)
    {
        bool caseSensitive  = args.CaseSensitive ?? s.CaseSensitive;
        bool useRegex       = args.UseRegex       ?? s.UseRegex;
        int  contextLines   = args.ContextLines   ?? s.ContextLines;
        long minFileSize    = args.MinFileSizeBytes ?? s.DefaultMinFileSizeBytes;
        long maxFileSize    = args.MaxFileSizeBytes ?? s.DefaultMaxFileSizeBytes;
        bool skipBinary     = args.SkipBinary     ?? s.SkipBinary;
        int  parallelism    = parallelismOverride ?? args.Parallelism ?? SearchOptions.ResolveContentSearchParallelism(s.ParallelismIndex, Environment.ProcessorCount);
        long memoryBytes    = args.MemoryLimitMB.HasValue
            ? (long)args.MemoryLimitMB.Value * 1024 * 1024
            : (long)s.MemoryLimitMB         * 1024 * 1024;
        int maxResults = args.MaxResults ?? s.MaxResults;
        int memoryPressure = args.MemoryPressurePercent ?? s.MemoryPressurePercent;
        int sdkBuffer = args.SdkChannelBufferSize ?? s.SdkChannelBufferSize;
        bool excludeAdminPaths = args.ExcludeAdminProtectedPaths ?? s.ExcludeAdminProtectedPaths;
        string adminSegments = args.AdminProtectedPathSegments ?? s.AdminProtectedPathSegments;
        bool searchArchives = args.SearchInsideArchives ?? s.SearchInsideArchives;
        string archiveExts = args.ArchiveExtensions ?? s.ArchiveExtensions;
        bool searchImageText = args.SearchImageText ?? s.SearchImageText;
        string imageOcrEngine = AppSettings.NormalizeImageOcrEngine(args.ImageOcrEngine ?? s.ImageOcrEngine);
        string imageOcrModel = AppSettings.NormalizeImageOcrModel(args.ImageOcrModel ?? s.ImageOcrModel);
        int imageOcrMaxSide = AppSettings.NormalizeImageOcrMaxSide(args.ImageOcrMaxSide ?? s.ImageOcrMaxSide);

        bool obeyGitignore = args.ObeyGitignore ?? s.ObeyGitignore;
        bool gitignorePrecedence = args.GitignoreTakesPrecedence ?? s.GitignoreTakesPrecedence;
        bool exactMatch = args.ExactMatch ?? s.ExactMatch;
        int maxMatchesPerFile = args.MaxMatchesPerFile ?? s.MaxMatchesPerFile;
        int maxSearchDepth = args.MaxSearchDepth ?? s.MaxSearchDepth;
        var includeMode = (FilterPatternMode)(args.IncludeFilterModeIndex ?? s.IncludeFilterModeIndex);
        var excludeMode = (FilterPatternMode)(args.ExcludeFilterModeIndex ?? s.ExcludeFilterModeIndex);

        var includeGlobs = args.IncludeGlobs.Count > 0
            ? (IReadOnlyList<string>)args.IncludeGlobs
            : SplitSemi(s.IncludeGlobs);

        var excludeGlobs = args.ExcludeGlobs.Count > 0
            ? (IReadOnlyList<string>)args.ExcludeGlobs
            : SplitSemi(s.ExcludeGlobs);

        var skipExtensions = args.SkipExtensions.Count > 0
            ? new HashSet<string>(args.SkipExtensions, StringComparer.OrdinalIgnoreCase)
            : ParseSkipExtensions(s.SkipExtensions);

        return new SearchOptions
        {
            Directory             = directoryOverride ?? args.Directory ?? string.Empty,
            Query                 = args.Pattern!,
            CaseSensitive         = caseSensitive,
            UseRegex              = useRegex,
            ExactMatch            = exactMatch,
            ContextLines          = contextLines,
            SearchMode            = args.SearchMode ?? SearchMode.Both,
            IncludeGlobs          = includeGlobs,
            ExcludeGlobs          = excludeGlobs,
            IncludeFilterMode     = includeMode,
            ExcludeFilterMode     = excludeMode,
            MinFileSizeBytes      = minFileSize,
            MaxFileSizeBytes      = maxFileSize,
            CreatedAfterDate      = args.CreatedAfter ?? s.DefaultCreatedAfterDate,
            CreatedBeforeDate     = args.CreatedBefore ?? s.DefaultCreatedBeforeDate,
            ModifiedAfterDate     = args.ModifiedAfter ?? s.DefaultModifiedAfterDate,
            ModifiedBeforeDate    = args.ModifiedBefore ?? s.DefaultModifiedBeforeDate,
            MaxResults            = Math.Min(maxResults, SearchOptions.MaxResultsCeiling),
            MaxMatchesPerFile     = maxMatchesPerFile,
            MaxSearchDepth        = maxSearchDepth,
            SkipBinary            = skipBinary,
            SearchOnlineOnlyFiles = s.SearchOnlineOnlyFiles,
            SearchHiddenFiles     = args.SearchHiddenFiles ?? s.SearchHiddenFiles,
            ObeyGitignore         = obeyGitignore,
            GitignoreTakesPrecedence = gitignorePrecedence,
            MaxDegreeOfParallelism = parallelism,
            FileListerBackendOverride = backendOverride,
            IoOversubscriptionIndex = s.IoOversubscriptionIndex,
            MaxProcessMemoryBytes = memoryBytes,
            MemoryPressurePercent = memoryPressure,
            SkipExtensions        = skipExtensions,
            SdkChannelBufferSize  = sdkBuffer,
            SearchInsideArchives  = searchArchives,
            ArchiveExtensions     = SplitSemi(archiveExts).ToHashSet(StringComparer.OrdinalIgnoreCase),
            SearchImageText       = searchImageText,
            ImageOcrExtensions    = SplitSemi(AppSettings.DefaultImageOcrExtensions).ToHashSet(StringComparer.OrdinalIgnoreCase),
            ImageOcrEngine        = imageOcrEngine,
            ImageOcrModel         = imageOcrModel,
            ImageOcrMaxSide       = imageOcrMaxSide,
            ExcludeAdminProtectedPaths = excludeAdminPaths,
            AdminProtectedPathSegments = Yagu.Services.FileLister.ParseAdminProtectedSegments(adminSegments),
        };
    }

    private static string[] SplitSemi(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        return value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static HashSet<string> ParseSkipExtensions(string raw)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(raw))
            foreach (var ext in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                set.Add(ext.TrimStart('.'));
        return set;
    }

    // -----------------------------------------------------------------------
    // Search + ripgrep-style output
    // -----------------------------------------------------------------------

    private static async Task<int> RunSearchAsync(List<SearchOptions> perRootOptions, CliArgs args, bool vtEnabled)
    {
        // Representative options for the flags that are identical across every root (query,
        // case-sensitivity, export/replace settings, etc.).
        var options = perRootOptions[0];
        bool useColor = vtEnabled;
        bool exporting = !string.IsNullOrWhiteSpace(args.ExportPath);
        bool replacing = args.ReplaceText is not null;
        bool sorting = !string.IsNullOrWhiteSpace(args.SortBy);
        bool grouping = !string.IsNullOrWhiteSpace(args.GroupBy);
        bool savingSession = !string.IsNullOrWhiteSpace(args.SaveSessionPath);
        bool needsCollection = exporting || replacing || sorting || grouping || savingSession;

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var service  = new SearchService();

        // Direct output mode: bypass RipgrepWriter entirely — the DirectOutputSink writes
        // ripgrep-formatted UTF-8 directly from Rust's byte buffers. A single 128 KiB buffered stdout
        // stream is shared by every root (roots are scanned sequentially), coalescing the many tiny
        // per-match writes into block writes like ripgrep's buffered stdout. Disabled while collecting
        // results for post-processing (sort/export/replace/group/save-session).
        Stream? directStream = needsCollection ? null : new BufferedStream(Console.OpenStandardOutput(), 1 << 17);
        foreach (var rootOptions in perRootOptions)
        {
            rootOptions.DirectOutputStream = directStream;
            rootOptions.DirectOutputColor = !needsCollection && useColor;
        }

        var progress = vtEnabled ? new ProgressLine(useColor) : null;
        var collectedResults = needsCollection ? new List<SearchResult>() : null;
        long filesScanned = 0;
        long bytesScanned = 0;
        DateTime searchStarted = DateTime.UtcNow;

        try
        {
            await foreach (var ev in service.SearchManyAsync(perRootOptions, cts.Token).ConfigureAwait(false))
            {
                switch (ev)
                {
                    case SearchEvent.Progress p:
                        progress?.Update(p.Snapshot);
                        break;

                    case SearchEvent.SearchError e:
                        progress?.Hide();
                        WriteError($"error: {e.Message}", useColor);
                        progress?.Show();
                        break;

                    case SearchEvent.MemoryPressure mp:
                        mp.AcknowledgeEviction(0);
                        progress?.Hide();
                        WriteError("warning: memory pressure detected; search continues in degraded mode.", useColor);
                        progress?.Show();
                        break;

                    case SearchEvent.Fallback f:
                        progress?.Hide();
                        WriteError($"info: file-lister fallback - {f.Reason}", useColor);
                        progress?.Show();
                        break;

                    case SearchEvent.Completed c:
                        progress?.Dismiss();
                        filesScanned = c.Summary.FilesScanned;
                        bytesScanned = c.Summary.BytesScanned;
                        WriteCompletionSummary(c.Summary, useColor);

                        if (needsCollection && collectedResults != null)
                        {
                            // Apply sort if requested
                            var sortedResults = sorting
                                ? SortResults(collectedResults, args.SortBy!, args.SortDescending)
                                : collectedResults;

                            // Replace in files (replace prints its own per-file summary).
                            if (replacing)
                                await RunReplaceAsync(sortedResults, options, args, useColor);
                            else if (grouping)
                                // Grouping waits for the whole search, then renders grouped
                                // (and, within each group, sorted) — never streamed live.
                                WriteGroupedResults(collectedResults, args, useColor);
                            else
                                // Stream matches to stdout for every collection mode
                                // (sort / export / save-session), matching the parity of
                                // a plain streaming search. Pure replace suppresses this.
                                WriteSortedResults(sortedResults, useColor);

                            // Export
                            if (exporting)
                                await WriteExportFileAsync(args, sortedResults.ToList(), options.Query, searchStarted, filesScanned, bytesScanned);

                            // Save session
                            if (savingSession)
                                await WriteSessionFileAsync(
                                    args.SaveSessionPath!,
                                    sortedResults,
                                    options.Query,
                                    options.Directory,
                                    searchStarted,
                                    c.Summary.Elapsed,
                                    (int)filesScanned,
                                    bytesScanned,
                                    c.Summary.TotalMatches,
                                    useColor);
                        }

                        if (c.Summary.Cancelled) return 130;
                        return c.Summary.TotalMatches > 0 ? 0 : 1;
                }

                // Collect results for post-processing
                if (needsCollection)
                {
                    if (ev is SearchEvent.Match m)
                        collectedResults!.Add(m.Result);
                    else if (ev is SearchEvent.MatchBatch mb)
                        collectedResults!.AddRange(mb.Results);
                }
            }
        }
        catch (OperationCanceledException)
        {
            progress?.Dismiss();
            WriteError("search cancelled.");
            return 130;
        }
        catch (IOException)
        {
            // Broken pipe — consumer closed the pipe before we finished.
            // This is normal when users pipe to head/Select-Object -First N.
            progress?.Dismiss();
            return 0;
        }

        progress?.Dismiss();
        return 1;
    }

    private const string Orange = "\x1B[38;5;208m";
    private const string Reset  = "\x1B[0m";

    private static void WriteCompletionSummary(SearchSummary s, bool color)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"\nSearched {s.FilesScanned} file(s)");
        if (s.TotalFiles > 0 && s.TotalFiles != s.FilesScanned)
            sb.Append(CultureInfo.InvariantCulture, $" of {s.TotalFiles}");
        if (s.FilesSkipped > 0)
            sb.Append(CultureInfo.InvariantCulture, $", {s.FilesSkipped} skipped");
        sb.Append(CultureInfo.InvariantCulture, $" - {s.TotalMatches} match(es) in {s.FilesWithMatches} file(s)");
        sb.Append(CultureInfo.InvariantCulture, $" [{s.Elapsed.TotalSeconds:F2}s]");
        if (s.Truncated)  sb.Append(" [truncated]");
        if (s.Cancelled)  sb.Append(" [cancelled]");
        WriteError(sb.ToString(), color);

        var b = s.SkipReasons;
        if (b is not null && s.FilesSkipped > 0)
        {
            WriteError("Skipped breakdown:", color);
            if (b.GlobExcluded > 0)  WriteError($"  Glob exclusions:          {b.GlobExcluded,8:N0}", color);
            if (b.Binary > 0)        WriteError($"  Binary files:             {b.Binary,8:N0}", color);
            if (b.ByExtension > 0)   WriteError($"  Extension skips:          {b.ByExtension,8:N0}", color);
            if (b.TooLarge > 0)      WriteError($"  Too large:                {b.TooLarge,8:N0}", color);
            if (b.AccessDenied > 0)  WriteError($"  Access denied:            {b.AccessDenied,8:N0}", color);
            if (b.Directories > 0)   WriteError($"  Inaccessible dirs:        {b.Directories,8:N0}", color);
            if (b.IOError > 0)       WriteError($"  I/O errors:               {b.IOError,8:N0}", color);
            if (b.NotFound > 0)      WriteError($"  Not found:                {b.NotFound,8:N0}", color);
            if (b.Encoding > 0)      WriteError($"  Encoding errors:          {b.Encoding,8:N0}", color);
            if (b.Other > 0)         WriteError($"  Other:                    {b.Other,8:N0}", color);
        }
    }

    private static void WriteError(string msg, bool color = false)
        => Console.Error.WriteLine(color ? $"{Orange}{msg}{Reset}" : msg);

    // -----------------------------------------------------------------------
    // Export file writing
    // -----------------------------------------------------------------------

    private static async Task WriteExportFileAsync(
        CliArgs args,
        List<SearchResult> results,
        string query,
        DateTime searchStarted,
        long filesScanned,
        long bytesScanned)
    {
        if (string.IsNullOrWhiteSpace(args.ExportPath) || results.Count == 0)
            return;

        var format = args.ExportFormat switch
        {
            "json" => ReportFormat.Json,
            "csv"  => ReportFormat.Csv,
            "html" => ReportFormat.Html,
            _      => InferFormatFromExtension(args.ExportPath),
        };

        var exportOptions = new ReportExportOptions
        {
            Format = format,
            IncludeFileSizes = args.ExportFileSizes,
            IncludeModifiedDates = args.ExportModifiedDates,
            IncludeContextLines = true,
            ContextLineCount = args.ExportContextLines ?? 3,
            IncludeMatchMarkers = !args.ExportNoMarkers,
            CsvEmbedContext = args.ExportCsvEmbedContext || args.ExportCsvPipeSeparator,
            CsvUsePipeSeparator = args.ExportCsvPipeSeparator,
        };

        var groups = results
            .GroupBy(r => r.FilePath, StringComparer.OrdinalIgnoreCase)
            .Select(g => new HtmlReportExportService.FileMatchGroup(g.Key, Path.GetFileName(g.Key), g.ToList()))
            .ToList();

        var stats = new HtmlReportExportService.SearchStats(
            searchStarted,
            DateTime.UtcNow - searchStarted,
            filesScanned,
            bytesScanned);

        var exportDir = Path.GetDirectoryName(args.ExportPath);
        if (!string.IsNullOrEmpty(exportDir) && !Directory.Exists(exportDir))
            Directory.CreateDirectory(exportDir);

        await using var fs = new FileStream(args.ExportPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous);
        using var w = new StreamWriter(fs, new UTF8Encoding(false), bufferSize: 64 * 1024, leaveOpen: false);

        switch (format)
        {
            case ReportFormat.Json:
                await ReportExportService.WriteJsonReportAsync(w, query, groups, stats, exportOptions).ConfigureAwait(false);
                break;
            case ReportFormat.Csv:
                await ReportExportService.WriteCsvReportAsync(w, query, groups, exportOptions).ConfigureAwait(false);
                break;
            default:
                await HtmlReportExportService.WriteMultiFileReportAsync(w, query, groups, stats).ConfigureAwait(false);
                break;
        }

        WriteError($"Exported {format.ToString().ToUpperInvariant()} report ({groups.Count:N0} files, {results.Count:N0} matches) to {args.ExportPath}");
    }

    private static ReportFormat InferFormatFromExtension(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".json" => ReportFormat.Json,
            ".csv"  => ReportFormat.Csv,
            _ => ReportFormat.Html,
        };
    }

    // -----------------------------------------------------------------------
    // .yagu-session save / load
    // -----------------------------------------------------------------------

    private static async Task WriteSessionFileAsync(
        string path,
        List<SearchResult> results,
        string query,
        string searchRoot,
        DateTime searchStarted,
        TimeSpan elapsed,
        int filesScanned,
        long bytesScanned,
        int matchesFound,
        bool useColor)
    {
        try
        {
            var stats = new SessionFileService.SessionStats(
                searchStarted, elapsed, filesScanned, bytesScanned, matchesFound);

            await using var fs = new FileStream(
                path, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 64 * 1024, useAsync: true);

            await SessionFileService.WriteAsync(
                fs, query ?? string.Empty, searchRoot ?? string.Empty, stats, results).ConfigureAwait(false);

            WriteError($"Saved session ({results.Count:N0} matches) to {path}", useColor);
        }
        catch (Exception ex)
        {
            WriteError($"error: failed to save session to {path}: {ex.Message}", useColor);
        }
    }

    private static int RunLoadSession(string path, bool vtEnabled)
    {
        bool useColor = vtEnabled;

        if (!File.Exists(path))
        {
            WriteError($"error: session file not found: {path}", useColor);
            return 2;
        }

        SessionFileService.SessionHeader? header = null;
        var writer = new RipgrepWriter(Console.Out, useColor);
        int emitted = 0;

        try
        {
            using var fs = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 64 * 1024, useAsync: false);

            // ReadAsync streams batches; we just need to walk them synchronously.
            SessionFileService.ReadAsync(
                fs,
                h => header = h,
                batch =>
                {
                    foreach (var r in batch)
                    {
                        writer.Add(r);
                        emitted++;
                    }
                    return Task.CompletedTask;
                }).GetAwaiter().GetResult();

            writer.Flush();
        }
        catch (InvalidDataException ex)
        {
            WriteError($"error: {ex.Message}", useColor);
            return 2;
        }
        catch (Exception ex)
        {
            WriteError($"error: failed to load session: {ex.Message}", useColor);
            return 2;
        }

        var query = header?.Query ?? string.Empty;
        var root = header?.SearchRoot ?? string.Empty;
        var savedUtc = header?.SavedUtc ?? DateTime.UtcNow;
        WriteError($"\nLoaded {emitted:N0} match(es) from session '{Path.GetFileName(path)}'", useColor);
        if (!string.IsNullOrEmpty(query))
            WriteError($"  query: {query}", useColor);
        if (!string.IsNullOrEmpty(root))
            WriteError($"  root:  {root}", useColor);
        WriteError($"  saved: {savedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}", useColor);

        return emitted > 0 ? 0 : 1;
    }

    // -----------------------------------------------------------------------
    // Sort results
    // -----------------------------------------------------------------------

    private static List<SearchResult> SortResults(List<SearchResult> results, string sortBy, bool descending)
    {
        // Group by file, sort groups, then flatten back
        var groups = results.GroupBy(r => r.FilePath, StringComparer.OrdinalIgnoreCase).ToList();
        return OrderFileGroups(groups, sortBy, descending).SelectMany(g => g).ToList();
    }

    /// <summary>Orders per-file match groups by the given sort key. Shared by flat sort and grouped output.</summary>
    private static IEnumerable<IGrouping<string, SearchResult>> OrderFileGroups(
        IReadOnlyList<IGrouping<string, SearchResult>> groups, string sortBy, bool descending)
    {
        return sortBy switch
        {
            "matches" or "count" => descending
                ? groups.OrderByDescending(g => g.Count())
                : groups.OrderBy(g => g.Count()),
            "date" or "modified" => descending
                ? groups.OrderByDescending(g => GetFileModifiedSafe(g.Key))
                : groups.OrderBy(g => GetFileModifiedSafe(g.Key)),
            "size" => descending
                ? groups.OrderByDescending(g => GetFileSizeSafe(g.Key))
                : groups.OrderBy(g => GetFileSizeSafe(g.Key)),
            "name" or "filename" => descending
                ? groups.OrderByDescending(g => Path.GetFileName(g.Key), StringComparer.OrdinalIgnoreCase)
                : groups.OrderBy(g => Path.GetFileName(g.Key), StringComparer.OrdinalIgnoreCase),
            "directory" or "dir" => descending
                ? groups.OrderByDescending(g => Path.GetDirectoryName(g.Key) ?? "", StringComparer.OrdinalIgnoreCase)
                : groups.OrderBy(g => Path.GetDirectoryName(g.Key) ?? "", StringComparer.OrdinalIgnoreCase),
            "path" => descending
                ? groups.OrderByDescending(g => g.Key, StringComparer.OrdinalIgnoreCase)
                : groups.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase),
            _ => groups,
        };
    }

    private static DateTime GetFileModifiedSafe(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch { return DateTime.MinValue; }
    }

    private static long GetFileSizeSafe(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return 0; }
    }

    private static void WriteSortedResults(IReadOnlyList<SearchResult> results, bool useColor)
    {
        var writer = new RipgrepWriter(Console.Out, useColor);
        foreach (var result in results)
            writer.Add(result);
        writer.Flush();
    }

    // -----------------------------------------------------------------------
    // Grouped output (post-search): buckets files by the group key, orders the
    // buckets, and within each bucket orders by the sort key (when given). Only
    // used after the whole scan completes — grouped results are never streamed.
    // -----------------------------------------------------------------------

    private sealed class GroupBucket(string label)
    {
        public string Label { get; } = label;
        public List<IGrouping<string, SearchResult>> Files { get; } = [];
        public long MinSize { get; private set; } = long.MaxValue;
        public DateTime MaxDate { get; private set; } = DateTime.MinValue;
        public void ObserveSize(long size) { if (size < MinSize) MinSize = size; }
        public void ObserveDate(DateTime date) { if (date > MaxDate) MaxDate = date; }
    }

    private static void WriteGroupedResults(IReadOnlyList<SearchResult> results, CliArgs args, bool useColor)
    {
        string groupBy = args.GroupBy!;
        bool groupDescending = args.GroupDescending;
        bool sorting = !string.IsNullOrWhiteSpace(args.SortBy);

        bool needsSize = groupBy == "size";
        bool needsDate = groupBy is "modified" or "created" or "date";
        GroupMode dateMode = groupBy switch
        {
            "modified" => GroupMode.DateRangeModified,
            "created" => GroupMode.DateRangeCreated,
            _ => GroupMode.DateRangeModifiedCreated,
        };

        // One entry per file (first-seen order preserved), with that file's matches.
        var fileGroups = results
            .GroupBy(r => r.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var buckets = new List<GroupBucket>();
        var byLabel = new Dictionary<string, GroupBucket>(StringComparer.Ordinal);

        foreach (var fg in fileGroups)
        {
            string label;
            long size = 0;
            DateTime date = default;

            if (needsSize)
            {
                size = GetFileSizeSafe(fg.Key);
                label = SearchResultCollection.ClassifyFileSizeBucket(size);
            }
            else if (needsDate)
            {
                date = GetGroupDate(fg.Key, groupBy);
                label = SearchResultCollection.ClassifyDateRangeBucket(date, dateMode);
            }
            else if (groupBy == "extension")
            {
                string ext = Path.GetExtension(fg.Key);
                label = string.IsNullOrEmpty(ext) ? "(no extension)" : ext.ToLowerInvariant();
            }
            else // directory
            {
                string dir = Path.GetDirectoryName(fg.Key) ?? string.Empty;
                label = dir.Length == 0 ? "(root)" : dir;
            }

            if (!byLabel.TryGetValue(label, out var bucket))
            {
                bucket = new GroupBucket(label);
                byLabel[label] = bucket;
                buckets.Add(bucket);
            }
            bucket.Files.Add(fg);
            if (needsSize) bucket.ObserveSize(size);
            if (needsDate) bucket.ObserveDate(date);
        }

        var ordered = OrderBuckets(buckets, groupBy, groupDescending);

        var o = Console.Out;
        bool first = true;
        foreach (var bucket in ordered)
        {
            int fileCount = bucket.Files.Count;
            int matchCount = bucket.Files.Sum(f => f.Count());

            if (!first) o.WriteLine();
            first = false;
            WriteGroupHeader(o, bucket.Label, fileCount, matchCount, useColor);

            IEnumerable<IGrouping<string, SearchResult>> filesInBucket = sorting
                ? OrderFileGroups(bucket.Files, args.SortBy!, args.SortDescending)
                : bucket.Files;

            var writer = new RipgrepWriter(o, useColor);
            foreach (var fg in filesInBucket)
                foreach (var r in fg)
                    writer.Add(r);
            writer.Flush();
        }
    }

    private static List<GroupBucket> OrderBuckets(List<GroupBucket> buckets, string groupBy, bool groupDescending)
    {
        IEnumerable<GroupBucket> ordered = groupBy switch
        {
            // Natural order: smallest first; --group-desc => largest first.
            "size" => groupDescending
                ? buckets.OrderByDescending(b => b.MinSize)
                : buckets.OrderBy(b => b.MinSize),
            // Natural order: most recent first; --group-desc => oldest first.
            "modified" or "created" or "date" => groupDescending
                ? buckets.OrderBy(b => b.MaxDate)
                : buckets.OrderByDescending(b => b.MaxDate),
            // Natural order: A-Z; --group-desc => Z-A.
            _ => groupDescending
                ? buckets.OrderByDescending(b => b.Label, StringComparer.OrdinalIgnoreCase)
                : buckets.OrderBy(b => b.Label, StringComparer.OrdinalIgnoreCase),
        };
        return ordered.ToList();
    }

    private static void WriteGroupHeader(TextWriter o, string label, int fileCount, int matchCount, bool color)
    {
        string files = fileCount == 1 ? "file" : "files";
        string matches = matchCount == 1 ? "match" : "matches";
        string text = $"== {label}  ({fileCount:N0} {files}, {matchCount:N0} {matches}) ==";
        o.WriteLine(color ? $"\x1B[1;36m{text}\x1B[0m" : text);
    }

    private static DateTime GetGroupDate(string path, string groupBy)
    {
        try
        {
            return groupBy switch
            {
                "created" => File.GetCreationTime(path),
                "modified" => File.GetLastWriteTime(path),
                _ => Latest(File.GetLastWriteTime(path), File.GetCreationTime(path)),
            };
        }
        catch { return default; }

        static DateTime Latest(DateTime a, DateTime b) => a >= b ? a : b;
    }

    // -----------------------------------------------------------------------
    // Replace in files
    // -----------------------------------------------------------------------

    private static async Task RunReplaceAsync(IReadOnlyList<SearchResult> results, SearchOptions options, CliArgs args, bool useColor)
    {
        var replacement = args.ReplaceText!;
        bool dryRun = args.ReplaceDryRun;
        bool noBackup = args.ReplaceNoBackup;

        // Build a matcher that mirrors the search semantics exactly so replace
        // touches precisely what the search matched:
        //   • regex mode      -> use the query as a regex
        //   • literal mode    -> escaped literal; honours exact-match term splitting
        //                        (non-exact splits on whitespace and matches any term)
        //   • case sensitivity -> taken from the effective SearchOptions, not re-derived
        string pattern = options.UseRegex
            ? options.Query
            : (SearchQueryParser.BuildLiteralRegexPattern(options.Query, options.ExactMatch)
               ?? Regex.Escape(options.Query));

        var regexOptions = RegexOptions.CultureInvariant;
        if (!options.CaseSensitive) regexOptions |= RegexOptions.IgnoreCase;

        Regex regex;
        try
        {
            regex = new Regex(pattern, regexOptions);
        }
        catch (ArgumentException ex)
        {
            WriteError($"error: invalid replace pattern '{options.Query}': {ex.Message}", useColor);
            return;
        }

        // The replacement is always literal text — return it verbatim from the
        // evaluator so '$' groups in the replacement are not interpreted. This
        // matches the UI's literal replace behaviour for both literal and regex.
        string Eval(Match _) => replacement;

        // Group results by file path; archive entries are read-only and excluded.
        var distinctPaths = results
            .Select(r => r.FilePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        int archiveSkipped = distinctPaths.Count(ZipArchiveSearcher.IsArchivePath);
        var filePaths = distinctPaths
            .Where(p => !ZipArchiveSearcher.IsArchivePath(p))
            .ToList();

        if (archiveSkipped > 0)
            WriteError($"note: {archiveSkipped:N0} archive file(s) skipped (archives are read-only).", useColor);

        if (filePaths.Count == 0)
        {
            WriteError("No files to replace in.", useColor);
            return;
        }

        int totalReplacements = 0;
        int filesModified = 0;
        int errors = 0;

        foreach (var path in filePaths)
        {
            if (!File.Exists(path)) continue;

            try
            {
                Encoding encoding;
                string original;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                           FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan))
                {
                    encoding = Helpers.EncodingDetector.DetectEncoding(stream);
                    if (encoding is UTF8Encoding)
                        encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
                    using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true);
                    original = await reader.ReadToEndAsync().ConfigureAwait(false);
                    encoding = reader.CurrentEncoding;
                }

                int replaceCount = 0;
                var replaced = regex.Replace(original, m => { replaceCount++; return Eval(m); });

                if (replaceCount == 0) continue;

                totalReplacements += replaceCount;
                filesModified++;

                if (dryRun)
                {
                    WriteError($"  {path}: {replaceCount} replacement(s) (dry run)", useColor);
                    continue;
                }

                // Create backup unless --replace-no-backup
                if (!noBackup)
                {
                    var bakPath = path + ".yagubak";
                    if (!File.Exists(bakPath))
                        File.Copy(path, bakPath, overwrite: false);
                    else
                    {
                        int suffix = 2;
                        while (File.Exists($"{path}.yagubak-{suffix}")) suffix++;
                        File.Copy(path, $"{path}.yagubak-{suffix}", overwrite: false);
                    }
                }

                await File.WriteAllTextAsync(path, replaced, encoding).ConfigureAwait(false);
                WriteError($"  {path}: {replaceCount} replacement(s)", useColor);
            }
            catch (Exception ex)
            {
                errors++;
                WriteError($"  error: {path}: {ex.Message}", useColor);
            }
        }

        // Summary
        var mode = dryRun ? " (dry run)" : "";
        WriteError($"\nReplaced {totalReplacements:N0} occurrence(s) in {filesModified:N0} file(s){mode}.", useColor);
        if (errors > 0)
            WriteError($"  {errors} file(s) had errors.", useColor);
    }

    // -----------------------------------------------------------------------
    // Help text
    // -----------------------------------------------------------------------

    private static void PrintHelp()
    {
        Console.Out.WriteLine("""
            Yagu CLI Mode - Yet Another Grep Utility

            USAGE:
              Yagu.exe --cli --directory <path> PATTERN [OPTIONS]

            REQUIRED:
              --directory <path>          Directory to search recursively.

            PATTERN (positional arg, or explicit flag):
              --pattern <pattern>         Search pattern (literal by default).

            MATCHING:
              -e, --regex                 Treat pattern as a regular expression.
                  --no-regex              Treat pattern as a literal string (default).
              -s, --case-sensitive        Case-sensitive match.
              -i, --ignore-case           Case-insensitive match (default).
              -C, --context <n>           Context lines around each match (default: 3).
                  --search-mode <mode>    both | content | filenames | filename-then-content  (default: both)
                  --exact-match           Match whole words only (default).
                  --no-exact-match        Allow substring matches.

            SEMANTIC SEARCH (local AI):
              -SP,--semantic-pattern <text> Natural-language request that a local on-device model
                                          translates into the search flags below (directory, globs,
                                          dates, sizes, search mode) and then executes. Replaces the
                                          positional PATTERN; --directory becomes optional.
                                          The first time you use it, Yagu offers a choice of local
                                          models to download (recommended pick first). Decline and it
                                          falls back to a literal Traditional search of your text.
                  --semantic-model <alias> Force a specific Foundry Local model, by family alias
                                          (e.g. phi-4-mini) or exact variant id (e.g.
                                          Phi-4-mini-instruct-cuda-gpu:5). Default: auto-pick the
                                          best small model for this machine's hardware, preferring
                                          the less-quantized GPU build for accuracy. Skips the
                                          first-run model-download prompt.
                  --accept-model-download Auto-download the recommended model without prompting (for
                                          scripts / non-interactive consoles). Without it, a redirected
                                          console falls back to Traditional search.
                  --explain               With --semantic-pattern, print the interpreted search
                                          parameters and exit WITHOUT searching (a dry-run).
                  --semantic-batch <file> Translate a file of natural-language queries (one per line;
                                          blank lines and '#' comments ignored) through a SINGLE
                                          loaded model, printing one delimited --explain block per
                                          query. The model loads once and is reused for every query,
                                          so a query set (or a sweep across models) can be evaluated
                                          without the cold-load cost per call. Always a dry-run.

            FILE FILTERING:
              -g, --glob <glob>           Include files matching GLOB (repeatable).
                  --exclude-glob <glob>   Exclude files/dirs matching GLOB (repeatable).
                  --include-regex         Interpret include patterns as regex (default: glob).
                  --include-glob          Interpret include patterns as glob (default).
                  --exclude-regex         Interpret exclude patterns as regex (default: glob).
                  --exclude-glob-mode     Interpret exclude patterns as glob (default).
                  --min-filesize <size>   Skip files smaller than SIZE (e.g. 1M, 10K, 1G).
                  --max-filesize <size>   Skip files larger than SIZE (e.g. 50M, 10K, 1G).
                  --binary                Include binary files in search.
                  --no-binary             Skip binary files (default).
                  --skip-extensions <e>   Semicolon-separated extensions to skip (e.g. exe;dll).
                  --created-after <date>  Only include files created on/after this date (ISO 8601).
                  --created-before <date> Only include files created on/before this date.
                  --modified-after <date> Only include files modified on/after this date.
                  --modified-before <date> Only include files modified on/before this date.

            GITIGNORE:
                  --obey-gitignore        Respect .gitignore exclusions.
                  --no-obey-gitignore     Ignore .gitignore files (default).
                  --gitignore-precedence  .gitignore wins over include filters (default when enabled).
                  --no-gitignore-precedence  Include filters win over .gitignore.

            PERFORMANCE:
                  --threads <n>           Worker threads (0 = service-selected safe cap).
                  --memory-limit <MB>     Process memory cap in megabytes.
                  --memory-pressure <n>   System memory pressure threshold 0-100 (0 = disabled).
                  --sdk-channel-buffer <n> Everything SDK channel buffer size.
                  --file-lister-backend <n> File lister: 0=Auto, 1=SDK, 2=es.exe, 3=Managed.
                  --max-matches-per-file <n> Cap matches per file (0 = unlimited).
                  --max-depth <n>         Max directory recursion depth (0 = unlimited).

            ARCHIVE SEARCH:
                  --search-archives       Search inside ZIP-like archives.
                  --no-search-archives    Do not search inside archives (default).
                  --archive-extensions <e> Semicolon-separated archive extensions.

            CONTENT OPTIONS:
                  --hidden                Include files/folders with the Hidden attribute (default).
                  --no-hidden             Exclude hidden files/folders (system files always skipped).
                  --image-text            OCR image files and search the recognized text (off by default).
                  --no-image-text         Do not OCR images (default).
                  --allow-ocr-download    Permit the one-time OCR engine/model download (~365 MB) without
                                          an interactive prompt. Needed only on the very first OCR run of
                                          an edition that does not bundle the OCR components.
                  --ocr-engine <name>     OCR engine for --image-text: paddle (default) or tesseract.
                  --ocr-model <name>      PaddleSharp recognition model: EnglishV3, EnglishV4,
                                          ChineseV4, or ChineseV5 (default). Ignored by the tesseract engine.
                  --ocr-max-side <px>     PaddleSharp detection resolution (longest side, default 960;
                                          0 = unlimited/native). Ignored by the tesseract engine.

            ADMIN / SECURITY:
                  --no-admin-warning      Suppress the non-administrator privilege warning.
                  --exclude-admin-paths   Skip admin-protected paths (default when non-admin).
                  --no-exclude-admin-paths Include admin-protected paths.
                  --admin-protected-paths <s> Semicolon-separated admin-protected path segments.

            LOGGING:
                  --log-level <n>         File log level: -1=None, 0=Critical, 1=Warning, 2=Info, 3=Verbose.
                  --console-log-level <n> Console log level (same scale as --log-level).

            MISC:
                  --max-results <n>       Stop after N matches (default: 50000).

            EXPORT:
                  --export <path>         Export results to a file (triggers export mode).
                  --export-format <fmt>   Export format: html, json, csv (default: inferred from extension).
                  --export-context <n>    Context lines in exported report (default: 3, 0 = none).
                  --export-file-sizes     Include file sizes in export.
                  --export-modified-dates Include file modified dates in export.
                  --export-no-markers     Omit <match></match> markers in JSON/CSV exports.
                  --export-csv-embed-context  Embed context as multi-line CSV fields (RFC 4180).
                  --export-csv-pipe-separator Use pipe ( | ) to separate context lines instead of newlines.

            REPLACE:
              -r, --replace <text>        Replace matched text with <text> in all matched files.
                  --replace-no-backup     Do not create .yagubak backup files before replacing.
                  --replace-dry-run       Show what would be replaced without modifying any files.
                  --dry-run               Alias for --replace-dry-run.

            SORT:
                  --sort <key>            Sort results by: matches, date, size, name, directory, path (default: unsorted).
                  --sort-desc             Sort in descending order.
                  --sort-asc              Sort in ascending order (default).

            GROUP:
                  --group <key>           Group results by: directory, extension, size, modified,
                                          created, date, none. Grouping waits for the whole search
                                          to finish, then prints the results under group headers
                                          (it is never streamed live). Combine with --sort to order
                                          files within each group.
                  --group-desc            Reverse the natural group order (Z-A / oldest / largest first).
                  --group-asc             Natural group order: A-Z / recent / smallest first (default).

            SESSIONS (.yagu-session):
                  --save-session <path>   After a search completes, save its results to
                                          <path> as a .yagu-session file (rehydrate later).
                  --load-session <path>   Skip searching entirely; load a previously saved
                                          .yagu-session file and emit its results in
                                          ripgrep-compatible format. --directory and
                                          PATTERN are not required when loading a session.

            SETTINGS FILE:
                            If .yagu.json exists in the current working directory it is used as the
                            base configuration. If not, Yagu checks the running process launch
                            directory next, then falls back to global AppData settings. CLI flags
                            always override file-based settings.

            EXAMPLES (212):
              001. Basic search in the current folder
                  Does: Finds TODO anywhere under the current directory.
                  Cmd:  Yagu.exe --cli --directory . "TODO"

              002. Limit the number of matches
                  Does: Finds TODO under src and stops after 100 matches.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --max-results 100

              003. Match case exactly
                  Does: Finds uppercase TODO only, not todo or Todo.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --case-sensitive

              004. Ignore case differences
                  Does: Finds todo, TODO, Todo, and other casing variants.
                  Cmd:  Yagu.exe --cli --directory src "todo" --ignore-case

              005. Show match lines only
                  Does: Finds TODO without printing any surrounding context lines.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --context 0

              006. Show extra context
                  Does: Finds TODO and includes five lines around each match.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --context 5

              007. Use an explicit pattern flag
                  Does: Searches for TODO using --pattern instead of a positional term.
                  Cmd:  Yagu.exe --cli --directory src --pattern "TODO" --exact-match

              008. Allow substring matches
                  Does: Finds TODO even when it appears inside a larger word.
                  Cmd:  Yagu.exe --cli --directory src --pattern "TODO" --no-exact-match

              009. Search for two terms with regex
                  Does: Finds either TODO or FIXME in source files.
                  Cmd:  Yagu.exe --cli --directory src --regex "TODO|FIXME"

              010. Find class declarations
                  Does: Uses a regex to find class declarations for MainViewModel.
                  Cmd:  Yagu.exe --cli --directory src -e "class\\s+MainViewModel"

              011. Find async task methods
                  Does: Finds public async Task patterns and prints two context lines.
                  Cmd:  Yagu.exe --cli --directory src -e "public\\s+async\\s+Task" -C 2

              012. Find catch blocks case-sensitively
                  Does: Finds catch statements with exact casing.
                  Cmd:  Yagu.exe --cli --directory src -e "catch\\s*\\(" --case-sensitive

              013. Search file contents only
                  Does: Finds ResultStore inside file contents and ignores filename matches.
                  Cmd:  Yagu.exe --cli --directory src "ResultStore" --search-mode content

              014. Search filenames only
                  Does: Finds files whose names include SettingsWindow.
                  Cmd:  Yagu.exe --cli --directory src "SettingsWindow" --search-mode filenames

              015. Prefer filename matches first
                  Does: Checks filenames for preview before falling back to file contents.
                  Cmd:  Yagu.exe --cli --directory src "preview" --search-mode filename-then-content

              016. Search names and contents
                  Does: Searches both filenames and file contents for query.
                  Cmd:  Yagu.exe --cli --directory src "query" --search-mode both

              017. Search only C# files
                  Does: Finds TODO only in files matching *.cs.
                  Cmd:  Yagu.exe --cli --directory src "TODO" -g "*.cs"

              018. Search only XAML files
                  Does: Finds TODO only in files matching *.xaml.
                  Cmd:  Yagu.exe --cli --directory src "TODO" -g "*.xaml"

              019. Search C# and XAML files
                  Does: Finds TODO in either C# or XAML files.
                  Cmd:  Yagu.exe --cli --directory src "TODO" -g "*.cs" -g "*.xaml"

              020. Exclude build output folders
                  Does: Finds TODO while skipping bin and obj folders.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --exclude-glob "bin/**" --exclude-glob "obj/**"

              021. Search app C# files but skip generated code
                  Does: Searches Yagu C# files and ignores generated .g.cs files.
                  Cmd:  Yagu.exe --cli --directory src "TODO" -g "Yagu/**/*.cs" --exclude-glob "**/*.g.cs"

              022. Use regex include filters
                  Does: Finds TODO only in paths matching a test-file regex.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --include-regex -g ".*Tests.*\\.cs$"

              023. Use regex exclude filters
                  Does: Finds TODO while excluding Designer C# files with a regex.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --exclude-regex --exclude-glob ".*\\.Designer\\.cs$"

              024. Use glob include and exclude filters
                  Does: Uses glob mode and skips Generated folders.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --include-glob --exclude-glob "**/Generated/**"

              025. Force exclude patterns to glob mode
                  Does: Treats exclude patterns as globs and skips bin folders.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --exclude-glob-mode --exclude-glob "**/bin/**"

              026. Skip tiny files
                  Does: Finds TODO only in files at least 1 KB in size.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --min-filesize 1K

              027. Skip large files
                  Does: Finds TODO only in files no larger than 250 KB.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --max-filesize 250K

              028. Search a file-size band
                  Does: Finds TODO in files from 1 KB through 2 MB.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --min-filesize 1K --max-filesize 2M

              029. Include binary files
                  Does: Allows binary files to be considered during the search.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --binary

              030. Skip binary files
                  Does: Searches text-like files and skips binary files.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --no-binary

              031. Skip selected extensions
                  Does: Finds TODO while skipping exe, dll, and pdb files.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --skip-extensions "exe;dll;pdb"

              032. Search recently created files
                  Does: Finds TODO in files created on or after January 1, 2026.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --created-after 2026-01-01

              033. Search files created before a date
                  Does: Finds TODO in files created on or before June 1, 2026.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --created-before 2026-06-01

              034. Search a created-date range
                  Does: Finds TODO in files created from January 1 through June 1, 2026.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --created-after 2026-01-01 --created-before 2026-06-01

              035. Search recently modified files
                  Does: Finds TODO in files modified on or after May 1, 2026.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --modified-after 2026-05-01

              036. Search files modified before a date
                  Does: Finds TODO in files modified on or before June 1, 2026.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --modified-before 2026-06-01

              037. Search a modified-date range
                  Does: Finds TODO in files modified from May 1 through June 1, 2026.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --modified-after 2026-05-01 --modified-before 2026-06-01

              038. Respect .gitignore files
                  Does: Finds TODO while honoring .gitignore exclusions.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --obey-gitignore

              039. Ignore .gitignore files
                  Does: Finds TODO even in paths that .gitignore would exclude.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --no-obey-gitignore

              040. Let .gitignore win over includes
                  Does: Honors .gitignore exclusions even if an include filter matches.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --obey-gitignore --gitignore-precedence

              041. Let includes override .gitignore
                  Does: Allows include filters to bring back files excluded by .gitignore.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --obey-gitignore --no-gitignore-precedence

              042. Use service-selected thread count
                  Does: Uses Yagu's safe-cap content-search worker count.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --threads 0

              043. Use four worker threads
                  Does: Runs the search with four content-search workers.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --threads 4

              044. Limit process memory
                  Does: Caps Yagu's search memory target at 1024 MB.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --memory-limit 1024

              045. React to system memory pressure
                  Does: Enters memory-saving behavior when machine RAM usage reaches 85 percent.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --memory-pressure 85

              046. Tune the SDK channel buffer
                  Does: Uses a larger Everything SDK channel buffer.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --sdk-channel-buffer 4096

              047. Use automatic file listing
                  Does: Lets Yagu choose the best file-listing backend.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --file-lister-backend 0

              048. Use Everything SDK listing
                  Does: Forces the Everything SDK backend for file discovery.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --file-lister-backend 1

              049. Use es.exe listing
                  Does: Forces the es.exe backend for file discovery.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --file-lister-backend 2

              050. Use managed listing
                  Does: Forces Yagu's built-in managed file lister.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --file-lister-backend 3

              051. Cap matches per file
                  Does: Stops collecting matches from a file after 10 hits.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --max-matches-per-file 10

              052. Limit recursion depth
                  Does: Searches no deeper than three directory levels.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --max-depth 3

              053. Search with unlimited depth
                  Does: Allows recursive search without a depth cap.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --max-depth 0

              054. Search inside archives
                  Does: Looks for TODO inside supported archive files.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --search-archives

              055. Skip archive contents
                  Does: Searches normal files and ignores archive interiors.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --no-search-archives

              056. Search selected archive types
                  Does: Searches inside zip, 7z, and jar archive files.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --search-archives --archive-extensions "zip;7z;jar"

              057. Suppress admin warning
                  Does: Runs without showing the non-administrator warning.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --no-admin-warning

              058. Skip admin-protected paths
                  Does: Searches C:\ while avoiding known protected paths.
                  Cmd:  Yagu.exe --cli --directory C:\ "TODO" --exclude-admin-paths

              059. Include admin-protected paths
                  Does: Searches C:\ without skipping protected-path patterns.
                  Cmd:  Yagu.exe --cli --directory C:\ "TODO" --no-exclude-admin-paths

              060. Customize protected paths
                  Does: Supplies custom protected path segments to skip.
                  Cmd:  Yagu.exe --cli --directory C:\ "TODO" --admin-protected-paths "\\Windows\\System32\\config;\\System Volume Information"

              061. Disable file logging
                  Does: Searches while turning file logging off.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --log-level -1

              062. Use informational file logging
                  Does: Searches while writing informational file logs.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --log-level 2

              063. Disable console logging
                  Does: Searches with console log messages disabled.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --console-log-level -1

              064. Show critical console logs only
                  Does: Searches while allowing only critical console logs.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --console-log-level 0

              065. Sort by match count
                  Does: Sorts result groups by how many matches they contain.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --sort matches

              066. Sort by most matches first
                  Does: Shows files with the most TODO matches first.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --sort matches --sort-desc

              067. Sort newest files first
                  Does: Sorts matching files by modified date descending.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --sort date --sort-desc

              068. Sort by smallest files first
                  Does: Sorts matching files by size ascending.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --sort size --sort-asc

              069. Sort by filename
                  Does: Sorts matching files alphabetically by file name.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --sort name

              070. Sort by directory
                  Does: Sorts matching files alphabetically by directory name.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --sort directory

              071. Sort by full path
                  Does: Sorts matching files alphabetically by full path.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --sort path

              072. Export an HTML report
                  Does: Searches for TODO and writes an HTML report.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --export .\reports\todo.html

              073. Export JSON explicitly
                  Does: Searches for TODO and writes JSON output.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --export .\reports\todo.json --export-format json

              074. Export CSV explicitly
                  Does: Searches for TODO and writes CSV output.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --export .\reports\todo.csv --export-format csv

              075. Export without context
                  Does: Writes an HTML report with no surrounding context lines.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --export .\reports\todo.html --export-context 0

              076. Export with more context
                  Does: Writes an HTML report with five context lines per match.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --export .\reports\todo.html --export-context 5

              077. Include file sizes in export
                  Does: Adds file size information to the HTML report.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --export .\reports\todo.html --export-file-sizes

              078. Include modified dates in export
                  Does: Adds file modified dates to the HTML report.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --export .\reports\todo.html --export-modified-dates

              079. Export JSON without markers
                  Does: Writes JSON without match marker tags in context text.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --export .\reports\todo.json --export-no-markers

              080. Embed context in CSV fields
                  Does: Writes CSV with context stored as multiline fields.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --export .\reports\todo.csv --export-csv-embed-context

              081. Use pipe-separated CSV context
                  Does: Writes CSV with context lines separated by pipe characters.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --export .\reports\todo.csv --export-csv-pipe-separator

              082. Preview a replacement
                  Does: Shows what replacing oldName with newName would change.
                  Cmd:  Yagu.exe --cli --directory src "oldName" --replace "newName" --replace-dry-run

              083. Replace text with backups
                  Does: Replaces oldName with newName and creates backup files.
                  Cmd:  Yagu.exe --cli --directory src "oldName" --replace "newName"

              084. Replace text without backups
                  Does: Replaces oldName with newName without creating .yagubak files.
                  Cmd:  Yagu.exe --cli --directory src "oldName" --replace "newName" --replace-no-backup

              085. Preview regex-based replacement
                  Does: Finds oldName or oldValue patterns and previews replacing them.
                  Cmd:  Yagu.exe --cli --directory src -e "old(Name|Value)" --replace "newName" --replace-dry-run

              086. Use the dry-run alias
                  Does: Previews replacing oldFunction with newFunction using --dry-run.
                  Cmd:  Yagu.exe --cli --directory src "oldFunction" --replace "newFunction" --dry-run

              087. Save a session file
                  Does: Searches for TODO and saves the results for later reuse.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --save-session .\sessions\todo.yagu-session

              088. Load a session file
                  Does: Replays results from a saved session without searching again.
                  Cmd:  Yagu.exe --cli --load-session .\sessions\todo.yagu-session

              089. Load and sort a session
                  Does: Replays a saved session and sorts its results by path.
                  Cmd:  Yagu.exe --cli --load-session .\sessions\todo.yagu-session --sort path

              090. Investigate exceptions in C# files
                  Does: Finds Exception in C# files with context and newest files first.
                  Cmd:  Yagu.exe --cli --directory . "Exception" -g "*.cs" --context 4 --sort date --sort-desc

              091. Search recent log errors
                  Does: Finds ERROR in logs, ignores casing, and shows newest files first.
                  Cmd:  Yagu.exe --cli --directory logs "ERROR" --ignore-case --max-results 500 --sort date --sort-desc

              092. Search warnings or errors in logs
                  Does: Finds ERROR or WARN in logs changed since June 1, 2026.
                  Cmd:  Yagu.exe --cli --directory logs -e "ERROR|WARN" --context 1 --modified-after 2026-06-01

              093. Search a path with spaces
                  Does: Searches a project folder whose path contains spaces.
                  Cmd:  Yagu.exe --cli --directory "C:\Projects\My App" "connection string" --ignore-case

              094. Search large CSV exports
                  Does: Finds customerId in CSV files up to 50 MB.
                  Cmd:  Yagu.exe --cli --directory "D:\Data Exports" "customerId" -g "*.csv" --max-filesize 50M

              095. Find one match per C# file
                  Does: Finds using System in C# files and keeps one match per file.
                  Cmd:  Yagu.exe --cli --directory src "using System" -g "*.cs" --sort name --max-matches-per-file 1

              096. Tune a larger source search
                  Does: Searches C# files with gitignore, eight threads, and a 2 GB memory target.
                  Cmd:  Yagu.exe --cli --directory src "TODO" -g "*.cs" --obey-gitignore --threads 8 --memory-limit 2048

              097. Search archive-heavy source trees
                  Does: Searches TODO inside zip and jar archives up to five levels deep.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --search-archives --archive-extensions "zip;jar" --max-depth 5

              098. Export work-item markers
                  Does: Finds TODO, FIXME, or HACK comments and writes an HTML report.
                  Cmd:  Yagu.exe --cli --directory src -e "TODO|FIXME|HACK" -g "*.cs" --export .\reports\work-items.html --sort path

              099. Preview a replace and export findings
                  Does: Previews replacing obsolete with current and writes JSON results.
                  Cmd:  Yagu.exe --cli --directory src "obsolete" --replace "current" --replace-dry-run --export .\reports\obsolete.json --export-format json

              100. Quick sensitive-word sweep
                  Does: Finds password mentions while skipping build output and limiting noise.
                  Cmd:  Yagu.exe --cli --directory . "password" --ignore-case --exclude-glob "**/bin/**" --exclude-glob "**/obj/**" --max-results 50

              101. Search for API key patterns
                  Does: Finds api key naming variants while skipping the .git folder.
                  Cmd:  Yagu.exe --cli --directory . -e "api[_-]?key" --ignore-case --exclude-glob "**/.git/**" --context 2 --sort path

              102. Search solution files by name
                  Does: Finds solution files whose names contain Yagu.
                  Cmd:  Yagu.exe --cli --directory . "Yagu" --search-mode filenames -g "*.sln"

              103. Search project files
                  Does: Finds TargetFramework in C# project files.
                  Cmd:  Yagu.exe --cli --directory . "TargetFramework" -g "*.csproj"

              104. Search props and targets files
                  Does: Finds LangVersion in MSBuild props and targets files.
                  Cmd:  Yagu.exe --cli --directory . "LangVersion" -g "*.props" -g "*.targets"

              105. Search JSON configuration
                  Does: Finds featureFlag in JSON config files only.
                  Cmd:  Yagu.exe --cli --directory config "featureFlag" -g "*.json"

              106. Search XML configuration
                  Does: Finds connectionStrings in XML configuration files.
                  Cmd:  Yagu.exe --cli --directory config "connectionStrings" -g "*.xml"

              107. Search Markdown docs
                  Does: Finds installation mentions in Markdown files.
                  Cmd:  Yagu.exe --cli --directory docs "installation" -g "*.md"

              108. Search scripts
                  Does: Finds Invoke-RestMethod in PowerShell scripts.
                  Cmd:  Yagu.exe --cli --directory scripts "Invoke-RestMethod" -g "*.ps1"

              109. Search batch files
                  Does: Finds robocopy usage in command scripts.
                  Cmd:  Yagu.exe --cli --directory scripts "robocopy" -g "*.cmd" -g "*.bat"

              110. Search TypeScript files
                  Does: Finds useEffect calls in TypeScript and TSX files.
                  Cmd:  Yagu.exe --cli --directory src "useEffect" -g "*.ts" -g "*.tsx"

              111. Search JavaScript files
                  Does: Finds console.log calls in JavaScript files.
                  Cmd:  Yagu.exe --cli --directory web "console.log" -g "*.js"

              112. Find GUID-like values
                  Does: Uses regex to find GUID-looking identifiers.
                  Cmd:  Yagu.exe --cli --directory . -e "[0-9a-fA-F]{8}-[0-9a-fA-F-]{27}"

              113. Find IPv4 addresses
                  Does: Uses regex to find IPv4-looking addresses in logs.
                  Cmd:  Yagu.exe --cli --directory logs -e "\\b\\d{1,3}(\\.\\d{1,3}){3}\\b"

              114. Find email-like text
                  Does: Uses regex to find email-shaped strings in docs.
                  Cmd:  Yagu.exe --cli --directory docs -e "[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+"

              115. Find HTTP URLs
                  Does: Uses regex to find HTTP or HTTPS URLs.
                  Cmd:  Yagu.exe --cli --directory . -e "https?://[^\\s\"]+"

              116. Find Windows absolute paths
                  Does: Uses regex to find drive-rooted Windows paths.
                  Cmd:  Yagu.exe --cli --directory docs -e "[A-Z]:\\\\[^\\r\\n\"]+"

              117. Search recent errors with context
                  Does: Finds TimeoutException in recent log files with three context lines.
                  Cmd:  Yagu.exe --cli --directory logs "TimeoutException" --modified-after 2026-06-01 -C 3

              118. Search old logs before archiving
                  Does: Finds ERROR in logs modified before 2026-01-01.
                  Cmd:  Yagu.exe --cli --directory logs "ERROR" --modified-before 2026-01-01

              119. Search small log files only
                  Does: Finds WARN in log files no larger than 5 MB.
                  Cmd:  Yagu.exe --cli --directory logs "WARN" -g "*.log" --max-filesize 5M

              120. Search large data files only
                  Does: Finds customerId in CSV files that are at least 10 MB.
                  Cmd:  Yagu.exe --cli --directory data "customerId" -g "*.csv" --min-filesize 10M

              121. Search shallow docs only
                  Does: Finds migration in docs without recursing deeper than two levels.
                  Cmd:  Yagu.exe --cli --directory docs "migration" --max-depth 2

              122. Search generated code only
                  Does: Finds partial class in generated C# files.
                  Cmd:  Yagu.exe --cli --directory src "partial class" -g "**/*.g.cs"

              123. Exclude generated code
                  Does: Finds partial class while skipping generated C# files.
                  Cmd:  Yagu.exe --cli --directory src "partial class" --exclude-glob "**/*.g.cs"

              124. Exclude package folders
                  Does: Searches TODO while skipping node_modules and packages folders.
                  Cmd:  Yagu.exe --cli --directory . "TODO" --exclude-glob "**/node_modules/**" --exclude-glob "**/packages/**"

              125. Exclude source control data
                  Does: Searches TODO while skipping .git and .svn folders.
                  Cmd:  Yagu.exe --cli --directory . "TODO" --exclude-glob "**/.git/**" --exclude-glob "**/.svn/**"

              126. Search only test files
                  Does: Finds Assert in files with Tests in the path.
                  Cmd:  Yagu.exe --cli --directory . "Assert" --include-regex -g ".*Tests.*"

              127. Search non-test files
                  Does: Finds Assert while excluding paths with Tests in the name.
                  Cmd:  Yagu.exe --cli --directory . "Assert" --exclude-regex --exclude-glob ".*Tests.*"

              128. Search exact identifier
                  Does: Finds ResultStore as a whole-word identifier.
                  Cmd:  Yagu.exe --cli --directory src "ResultStore" --exact-match

              129. Search partial identifier
                  Does: Finds Store inside longer identifiers.
                  Cmd:  Yagu.exe --cli --directory src "Store" --no-exact-match

              130. Search with no result cap
                  Does: Allows all matches by setting max results to zero.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --max-results 0

              131. Search with one match per file
                  Does: Finds TODO but keeps only the first match from each file.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --max-matches-per-file 1

              132. Search with no per-file cap
                  Does: Allows unlimited matches per file.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --max-matches-per-file 0

              133. Search only recently created tests
                  Does: Finds Fact attributes in test files created after 2026-05-01.
                  Cmd:  Yagu.exe --cli --directory tests "[Fact]" -g "*.cs" --created-after 2026-05-01

              134. Search config by modification window
                  Does: Finds enabled in JSON files modified during May 2026.
                  Cmd:  Yagu.exe --cli --directory config "enabled" -g "*.json" --modified-after 2026-05-01 --modified-before 2026-06-01

              135. Search archive names only
                  Does: Finds backup in archive filenames without searching contents.
                  Cmd:  Yagu.exe --cli --directory backups "backup" --search-mode filenames --no-search-archives

              136. Search inside NuGet packages
                  Does: Searches package archives by treating nupkg as an archive extension.
                  Cmd:  Yagu.exe --cli --directory packages "TargetFramework" --search-archives --archive-extensions "nupkg"

              137. Search JAR contents
                  Does: Searches manifest text inside jar files.
                  Cmd:  Yagu.exe --cli --directory libs "Implementation-Version" --search-archives --archive-extensions "jar"

              138. Search ZIP backups
                  Does: Searches TODO inside zip files in the backup folder.
                  Cmd:  Yagu.exe --cli --directory backups "TODO" --search-archives --archive-extensions "zip"

              139. Search with managed listing and gitignore
                  Does: Uses the managed file lister while respecting .gitignore.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --file-lister-backend 3 --obey-gitignore

              140. Search with es.exe and no gitignore
                  Does: Uses es.exe discovery and ignores .gitignore rules.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --file-lister-backend 2 --no-obey-gitignore

              141. Search with SDK backend and threads
                  Does: Uses the Everything SDK backend with six workers.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --file-lister-backend 1 --threads 6

              142. Export sorted HTML
                  Does: Searches TODO, sorts by path, and exports an HTML report.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --sort path --export .\reports\todo.html

              143. Export newest-first JSON
                  Does: Searches ERROR and exports newest-first JSON results.
                  Cmd:  Yagu.exe --cli --directory logs "ERROR" --sort date --sort-desc --export .\reports\errors.json

              144. Export compact CSV
                  Does: Exports TODO to CSV without context lines.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --export .\reports\todo.csv --export-context 0

              145. Export CSV with file metadata
                  Does: Exports TODO to CSV with file sizes and modified dates.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --export .\reports\todo.csv --export-file-sizes --export-modified-dates

              146. Export regex findings
                  Does: Finds TODO or FIXME and writes a JSON report.
                  Cmd:  Yagu.exe --cli --directory src -e "TODO|FIXME" --export .\reports\work.json --export-format json

              147. Save sorted session
                  Does: Searches TODO, sorts by path, and saves a session file.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --sort path --save-session .\sessions\todo.yagu-session

              148. Save recent-error session
                  Does: Searches recent errors and saves the result set for later review.
                  Cmd:  Yagu.exe --cli --directory logs "ERROR" --modified-after 2026-06-01 --save-session .\sessions\errors.yagu-session

              149. Load session as CSV source
                  Does: Loads a saved session and exports it to CSV.
                  Cmd:  Yagu.exe --cli --load-session .\sessions\todo.yagu-session --export .\reports\todo.csv

              150. Load session as HTML report
                  Does: Loads saved results and writes an HTML report.
                  Cmd:  Yagu.exe --cli --load-session .\sessions\todo.yagu-session --export .\reports\todo.html

              151. Load session newest first
                  Does: Loads saved results and sorts them by date descending.
                  Cmd:  Yagu.exe --cli --load-session .\sessions\todo.yagu-session --sort date --sort-desc

              152. Preview literal rename
                  Does: Previews replacing OldService with NewService in C# files.
                  Cmd:  Yagu.exe --cli --directory src "OldService" -g "*.cs" --replace "NewService" --dry-run

              153. Replace literal with backups
                  Does: Replaces OldService with NewService and keeps backup files.
                  Cmd:  Yagu.exe --cli --directory src "OldService" --replace "NewService"

              154. Replace literal without backups
                  Does: Replaces OldService without creating backup files.
                  Cmd:  Yagu.exe --cli --directory src "OldService" --replace "NewService" --replace-no-backup

              155. Preview regex cleanup
                  Does: Previews replacing multiple whitespace runs with one space.
                  Cmd:  Yagu.exe --cli --directory docs -e "\\s{2,}" --replace " " --replace-dry-run

              156. Replace in Markdown only
                  Does: Replaces old product text with new text in Markdown files.
                  Cmd:  Yagu.exe --cli --directory docs "Old Product" -g "*.md" --replace "New Product"

              157. Replace with no context output
                  Does: Previews a replacement while suppressing context lines.
                  Cmd:  Yagu.exe --cli --directory src "obsolete" --replace "current" --dry-run --context 0

              158. Replace with case-sensitive matching
                  Does: Replaces TODO only when the case exactly matches.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --case-sensitive --replace "DONE" --dry-run

              159. Replace substring matches
                  Does: Previews replacing temp even inside longer words.
                  Cmd:  Yagu.exe --cli --directory src "temp" --no-exact-match --replace "temporary" --dry-run

              160. Replace and export audit
                  Does: Previews replacements and exports the matching rows to HTML.
                  Cmd:  Yagu.exe --cli --directory src "legacy" --replace "modern" --dry-run --export .\reports\legacy.html

              161. Replace in recent files only
                  Does: Previews replacements only in files modified after 2026-06-01.
                  Cmd:  Yagu.exe --cli --directory src "old" --replace "new" --dry-run --modified-after 2026-06-01

              162. Find nullable annotations
                  Does: Finds nullable reference type annotations in C# files.
                  Cmd:  Yagu.exe --cli --directory src -e "\\w+\\?" -g "*.cs"

              163. Find async void methods
                  Does: Finds async void declarations in C# files.
                  Cmd:  Yagu.exe --cli --directory src -e "async\\s+void" -g "*.cs"

              164. Find public fields
                  Does: Finds public field-like declarations in C# files.
                  Cmd:  Yagu.exe --cli --directory src -e "public\\s+\\w+\\s+\\w+;" -g "*.cs"

              165. Find hardcoded localhost URLs
                  Does: Finds localhost URLs in source files.
                  Cmd:  Yagu.exe --cli --directory src -e "https?://localhost(:\\d+)?"

              166. Find TODO comments exactly
                  Does: Finds line comments that contain TODO.
                  Cmd:  Yagu.exe --cli --directory src -e "//.*TODO" -g "*.cs"

              167. Find XML comments
                  Does: Finds summary XML doc comments in C# files.
                  Cmd:  Yagu.exe --cli --directory src "<summary>" -g "*.cs"

              168. Find XAML controls
                  Does: Finds Button elements in XAML files.
                  Cmd:  Yagu.exe --cli --directory src "<Button" -g "*.xaml"

              169. Find resource keys
                  Does: Finds StaticResource references in XAML files.
                  Cmd:  Yagu.exe --cli --directory src "StaticResource" -g "*.xaml"

              170. Find package references
                  Does: Finds PackageReference entries in project files.
                  Cmd:  Yagu.exe --cli --directory . "PackageReference" -g "*.csproj"

              171. Find project references
                  Does: Finds ProjectReference entries in project files.
                  Cmd:  Yagu.exe --cli --directory . "ProjectReference" -g "*.csproj"

              172. Find Docker base images
                  Does: Finds FROM lines in Dockerfiles.
                  Cmd:  Yagu.exe --cli --directory . -e "^FROM\\s+" -g "Dockerfile*"

              173. Find YAML image references
                  Does: Finds image fields in YAML deployment files.
                  Cmd:  Yagu.exe --cli --directory . -e "image:\\s*" -g "*.yml" -g "*.yaml"

              174. Find ports in YAML
                  Does: Finds port declarations in YAML files.
                  Cmd:  Yagu.exe --cli --directory . -e "port:\\s*\\d+" -g "*.yml" -g "*.yaml"

              175. Find Terraform resources
                  Does: Finds Terraform resource blocks.
                  Cmd:  Yagu.exe --cli --directory infra -e "resource\\s+\"" -g "*.tf"

              176. Find Bicep resources
                  Does: Finds resource declarations in Bicep files.
                  Cmd:  Yagu.exe --cli --directory infra -e "^resource\\s+" -g "*.bicep"

              177. Find ARM template parameters
                  Does: Finds parameters sections in ARM JSON templates.
                  Cmd:  Yagu.exe --cli --directory infra "parameters" -g "*.json"

              178. Find SQL table creation
                  Does: Finds CREATE TABLE statements in SQL files.
                  Cmd:  Yagu.exe --cli --directory database -e "CREATE\\s+TABLE" -g "*.sql"

              179. Find stored procedures
                  Does: Finds procedure creation statements in SQL files.
                  Cmd:  Yagu.exe --cli --directory database -e "CREATE\\s+PROCEDURE" -g "*.sql"

              180. Find migration scripts
                  Does: Finds migration mentions in SQL and Markdown files.
                  Cmd:  Yagu.exe --cli --directory database "migration" -g "*.sql" -g "*.md"

              181. Find CSV headers
                  Does: Finds files with customer_id in CSV content.
                  Cmd:  Yagu.exe --cli --directory data "customer_id" -g "*.csv" --context 0

              182. Find tab-separated columns
                  Does: Finds status columns in TSV files.
                  Cmd:  Yagu.exe --cli --directory data "status" -g "*.tsv" --context 0

              183. Find binary-adjacent metadata
                  Does: Searches metadata files while skipping common binaries.
                  Cmd:  Yagu.exe --cli --directory assets "license" --skip-extensions "png;jpg;gif;webp"

              184. Search media sidecars
                  Does: Finds title in JSON sidecar files next to media assets.
                  Cmd:  Yagu.exe --cli --directory assets "title" -g "*.json"

              185. Search logs with low noise
                  Does: Finds error in log files but returns at most 20 matches.
                  Cmd:  Yagu.exe --cli --directory logs "error" --ignore-case --max-results 20

              186. Search logs by newest path order
                  Does: Finds error and sorts matching log files by modified date.
                  Cmd:  Yagu.exe --cli --directory logs "error" --sort date --sort-desc

              187. Search one directory level
                  Does: Finds README only in the top directory and direct children.
                  Cmd:  Yagu.exe --cli --directory . "README" --max-depth 1

              188. Search by filename and export
                  Does: Finds files with README in the name and exports HTML.
                  Cmd:  Yagu.exe --cli --directory . "README" --search-mode filenames --export .\reports\readme.html

              189. Search content and save session
                  Does: Searches for Exception and saves the results to a session file.
                  Cmd:  Yagu.exe --cli --directory src "Exception" --save-session .\sessions\exceptions.yagu-session

              190. Load session and export JSON
                  Does: Converts a saved exception session to JSON.
                  Cmd:  Yagu.exe --cli --load-session .\sessions\exceptions.yagu-session --export .\reports\exceptions.json

              191. Load session and limit output
                  Does: Loads a session and emits only the first 25 matches.
                  Cmd:  Yagu.exe --cli --load-session .\sessions\exceptions.yagu-session --max-results 25

              192. Search code comments
                  Does: Finds TODO comments in C# files with one context line.
                  Cmd:  Yagu.exe --cli --directory src "TODO" -g "*.cs" --context 1

              193. Search docs excluding drafts
                  Does: Finds release in docs while excluding draft folders.
                  Cmd:  Yagu.exe --cli --directory docs "release" --exclude-glob "**/drafts/**"

              194. Search release notes
                  Does: Finds breaking changes in Markdown release notes.
                  Cmd:  Yagu.exe --cli --directory docs "breaking" -g "*.md" --sort name

              195. Search build output intentionally
                  Does: Finds version text inside bin folders by explicitly including them.
                  Cmd:  Yagu.exe --cli --directory . "Version" -g "**/bin/**"

              196. Search with include overriding gitignore
                  Does: Searches generated files even if .gitignore excludes them.
                  Cmd:  Yagu.exe --cli --directory . "Generated" -g "**/Generated/**" --obey-gitignore --no-gitignore-precedence

              197. Search with gitignore precedence
                  Does: Keeps ignored generated folders excluded even with broad includes.
                  Cmd:  Yagu.exe --cli --directory . "Generated" -g "**/*" --obey-gitignore --gitignore-precedence

              198. Search non-admin friendly C drive
                  Does: Searches C:\ while skipping protected paths and hiding the admin warning.
                  Cmd:  Yagu.exe --cli --directory C:\ "TODO" --exclude-admin-paths --no-admin-warning

              199. Search C drive with tight limits
                  Does: Searches C:\ for TODO with shallow depth and a small result cap.
                  Cmd:  Yagu.exe --cli --directory C:\ "TODO" --max-depth 2 --max-results 100

              200. Search source with strict resource limits
                  Does: Searches TODO with two workers, 512 MB memory, and 50 results.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --threads 2 --memory-limit 512 --max-results 50

              201. Search source and write a compact audit
                  Does: Finds TODO in C# files, exports JSON, and omits match markers.
                  Cmd:  Yagu.exe --cli --directory src "TODO" -g "*.cs" --export .\reports\todo-audit.json --export-no-markers

              202. Semantic search in plain English
                  Does: Lets a local AI model translate the request into search flags, then runs it.
                  Cmd:  Yagu.exe --cli --semantic-pattern "find png files on the C drive modified in the past year, ignore mov files"

              203. Preview a semantic translation without searching
                  Does: Prints the interpreted directory, globs, and date filters, then exits.
                  Cmd:  Yagu.exe --cli --semantic-pattern "large pdf reports created since January" --explain

              204. Semantic search with a specific local model
                  Does: Forces a chosen Foundry Local model to interpret the request.
                  Cmd:  Yagu.exe --cli --semantic-pattern "config files under the repo" --semantic-model "qwen2.5-1.5b-instruct-generic-cpu"

              205. Semantic search in a script (auto-download the recommended model)
                  Does: Skips the first-run model picker and downloads the recommended model, then runs.
                  Cmd:  Yagu.exe --cli --semantic-pattern "log files changed this week" --accept-model-download

              206. Exclude hidden files and folders
                  Does: Searches TODO while skipping items with the Windows Hidden attribute.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --no-hidden

              207. Force-include hidden files
                  Does: Searches secrets including dotfiles/hidden folders (overrides a disabled default).
                  Cmd:  Yagu.exe --cli --directory . "API_KEY" --hidden

              208. Group results by directory
                  Does: Waits for the search to finish, then prints matches under per-folder headers.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --group directory

              209. Group by file type, biggest files first within each group
                  Does: Groups matches by extension and sorts files inside each group by size descending.
                  Cmd:  Yagu.exe --cli --directory src "TODO" --group extension --sort size --sort-desc

              210. Group by modified date, oldest groups first
                  Does: Groups matches into modified-date ranges and reverses the natural recent-first order.
                  Cmd:  Yagu.exe --cli --directory logs "ERROR" --group modified --group-desc

              211. Search text inside images (OCR)
                  Does: OCRs image files with the default PaddleSharp engine and matches the recognized text.
                  Cmd:  Yagu.exe --cli --directory screenshots "invoice" --image-text

              212. Search image text with the Tesseract engine
                  Does: Forces the Tesseract OCR engine instead of the default PaddleSharp.
                  Cmd:  Yagu.exe --cli --directory scans "TOTAL" --image-text --ocr-engine tesseract

            EXIT CODES:
              0   One or more matches found.
              1   No matches found.
              2   Usage error.
              130 Cancelled (Ctrl+C).
            """);
    }
}

// ---------------------------------------------------------------------------
// Ripgrep-compatible output writer
// ---------------------------------------------------------------------------

/// <summary>
/// Writes search results to a <see cref="TextWriter"/> (stdout) in the same
/// format that ripgrep uses:
/// <list type="bullet">
///   <item>File path header, bold when ANSI is supported.</item>
///   <item><c>LINE:</c> prefix for match lines.</item>
///   <item><c>LINE-</c> prefix for context lines.</item>
///   <item><c>--</c> separator between non-adjacent match groups in the same file.</item>
///   <item>Blank line between files.</item>
/// </list>
/// </summary>
// ---------------------------------------------------------------------------
// Persistent progress indicator (stderr, interactive only)
// ---------------------------------------------------------------------------

internal sealed class ProgressLine
{
    private static readonly char[] Spinner = ['|', '/', '-', '\\'];
    private int    _frame;
    private string _current = "";
    private bool   _visible;
    private readonly bool _color;

    private const string Dim   = "\x1B[2m";
    private const string Reset = "\x1B[0m";
    private const string Clear = "\r\x1B[K";

    public ProgressLine(bool color) => _color = color;

    /// <summary>Called on each SearchEvent.Progress — advances spinner and redraws.</summary>
    public void Update(SearchProgress p)
    {
        _frame   = (_frame + 1) % Spinner.Length;
        _current = Build(p);
        Draw();
    }

    /// <summary>Clear the line so stdout match output doesn't overlap.</summary>
    public void Hide()
    {
        if (_visible)
        {
            Console.Error.Write(Clear);
            _visible = false;
        }
    }

    /// <summary>Redraw the last progress line after match output.</summary>
    public void Show()
    {
        if (_current.Length > 0) Draw();
    }

    /// <summary>Clear permanently (search ended).</summary>
    public void Dismiss() => Hide();

    private void Draw()
    {
        Console.Error.Write(Clear + (_color ? Dim + _current + Reset : _current));
        _visible = true;
    }

    private string Build(SearchProgress p)
    {
        char spin = Spinner[_frame];
        // Only show X/Y when TotalFiles is a genuine pre-known total (e.g. from Everything SDK).
        // When no pre-known total exists, TotalFiles == discoveredTotal == FilesScanned,
        // so the X/Y format would show 50K/50K which looks wrong — fall back to "Searching..."
        bool knownTotal = p.TotalFiles > p.FilesScanned;
        return knownTotal
            ? $"{spin} Searching {p.FilesScanned:N0} / {p.TotalFiles:N0} files  ·  {p.MatchesFound:N0} match(es)"
            : $"{spin} Searching... {p.FilesScanned:N0} files  ·  {p.MatchesFound:N0} match(es)";
    }
}

internal sealed class RipgrepWriter
{
    // ANSI escape sequences — matches ripgrep defaults
    private const string BoldMagenta = "\x1B[1;35m";  // file path header
    private const string BoldGreen   = "\x1B[1;32m";  // match line numbers
    private const string BoldBlue    = "\x1B[1;34m";  // context line numbers + separator
    private const string BoldRed     = "\x1B[1;31m";  // matched text within the line
    private const string Reset       = "\x1B[0m";

    private readonly TextWriter _out;
    private readonly bool _color;

    private string? _currentFile;
    private int     _lastLine;       // last line number written for the current file
    private bool    _wroteMatchInFile;

    public int TotalMatches { get; private set; }

    public RipgrepWriter(TextWriter @out, bool color)
    {
        _out   = @out;
        _color = color;
    }

    public void Add(SearchResult result)
    {
        bool sameFile = string.Equals(_currentFile, result.FilePath, StringComparison.OrdinalIgnoreCase);

        // Dedup: multiple matches on the same line produce separate SearchResults;
        // ripgrep prints each line once, so skip any that repeat a line already written.
        if (sameFile && result.LineNumber > 0 && result.LineNumber == _lastLine)
            return;

        TotalMatches++;

        // ---- File header ------------------------------------------------
        if (!sameFile)
        {
            if (_currentFile is not null)
                _out.WriteLine(); // blank line between files

            _out.WriteLine(_color
                ? $"{BoldMagenta}{result.FilePath}{Reset}"
                : result.FilePath);

            _currentFile       = result.FilePath;
            _lastLine          = 0;
            _wroteMatchInFile  = false;
        }

        // ---- Context before ---------------------------------------------
        var before = result.NumberedBefore;
        if (before.Count > 0)
        {
            int firstCtx = before[0].LineNum;
            if (_wroteMatchInFile && firstCtx > _lastLine + 1)
                WriteSep();

            foreach (var ctx in before)
            {
                if (ctx.LineNum > _lastLine)
                {
                    WriteCtx(ctx.LineNum, ctx.Text);
                    _lastLine = ctx.LineNum;
                }
            }
        }
        else if (_wroteMatchInFile && result.LineNumber > _lastLine + 1)
        {
            WriteSep();
        }

        // ---- Match line -------------------------------------------------
        WriteMatch(result.LineNumber, result.MatchLine, result.MatchStartColumn, result.MatchLength);
        _lastLine         = result.LineNumber;
        _wroteMatchInFile = true;

        // ---- Context after ----------------------------------------------
        foreach (var ctx in result.NumberedAfter)
        {
            WriteCtx(ctx.LineNum, ctx.Text);
            _lastLine = ctx.LineNum;
        }
    }

    public void Flush() => _out.Flush();

    // ---- Private helpers -----------------------------------------------

    private void WriteMatch(int line, string text, int matchStart, int matchLength)
    {
        if (_color)
        {
            string highlighted = HighlightMatch(text, matchStart, matchLength);
            _out.WriteLine($"{BoldGreen}{line}{Reset}:{highlighted}");
        }
        else
        {
            _out.WriteLine($"{line}:{text}");
        }
    }

    private void WriteCtx(int line, string text)
    {
        if (_color)
            _out.WriteLine($"{BoldBlue}{line}{Reset}-{text}");
        else
            _out.WriteLine($"{line}-{text}");
    }

    private void WriteSep()
    {
        _out.WriteLine(_color ? $"{BoldBlue}--{Reset}" : "--");
    }

    private string HighlightMatch(string text, int start, int length)
    {
        // Guard against out-of-range offsets (e.g. evicted results or filename matches).
        if (!_color || length <= 0 || start < 0 || start >= text.Length)
            return text;
        int end = Math.Min(start + length, text.Length);
        return text[..start] + BoldRed + text[start..end] + Reset + text[end..];
    }
}

// ---------------------------------------------------------------------------
// CLI argument parser
// ---------------------------------------------------------------------------

/// <summary>Parsed command-line arguments for <c>--cli</c> mode.</summary>
internal sealed class CliArgs
{
    private static readonly char[] s_extensionSeparators = [';', ','];

    public string?          Directory    { get; private set; }
    public string?          Pattern      { get; private set; }
    public bool?            CaseSensitive { get; private set; }
    public bool?            UseRegex     { get; private set; }
    public int?             ContextLines { get; private set; }
    public List<string>     IncludeGlobs { get; } = [];
    public List<string>     ExcludeGlobs { get; } = [];
    public long?            MinFileSizeBytes { get; private set; }
    public long?            MaxFileSizeBytes { get; private set; }
    public int?             MaxResults   { get; private set; }
    public bool?            SkipBinary   { get; private set; }
    public List<string>     SkipExtensions { get; } = [];
    public SearchMode?      SearchMode   { get; private set; }
    public int?             Parallelism  { get; private set; }
    public int?             MemoryLimitMB { get; private set; }
    public int?             MemoryPressurePercent { get; private set; }
    public int?             SdkChannelBufferSize { get; private set; }
    public int?             LogLevelIndex { get; private set; }
    public int?             ConsoleLogLevelIndex { get; private set; }
    public int?             FileListerBackendIndex { get; private set; }
    public bool?            SearchInsideArchives { get; private set; }
    public bool?            SearchHiddenFiles { get; private set; }
    public bool?            SearchImageText { get; private set; }
    public string?          ImageOcrEngine { get; private set; }
    public string?          ImageOcrModel { get; private set; }
    public int?             ImageOcrMaxSide { get; private set; }
    public bool             AllowOcrDownload { get; private set; }
    public string?          ArchiveExtensions { get; private set; }
    public bool?            ExcludeAdminProtectedPaths { get; private set; }
    public string?          AdminProtectedPathSegments { get; private set; }
    public bool?            ObeyGitignore { get; private set; }
    public bool?            GitignoreTakesPrecedence { get; private set; }
    public int?             IncludeFilterModeIndex { get; private set; }
    public int?             ExcludeFilterModeIndex { get; private set; }
    public int?             MaxMatchesPerFile { get; private set; }
    public int?             MaxSearchDepth { get; private set; }
    public bool?            ExactMatch { get; private set; }
    public DateTimeOffset?  CreatedAfter { get; private set; }
    public DateTimeOffset?  CreatedBefore { get; private set; }
    public DateTimeOffset?  ModifiedAfter { get; private set; }
    public DateTimeOffset?  ModifiedBefore { get; private set; }
    public bool             SuppressAdminWarning { get; private set; }
    public bool             ShowHelp     { get; private set; }

    // Semantic search (--semantic-pattern): natural-language request translated by the local model.
    public string?          SemanticPattern { get; private set; }
    public string?          SemanticModel { get; private set; }
    public bool             AcceptModelDownload { get; private set; }
    public bool             Explain { get; private set; }

    // Semantic batch evaluation (--semantic-batch <file>): translate many natural-language queries
    // (one per line) through a SINGLE loaded model, emitting one delimited --explain block per query.
    // Keeping the model resident across queries avoids the ~20 s cold-load cost on every call, which is
    // what makes benchmarking a model across a query set (or many models) practical.
    public string?          SemanticBatch { get; private set; }

    // Export options
    public string?          ExportPath { get; private set; }
    public string?          ExportFormat { get; private set; } // html, json, csv
    public int?             ExportContextLines { get; private set; }
    public bool             ExportFileSizes { get; private set; }
    public bool             ExportModifiedDates { get; private set; }
    public bool             ExportNoMarkers { get; private set; }
    public bool             ExportCsvEmbedContext { get; private set; }
    public bool             ExportCsvPipeSeparator { get; private set; }

    // Replace options
    public string?          ReplaceText { get; private set; }
    public bool             ReplaceNoBackup { get; private set; }
    public bool             ReplaceDryRun { get; private set; }

    // Sort options
    public string?          SortBy { get; private set; } // matches, date, size, name, directory, path
    public bool             SortDescending { get; private set; }

    // Group options (post-search, applied after the scan completes)
    public string?          GroupBy { get; private set; } // directory, extension, size, modified, created, date
    public bool             GroupDescending { get; private set; }

    // Session (.yagu-session) file options
    public string?          LoadSessionPath { get; private set; }
    public string?          SaveSessionPath { get; private set; }

    private CliArgs() { }

    public static CliArgs Parse(string[] raw)
    {
        var a = new CliArgs();
        int i = 0;
        while (i < raw.Length)
        {
            var tok = raw[i];

            if (Eq(tok, "--cli"))                          { i++; continue; }
            if (Eq(tok, "--help", "-help", "-h", "--h", "-?", "/?", "?", "/help", "/h"))
                { a.ShowHelp = true; i++; continue; }
            if (Eq(tok, "--case-sensitive", "-s"))         { a.CaseSensitive = true; i++; continue; }
            if (Eq(tok, "--ignore-case", "-i"))            { a.CaseSensitive = false; i++; continue; }
            if (Eq(tok, "--regex", "-e"))                  { a.UseRegex = true; i++; continue; }
            if (Eq(tok, "--no-regex"))                     { a.UseRegex = false; i++; continue; }
            if (Eq(tok, "--no-binary"))                    { a.SkipBinary = true; i++; continue; }
            if (Eq(tok, "--binary"))                       { a.SkipBinary = false; i++; continue; }
            if (Eq(tok, "--no-admin-warning"))             { a.SuppressAdminWarning = true; i++; continue; }
            if (Eq(tok, "--search-archives"))                { a.SearchInsideArchives = true; i++; continue; }
            if (Eq(tok, "--no-search-archives"))             { a.SearchInsideArchives = false; i++; continue; }
            if (Eq(tok, "--hidden", "--search-hidden"))      { a.SearchHiddenFiles = true; i++; continue; }
            if (Eq(tok, "--no-hidden", "--no-search-hidden")) { a.SearchHiddenFiles = false; i++; continue; }
            if (Eq(tok, "--image-text", "--search-image-text", "--ocr")) { a.SearchImageText = true; i++; continue; }
            if (Eq(tok, "--no-image-text", "--no-search-image-text", "--no-ocr")) { a.SearchImageText = false; i++; continue; }
            if (Eq(tok, "--allow-ocr-download")) { a.AllowOcrDownload = true; i++; continue; }
            if (Eq(tok, "--exclude-admin-paths"))            { a.ExcludeAdminProtectedPaths = true; i++; continue; }
            if (Eq(tok, "--no-exclude-admin-paths"))         { a.ExcludeAdminProtectedPaths = false; i++; continue; }
            if (Eq(tok, "--obey-gitignore", "--gitignore"))  { a.ObeyGitignore = true; i++; continue; }
            if (Eq(tok, "--no-obey-gitignore", "--no-gitignore")) { a.ObeyGitignore = false; i++; continue; }
            if (Eq(tok, "--gitignore-precedence"))           { a.GitignoreTakesPrecedence = true; i++; continue; }
            if (Eq(tok, "--no-gitignore-precedence"))        { a.GitignoreTakesPrecedence = false; i++; continue; }
            if (Eq(tok, "--exact-match"))                    { a.ExactMatch = true; i++; continue; }
            if (Eq(tok, "--no-exact-match", "--substring"))  { a.ExactMatch = false; i++; continue; }
            if (Eq(tok, "--explain"))                        { a.Explain = true; i++; continue; }
            if (Eq(tok, "--accept-model-download", "--yes-download")) { a.AcceptModelDownload = true; i++; continue; }
            if (Eq(tok, "--include-regex"))                  { a.IncludeFilterModeIndex = 1; i++; continue; }
            if (Eq(tok, "--include-glob"))                   { a.IncludeFilterModeIndex = 0; i++; continue; }
            if (Eq(tok, "--exclude-regex"))                  { a.ExcludeFilterModeIndex = 1; i++; continue; }
            if (Eq(tok, "--exclude-glob-mode"))              { a.ExcludeFilterModeIndex = 0; i++; continue; }

            string? v;
            if (TryGetVal(raw, ref i, out v, "--directory", "--dir"))
                { a.Directory = v.Trim('"'); continue; }
            if (TryGetVal(raw, ref i, out v, "--pattern", "-p"))
                { a.Pattern = v; continue; }
            if (TryGetVal(raw, ref i, out v, "--semantic-pattern", "-SP"))
                { a.SemanticPattern = v; continue; }
            if (TryGetVal(raw, ref i, out v, "--semantic-model"))
                { a.SemanticModel = v.Trim(); continue; }
            if (TryGetVal(raw, ref i, out v, "--semantic-batch"))
                { a.SemanticBatch = v.Trim('"'); continue; }
            if (TryGetVal(raw, ref i, out v, "--glob", "-g"))
                { a.IncludeGlobs.Add(v); continue; }
            if (TryGetVal(raw, ref i, out v, "--exclude-glob", "--exclude"))
                { a.ExcludeGlobs.Add(v); continue; }
            if (TryGetVal(raw, ref i, out v, "--skip-extensions"))
            {
                foreach (var ext in v.Split(s_extensionSeparators,
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    a.SkipExtensions.Add(ext.TrimStart('.'));
                continue;
            }
            if (TryGetVal(raw, ref i, out v, "--min-filesize"))
                { a.MinFileSizeBytes = ParseFileSize(v); continue; }
            if (TryGetVal(raw, ref i, out v, "--max-filesize"))
                { a.MaxFileSizeBytes = ParseFileSize(v); continue; }
            if (TryGetVal(raw, ref i, out v, "--search-mode"))
            {
                a.SearchMode = v.ToLowerInvariant() switch
                {
                    "content"                        => Models.SearchMode.Content,
                    "filenames" or "filename" or "files" => Models.SearchMode.FileNames,
                    "filename-then-content" or "filenames-then-content" or "file-name-then-content" or "names-then-content"
                                                     => Models.SearchMode.FileNameThenContent,
                    _                                => Models.SearchMode.Both,
                };
                continue;
            }

            if (TryGetInt(raw, ref i, out int n, "--context", "-C"))    { a.ContextLines  = n; continue; }
            if (TryGetInt(raw, ref i, out n, "--max-results"))           { a.MaxResults    = n; continue; }
            if (TryGetInt(raw, ref i, out n, "--threads", "--parallelism")) { a.Parallelism = n; continue; }
            if (TryGetInt(raw, ref i, out n, "--memory-limit"))          { a.MemoryLimitMB = n; continue; }
            if (TryGetInt(raw, ref i, out n, "--memory-pressure"))       { a.MemoryPressurePercent = n; continue; }
            if (TryGetInt(raw, ref i, out n, "--sdk-channel-buffer"))    { a.SdkChannelBufferSize = n; continue; }
            if (TryGetInt(raw, ref i, out n, "--log-level"))             { a.LogLevelIndex = n; continue; }
            if (TryGetInt(raw, ref i, out n, "--console-log-level"))     { a.ConsoleLogLevelIndex = n; continue; }
            if (TryGetInt(raw, ref i, out n, "--file-lister-backend"))   { a.FileListerBackendIndex = n; continue; }
            if (TryGetInt(raw, ref i, out n, "--max-matches-per-file"))  { a.MaxMatchesPerFile = n; continue; }
            if (TryGetInt(raw, ref i, out n, "--max-depth"))             { a.MaxSearchDepth = n; continue; }
            if (TryGetVal(raw, ref i, out v, "--archive-extensions"))    { a.ArchiveExtensions = v; continue; }
            if (TryGetVal(raw, ref i, out v, "--ocr-engine"))            { a.ImageOcrEngine = AppSettings.NormalizeImageOcrEngine(v); continue; }
            if (TryGetVal(raw, ref i, out v, "--ocr-model"))             { a.ImageOcrModel = AppSettings.NormalizeImageOcrModel(v); continue; }
            if (TryGetInt(raw, ref i, out n, "--ocr-max-side"))          { a.ImageOcrMaxSide = AppSettings.NormalizeImageOcrMaxSide(n); continue; }
            if (TryGetVal(raw, ref i, out v, "--admin-protected-paths")) { a.AdminProtectedPathSegments = v; continue; }
            if (TryGetVal(raw, ref i, out v, "--created-after"))         { if (DateTimeOffset.TryParse(v, out var d)) a.CreatedAfter = d; continue; }
            if (TryGetVal(raw, ref i, out v, "--created-before"))        { if (DateTimeOffset.TryParse(v, out var d)) a.CreatedBefore = d; continue; }
            if (TryGetVal(raw, ref i, out v, "--modified-after"))        { if (DateTimeOffset.TryParse(v, out var d)) a.ModifiedAfter = d; continue; }
            if (TryGetVal(raw, ref i, out v, "--modified-before"))       { if (DateTimeOffset.TryParse(v, out var d)) a.ModifiedBefore = d; continue; }

            // Export options
            if (TryGetVal(raw, ref i, out v, "--export"))               { a.ExportPath = v.Trim('"'); continue; }
            if (TryGetVal(raw, ref i, out v, "--export-format"))        { a.ExportFormat = v.ToLowerInvariant(); continue; }
            if (TryGetInt(raw, ref i, out n, "--export-context"))        { a.ExportContextLines = n; continue; }
            if (Eq(tok, "--export-file-sizes"))                          { a.ExportFileSizes = true; i++; continue; }
            if (Eq(tok, "--export-modified-dates"))                      { a.ExportModifiedDates = true; i++; continue; }
            if (Eq(tok, "--export-no-markers"))                          { a.ExportNoMarkers = true; i++; continue; }
            if (Eq(tok, "--export-csv-embed-context"))                   { a.ExportCsvEmbedContext = true; i++; continue; }
            if (Eq(tok, "--export-csv-pipe-separator"))                  { a.ExportCsvPipeSeparator = true; i++; continue; }

            // Replace options
            if (TryGetVal(raw, ref i, out v, "--replace", "-r"))        { a.ReplaceText = v; continue; }
            if (Eq(tok, "--replace-no-backup"))                          { a.ReplaceNoBackup = true; i++; continue; }
            if (Eq(tok, "--replace-dry-run", "--dry-run"))               { a.ReplaceDryRun = true; i++; continue; }

            // Sort options
            if (TryGetVal(raw, ref i, out v, "--sort"))
            {
                a.SortBy = v.ToLowerInvariant();
                if (a.SortBy is not ("matches" or "count" or "date" or "modified"
                    or "size" or "name" or "filename" or "directory" or "dir" or "path"))
                {
                    Console.Error.WriteLine(
                        $"warning: unknown sort key '{v}' - results will be unsorted. " +
                        "Valid keys: matches, date, size, name, directory, path.");
                    a.SortBy = null;
                }
                continue;
            }
            if (Eq(tok, "--sort-desc", "--sort-descending"))              { a.SortDescending = true; i++; continue; }
            if (Eq(tok, "--sort-asc", "--sort-ascending"))                { a.SortDescending = false; i++; continue; }

            // Group options
            if (TryGetVal(raw, ref i, out v, "--group"))
            {
                a.GroupBy = NormalizeGroupKey(v);
                if (a.GroupBy is null)
                {
                    Console.Error.WriteLine(
                        $"warning: unknown group key '{v}' - results will not be grouped. " +
                        "Valid keys: directory, extension, size, modified, created, date, none.");
                }
                else if (a.GroupBy.Length == 0)
                {
                    a.GroupBy = null; // "none" — explicit no-grouping
                }
                continue;
            }
            if (Eq(tok, "--group-desc", "--group-descending"))            { a.GroupDescending = true; i++; continue; }
            if (Eq(tok, "--group-asc", "--group-ascending"))              { a.GroupDescending = false; i++; continue; }

            // Session file options
            if (TryGetVal(raw, ref i, out v, "--load-session"))           { a.LoadSessionPath = v.Trim('"'); continue; }
            if (TryGetVal(raw, ref i, out v, "--save-session"))           { a.SaveSessionPath = v.Trim('"'); continue; }

            // Positional: first non-flag is the pattern
            if (!tok.StartsWith('-') && a.Pattern is null)
                { a.Pattern = tok; i++; continue; }

            // Unknown flag — warn and skip
            Console.Error.WriteLine($"warning: unknown flag '{tok}' ignored.");
            i++;
        }
        return a;
    }

    /// <summary>
    /// Drops semantic mode to a literal Traditional search when the user declines the model download:
    /// reuses the typed <see cref="SemanticPattern"/> as the search pattern. An empty directory is
    /// preserved (it means "search all drives"). Explicit <c>--pattern</c>/<c>--directory</c> values
    /// are preserved.
    /// </summary>
    internal void FallBackSemanticToTraditional()
    {
        if (string.IsNullOrWhiteSpace(Pattern) && !string.IsNullOrWhiteSpace(SemanticPattern))
            Pattern = SemanticPattern;
        // An empty Directory is intentionally preserved: it means "search all drives".
    }

    /// <summary>
    /// Folds a model-produced <see cref="SemanticSearchOverlay"/> into these args. Only fills fields
    /// the user did NOT set explicitly on the command line, so explicit flags always win over the
    /// model's interpretation.
    /// </summary>
    internal void ApplySemanticOverlay(SemanticSearchOverlay overlay)
    {
        if (overlay is null) return;

        if (string.IsNullOrWhiteSpace(Directory) && !string.IsNullOrWhiteSpace(overlay.Directory))
            Directory = overlay.Directory!.Trim('"');
        if (string.IsNullOrWhiteSpace(Pattern) && !string.IsNullOrWhiteSpace(overlay.Query))
            Pattern = overlay.Query;

        if (overlay.SearchMode is { } sm && SearchMode is null) SearchMode = sm;
        if (overlay.CaseSensitive is { } cs && CaseSensitive is null) CaseSensitive = cs;
        if (overlay.UseRegex is { } rx && UseRegex is null) UseRegex = rx;
        if (overlay.ExactMatch is { } em && ExactMatch is null) ExactMatch = em;

        if (overlay.IncludeGlobs is { } inc && IncludeGlobs.Count == 0)
        {
            IncludeGlobs.AddRange(inc);
            IncludeFilterModeIndex ??= 0;
        }
        if (overlay.ExcludeGlobs is { } exc && ExcludeGlobs.Count == 0)
        {
            ExcludeGlobs.AddRange(exc);
            ExcludeFilterModeIndex ??= 0;
        }

        if (overlay.MinFileSizeBytes is { } mn && MinFileSizeBytes is null) MinFileSizeBytes = mn;
        if (overlay.MaxFileSizeBytes is { } mx && MaxFileSizeBytes is null) MaxFileSizeBytes = mx;
        if (overlay.CreatedAfterDate is { } ca && CreatedAfter is null) CreatedAfter = ca;
        if (overlay.CreatedBeforeDate is { } cb && CreatedBefore is null) CreatedBefore = cb;
        if (overlay.ModifiedAfterDate is { } ma && ModifiedAfter is null) ModifiedAfter = ma;
        if (overlay.ModifiedBeforeDate is { } mb && ModifiedBefore is null) ModifiedBefore = mb;
        if (overlay.MaxSearchDepth is { } depth && MaxSearchDepth is null) MaxSearchDepth = depth < 0 ? 0 : depth;
        if (overlay.ObeyGitignore is { } gi && ObeyGitignore is null) ObeyGitignore = gi;
        if (overlay.SearchInsideArchives is { } arc && SearchInsideArchives is null) SearchInsideArchives = arc;
        // The plan targets known-binary extensions (.exe/.com/.cpl…): stop skipping binary content so a
        // content search over those files actually reads them (mirrors the GUI's binary auto-enable).
        if (overlay.SearchBinary == true && SkipBinary is null) SkipBinary = false;
        if (overlay.SearchHiddenFiles is { } sh && SearchHiddenFiles is null) SearchHiddenFiles = sh;
        if (overlay.SearchImageText is { } sit && SearchImageText is null) SearchImageText = sit;

        // Sort/group: only fill from the model when the user did not set them explicitly.
        if (overlay.SortBy is { } sortKey && SortBy is null)
        {
            SortBy = sortKey;
            if (overlay.SortDescending is { } desc) SortDescending = desc;
        }
        if (overlay.GroupBy is { } groupKey && GroupBy is null)
        {
            GroupBy = groupKey;
            if (overlay.GroupDescending is { } gdesc) GroupDescending = gdesc;
        }
    }

    // ---- Helpers -------------------------------------------------------

    /// <summary>
    /// Normalizes a <c>--group</c> key to its canonical form. Returns the canonical key for a known
    /// group field, an empty string for "none" (explicit no-grouping), or null for an unknown key.
    /// </summary>
    internal static string? NormalizeGroupKey(string raw)
    {
        string v = raw.Trim().ToLowerInvariant().Replace("_", "-").Replace(" ", "-");
        return v switch
        {
            "none" or "off" or "no" or "ungrouped" => "",
            "directory" or "dir" or "folder" or "path" or "location" => "directory",
            "extension" or "ext" or "type" or "filetype" or "file-type" or "kind" => "extension",
            "size" or "filesize" or "file-size" => "size",
            "modified" or "date-modified" or "datemodified" or "modified-date" or "lastmodified" or "last-modified" => "modified",
            "created" or "date-created" or "datecreated" or "created-date" or "creation" => "created",
            "date" or "date-range" or "daterange" or "modified-created" or "modified+created" => "date",
            _ => null,
        };
    }

    private static bool Eq(string tok, params string[] candidates) =>
        candidates.Any(c => string.Equals(tok, c, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Tries to consume a value for <paramref name="flags"/> from <paramref name="args"/>
    /// at position <paramref name="i"/>.  Supports both <c>--flag value</c> and
    /// <c>--flag=value</c> forms.  Advances <paramref name="i"/> on success.
    /// </summary>
    private static bool TryGetVal(string[] args, ref int i, out string value, params string[] flags)
    {
        var tok = args[i];
        foreach (var flag in flags)
        {
            // --flag=value
            var prefix = flag + "=";
            if (tok.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                value = tok[prefix.Length..];
                i++;
                return true;
            }
            // --flag value
            if (string.Equals(tok, flag, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                value = args[i + 1];
                i += 2;
                return true;
            }
        }
        value = string.Empty;
        return false;
    }

    private static bool TryGetInt(string[] args, ref int i, out int value, params string[] flags)
    {
        int saved = i;
        if (TryGetVal(args, ref i, out var s, flags) && int.TryParse(s, out value))
            return true;
        i = saved;   // restore on parse failure so the caller can emit a warning
        value = 0;
        return false;
    }

    private static long ParseFileSize(string s)
    {
        s = s.Trim();
        if (string.IsNullOrEmpty(s)) return 0;
        long mul = 1;
        if      (s.EndsWith("G", StringComparison.OrdinalIgnoreCase)) { mul = 1024L * 1024 * 1024; s = s[..^1]; }
        else if (s.EndsWith("M", StringComparison.OrdinalIgnoreCase)) { mul = 1024L * 1024;         s = s[..^1]; }
        else if (s.EndsWith("K", StringComparison.OrdinalIgnoreCase)) { mul = 1024L;                s = s[..^1]; }
        else if (s.EndsWith("B", StringComparison.OrdinalIgnoreCase)) {                             s = s[..^1]; }
        return long.TryParse(s, out var n) ? n * mul : 0;
    }
}
