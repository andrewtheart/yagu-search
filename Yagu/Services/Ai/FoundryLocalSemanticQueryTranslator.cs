using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.AI.Foundry.Local;
using Betalgo.Ranul.OpenAI.ObjectModels.RequestModels;
using Yagu.Models;

namespace Yagu.Services.Ai;

/// <summary>
/// <see cref="ISemanticQueryTranslator"/> backed by Foundry Local's in-process chat API. Runs the
/// model entirely on-device (no HTTP server, no network egress of the user's query). The execution
/// provider and model are downloaded lazily on first use; subsequent calls reuse the loaded model.
/// </summary>
public sealed class FoundryLocalSemanticQueryTranslator : ISemanticQueryTranslator, IAsyncDisposable
{
    private const string PromptResourceName = "Yagu.Services.Ai.Prompts.SemanticSearchSystemPrompt.prompt.md";
    private const string LogSource = "Semantic.Translator";

    private bool _enabled;
    private string? _preferredAlias;
    private IReadOnlyList<DeviceType> _deviceOrder;
    private bool _hasGpu = true;
    private bool _hasNpu = true;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly Lazy<string> _systemPromptTemplate = new(LoadSystemPromptTemplate);

    private ICatalog? _catalog;
    private OpenAIChatClient? _chatClient;
    private IModel? _model;
    private bool _initialized;

    /// <summary>The alias of the model that was actually selected/loaded, available after the first
    /// successful <see cref="TranslateAsync"/> call. Useful for diagnostics (which model ran).</summary>
    public string? SelectedModelAlias { get; private set; }

    public FoundryLocalSemanticQueryTranslator(bool enabled, string? modelOverrideAlias = null, string? devicePreferenceOrder = null)
    {
        _enabled = enabled;
        _preferredAlias = string.IsNullOrWhiteSpace(modelOverrideAlias) ? null : modelOverrideAlias.Trim();
        _deviceOrder = FoundryModelSelector.ParseDeviceOrder(devicePreferenceOrder);
        LogService.Instance.Verbose(LogSource,
            $"Created (enabled={_enabled}, preferredAlias={_preferredAlias ?? "<auto>"}, deviceOrder={string.Join(">", _deviceOrder)}).");
    }

    public bool IsAvailable => _enabled;

    public void SetEnabled(bool enabled)
    {
        if (_enabled == enabled) return;
        _enabled = enabled;
        LogService.Instance.Info(LogSource, $"Semantic translation {(enabled ? "enabled" : "disabled")} at runtime.");
        // Drop any loaded model so a later re-enable re-selects from scratch.
        ResetLoadedModel();
    }

    public void SetDevicePreferenceOrder(string? order)
    {
        var parsed = FoundryModelSelector.ParseDeviceOrder(order);
        if (parsed.SequenceEqual(_deviceOrder)) return;
        _deviceOrder = parsed;
        LogService.Instance.Info(LogSource, $"Device preference order set to {string.Join(">", _deviceOrder)}; will re-select on next translation.");
        // Force the next translation to re-select the model variant for the new device order.
        ResetLoadedModel();
    }

    public void SetModelOverride(string? modelAlias)
    {
        string? normalized = string.IsNullOrWhiteSpace(modelAlias) ? null : modelAlias.Trim();
        if (string.Equals(normalized, _preferredAlias, StringComparison.OrdinalIgnoreCase)) return;
        _preferredAlias = normalized;
        LogService.Instance.Info(LogSource, $"Model override set to '{normalized ?? "<auto>"}'; will re-select on next translation.");
        ResetLoadedModel();
    }

    public void SetAvailableAccelerators(bool hasGpu, bool hasNpu)
    {
        if (_hasGpu == hasGpu && _hasNpu == hasNpu) return;
        _hasGpu = hasGpu;
        _hasNpu = hasNpu;
        LogService.Instance.Info(LogSource, $"Available accelerators set (GPU={hasGpu}, NPU={hasNpu}); will re-select on next translation.");
        // A model variant for an absent accelerator must not stay loaded; force re-selection.
        ResetLoadedModel();
    }

    /// <summary>The execution devices this machine can actually run, per Yagu's capability detector.
    /// CPU is always present; GPU/NPU only when detected. Passed to the model selector so a build for an
    /// absent accelerator is never chosen (it could load via DirectML yet crash during inference).</summary>
    private HashSet<DeviceType> AvailableDevices()
    {
        var set = new HashSet<DeviceType> { DeviceType.CPU };
        if (_hasGpu) set.Add(DeviceType.GPU);
        if (_hasNpu) set.Add(DeviceType.NPU);
        return set;
    }

    /// <summary>
    /// Available physical memory (MB) used to cap AUTO model selection on CPU-only machines, where the
    /// model's weights and KV cache live in system RAM. Returns null on accelerated machines (the model
    /// runs in GPU/NPU memory, so a system-RAM budget would wrongly exclude it) or when the query fails.
    /// Prevents auto-selecting a model that downloads and loads, then OOMs ("bad allocation") while
    /// allocating the large prompt's token sequences during generation.
    /// </summary>
    private int? AvailableMemoryBudgetMb()
    {
        if (_hasGpu || _hasNpu) return null;
        try
        {
            var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref status))
            {
                ulong availMb = status.ullAvailPhys / (1024UL * 1024UL);
                return (int)Math.Min(availMb, (ulong)int.MaxValue);
            }
        }
        catch { /* P/Invoke unavailable; disable the memory guard */ }
        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    /// <summary>Drops the cached chat client/model so the next translation re-selects and reloads.
    /// The previously loaded Foundry model stays resident until process exit (no async unload here),
    /// which is acceptable for a rare settings change.</summary>
    private void ResetLoadedModel()
    {
        _chatClient = null;
        _model = null;
        _initialized = false;
    }

    public async Task<SemanticTranslationResult> TranslateAsync(
        string naturalLanguageQuery,
        SemanticTranslationContext context,
        IProgress<SemanticTranslationProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!_enabled)
        {
            LogService.Instance.Verbose(LogSource, "Translation skipped: semantic search is disabled in settings.");
            return SemanticTranslationResult.Fail("Semantic search is disabled in settings.");
        }
        if (string.IsNullOrWhiteSpace(naturalLanguageQuery))
        {
            LogService.Instance.Verbose(LogSource, "Translation skipped: empty query.");
            return SemanticTranslationResult.Fail("Enter a request to translate.");
        }

        context ??= new SemanticTranslationContext();

        var log = LogService.Instance;
        string trimmedQuery = naturalLanguageQuery.Trim();
        log.Info(LogSource, $"Translation requested (queryLength={trimmedQuery.Length}).");
        if (log.IsVerboseEnabled)
            log.Verbose(LogSource,
                $"Query='{trimmedQuery}', defaultDir='{context.DefaultDirectory ?? "<none>"}', now={context.Now:O}.");

        var totalStopwatch = Stopwatch.StartNew();

        OpenAIChatClient chat;
        try
        {
            chat = await EnsureChatClientAsync(progress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            log.Info(LogSource, "Translation canceled while preparing the local model.");
            throw;
        }
        catch (Exception ex)
        {
            log.Warning(LogSource, "Could not start the local AI model.", ex);
            return SemanticTranslationResult.Fail($"Could not start the local AI model: {ex.Message}");
        }

        progress?.Report(new SemanticTranslationProgress { Stage = SemanticTranslationStage.Interpreting });

        string raw;
        var inferenceStopwatch = Stopwatch.StartNew();
        try
        {
            string systemPrompt = BuildSystemPrompt(context);
            var messages = new List<ChatMessage>
            {
                ChatMessage.FromSystem(systemPrompt),
                ChatMessage.FromUser(trimmedQuery),
            };
            log.Verbose(LogSource,
                $"Sending chat completion (model={SelectedModelAlias ?? "<unknown>"}, systemPromptChars={systemPrompt.Length}).");

            var response = await chat.CompleteChatAsync(messages, cancellationToken).ConfigureAwait(false);
            raw = response?.Choices?.FirstOrDefault()?.Message?.Content ?? string.Empty;
            inferenceStopwatch.Stop();
            log.Info(LogSource,
                $"Model responded in {inferenceStopwatch.ElapsedMilliseconds} ms (model={SelectedModelAlias ?? "<unknown>"}, responseChars={raw.Length}).");
        }
        catch (OperationCanceledException)
        {
            log.Info(LogSource, "Translation canceled during model inference.");
            throw;
        }
        catch (Exception ex)
        {
            log.Warning(LogSource, $"The local AI model failed to respond (model={SelectedModelAlias ?? "<unknown>"}).", ex);
            return SemanticTranslationResult.Fail($"The local AI model failed to respond: {ex.Message}");
        }

        if (log.IsVerboseEnabled)
            log.Verbose(LogSource, $"Raw model output:\n{raw}");

        if (string.IsNullOrWhiteSpace(raw))
        {
            log.Warning(LogSource, $"The local AI model returned an empty response (model={SelectedModelAlias ?? "<unknown>"}).");
            return SemanticTranslationResult.Fail("The local AI model returned an empty response.", raw);
        }

        if (!TryParsePlan(raw, out var plan, out var parseError))
        {
            log.Warning(LogSource, $"Could not parse a search plan from model output: {parseError}");
            return SemanticTranslationResult.Fail(parseError ?? "Could not understand the model output.", raw);
        }

        totalStopwatch.Stop();
        log.Info(LogSource,
            $"Translation succeeded in {totalStopwatch.ElapsedMilliseconds} ms (model={SelectedModelAlias ?? "<unknown>"}).");
        if (log.IsVerboseEnabled)
            log.Verbose(LogSource, $"Parsed plan: {DescribePlan(plan!)}");

        return SemanticTranslationResult.Ok(plan!, raw);
    }

    private async Task<OpenAIChatClient> EnsureChatClientAsync(
        IProgress<SemanticTranslationProgress>? progress, CancellationToken cancellationToken)
    {
        if (_initialized && _chatClient is not null) return _chatClient;

        var log = LogService.Instance;
        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized && _chatClient is not null) return _chatClient;

            var catalog = await EnsureCatalogLockedAsync(progress, cancellationToken).ConfigureAwait(false);

            log.Verbose(LogSource, $"Selecting model (preferredAlias={_preferredAlias ?? "<auto>"}, deviceOrder={string.Join(">", _deviceOrder)}).");
            var model = await FoundryModelSelector.SelectAsync(catalog, _preferredAlias, _deviceOrder, AvailableDevices(), AvailableMemoryBudgetMb(), cancellationToken).ConfigureAwait(false);
            if (model is null)
            {
                log.Warning(LogSource, "No compatible local model is available for this machine.");
                throw new InvalidOperationException("No compatible local model is available for this machine.");
            }
            log.Info(LogSource, $"Selected model '{model.Alias}'.");

            bool cached = await model.IsCachedAsync(cancellationToken).ConfigureAwait(false);
            log.Verbose(LogSource, $"Model '{model.Alias}' cached on disk: {cached}.");
            // Only surface the download UI when the model isn't already on disk. A cached model
            // makes DownloadAsync a fast no-op, so flashing "Downloading model — 100%" on every
            // launch wrongly implies a re-download that never actually happens.
            if (!cached)
            {
                log.Info(LogSource, $"Downloading model '{model.Alias}' (first-time setup).");
                progress?.Report(new SemanticTranslationProgress
                {
                    Stage = SemanticTranslationStage.DownloadingModel,
                    Detail = model.Alias,
                    Percent = 0,
                });
            }
            var downloadStopwatch = Stopwatch.StartNew();
            await model.DownloadAsync(
                pct =>
                {
                    if (!cached)
                        progress?.Report(new SemanticTranslationProgress
                        {
                            Stage = SemanticTranslationStage.DownloadingModel,
                            Detail = model.Alias,
                            Percent = pct,
                        });
                },
                cancellationToken).ConfigureAwait(false);
            downloadStopwatch.Stop();
            if (!cached)
                log.Info(LogSource, $"Model '{model.Alias}' downloaded in {downloadStopwatch.ElapsedMilliseconds} ms.");

            progress?.Report(new SemanticTranslationProgress
            {
                Stage = SemanticTranslationStage.LoadingModel,
                Detail = model.Alias,
            });
            log.Info(LogSource, $"Loading model '{model.Alias}'.");
            var loadStopwatch = Stopwatch.StartNew();
            await model.LoadAsync(cancellationToken).ConfigureAwait(false);
            loadStopwatch.Stop();
            log.Info(LogSource, $"Model '{model.Alias}' loaded in {loadStopwatch.ElapsedMilliseconds} ms.");

            var chat = await model.GetChatClientAsync(cancellationToken).ConfigureAwait(false);
            ConfigureForDeterministicJson(chat);
            log.Verbose(LogSource,
                "Chat client configured for deterministic JSON (temperature=0, topP=1, maxTokens=512, frequencyPenalty=0.6, presencePenalty=0.3).");

            _model = model;
            _chatClient = chat;
            _initialized = true;
            _preferredAlias = model.Alias;
            SelectedModelAlias = model.Alias;
            log.Info(LogSource, $"Local model ready: '{model.Alias}'.");
            return chat;
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    /// Initializes the Foundry Local manager, downloads/registers the hardware execution providers,
    /// and resolves the model catalog (cached). Assumes <see cref="_initLock"/> is held.
    /// </summary>
    private async Task<ICatalog> EnsureCatalogLockedAsync(
        IProgress<SemanticTranslationProgress>? progress, CancellationToken cancellationToken)
    {
        if (_catalog is not null) return _catalog;

        var log = LogService.Instance;
        progress?.Report(new SemanticTranslationProgress { Stage = SemanticTranslationStage.Initializing });

        if (!FoundryLocalManager.IsInitialized)
        {
            log.Info(LogSource, "Initializing Foundry Local manager (AppName=Yagu).");
            var config = new Configuration { AppName = "Yagu" };
            await FoundryLocalManager.CreateAsync(config, FoundryLoggerAdapter.Instance, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            log.Verbose(LogSource, "Foundry Local manager already initialized; reusing it.");
        }

        var manager = FoundryLocalManager.Instance;

        // Execution providers are cached on disk by Foundry Local after the first run. The
        // register step still runs each process, but performs no download when already cached.
        // Surface the "Downloading AI runtime" stage only while a real download is in flight
        // (callback reports < 100%), so repeat launches don't appear to re-download every time.
        log.Info(LogSource, "Ensuring hardware execution providers are downloaded and registered.");
        var epStopwatch = Stopwatch.StartNew();
        await manager.DownloadAndRegisterEpsAsync(
            (epName, pct) =>
            {
                if (pct < 100)
                    progress?.Report(new SemanticTranslationProgress
                    {
                        Stage = SemanticTranslationStage.DownloadingExecutionProviders,
                        Detail = epName,
                        Percent = pct,
                    });
            },
            cancellationToken).ConfigureAwait(false);
        epStopwatch.Stop();
        log.Info(LogSource, $"Execution providers ready in {epStopwatch.ElapsedMilliseconds} ms.");

        log.Verbose(LogSource, "Resolving Foundry Local model catalog.");
        _catalog = await manager.GetCatalogAsync(cancellationToken).ConfigureAwait(false);
        log.Verbose(LogSource, "Model catalog resolved.");
        return _catalog;
    }

    public async Task<IReadOnlyList<SemanticModelOption>> ListModelOptionsAsync(
        IProgress<SemanticTranslationProgress>? progress, CancellationToken cancellationToken)
    {
        if (!_enabled) return Array.Empty<SemanticModelOption>();

        var log = LogService.Instance;
        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            log.Info(LogSource, "Listing locally-runnable model options.");
            var catalog = await EnsureCatalogLockedAsync(progress, cancellationToken).ConfigureAwait(false);
            var models = await catalog.ListModelsAsync(cancellationToken).ConfigureAwait(false);
            if (models is null || models.Count == 0)
            {
                log.Warning(LogSource, "Model catalog returned no models when listing options.");
                return Array.Empty<SemanticModelOption>();
            }

            // Restrict to text-chat models and rank them so the recommended pick can be flagged.
            var usable = models
                .Where(m => m is not null && FoundryModelSelector.IsTextChatModel(m.Info?.Task))
                .Select(m => m!)
                .ToList();
            if (usable.Count == 0)
                usable = models.Where(m => m is not null).Select(m => m!).ToList();

            // Resolve each family to the BEST variant this machine can actually run. Foundry reports a
            // misleading family-level DeviceType (it reflects the registered execution provider —
            // DirectML registers on almost any Windows box, including Windows Sandbox's basic display
            // adapter — not the real hardware), so GPU/NPU builds look "compatible" yet crash during
            // inference on hardware with no usable accelerator. We descend into each family's variants
            // and pick by the device encoded in the variant ID, dropping families with no runnable
            // variant. CPU is always available, so this rarely drops any. The picker then shows the
            // device/build Yagu will actually load (not Foundry's GPU default).
            var availableDevices = AvailableDevices();
            var resolved = new List<(IModel Family, IModel Variant)>(usable.Count);
            foreach (var m in usable)
            {
                IModel family = await catalog.GetModelAsync(AliasOf(m), cancellationToken).ConfigureAwait(false) ?? m;
                IModel? variant = FoundryModelSelector.BestRunnableVariant(family, _deviceOrder, availableDevices);
                if (variant is not null) resolved.Add((family, variant));
            }
            if (resolved.Count == 0)
            {
                log.Warning(LogSource, "No model variant matches the detected devices; listing families as a fallback.");
                resolved = usable.Select(m => (m, m)).ToList();
            }

            string? recommendedAlias = FoundryModelSelector.SelectAlias(
                resolved.Select(p => new FoundryModelSelector.ModelCandidate(
                    Alias: AliasOf(p.Family),
                    FileSizeMb: p.Variant.Info?.FileSizeMb,
                    Task: p.Variant.Info?.Task,
                    Device: FoundryModelSelector.ResolveVariantDevice(p.Variant))).ToList(),
                AvailableMemoryBudgetMb());
            int recommendedRank = recommendedAlias is null
                ? int.MaxValue
                : FoundryModelSelector.RankOf(recommendedAlias);

            var options = new List<SemanticModelOption>(resolved.Count);
            foreach ((IModel family, IModel variant) in resolved)
            {
                string alias = AliasOf(family);
                bool isRecommended = recommendedAlias is not null &&
                    string.Equals(alias, recommendedAlias, StringComparison.OrdinalIgnoreCase);
                int rank = FoundryModelSelector.RankOf(alias);
                bool isCached = await variant.IsCachedAsync(cancellationToken).ConfigureAwait(false);
                int? sizeMb = variant.Info?.FileSizeMb;

                options.Add(new SemanticModelOption
                {
                    Alias = alias,
                    DisplayName = alias,
                    Id = variant.Id,
                    SizeBytes = sizeMb is > 0 ? sizeMb.Value * 1024L * 1024L : null,
                    IsRecommended = isRecommended,
                    IsBelowRecommended = !isRecommended && rank > recommendedRank,
                    IsCached = isCached,
                    DeviceLabel = DeviceLabelOf(FoundryModelSelector.ResolveVariantDevice(variant)),
                });
            }

            // Recommended first, then better-ranked models, then the rest; cached/smaller as tiebreakers.
            var ordered = options
                .OrderByDescending(o => o.IsRecommended)
                .ThenBy(o => FoundryModelSelector.RankOf(o.Alias))
                .ThenByDescending(o => o.IsCached)
                .ThenBy(o => o.SizeBytes ?? long.MaxValue)
                .ThenBy(o => o.Alias, StringComparer.OrdinalIgnoreCase)
                .ToList();
            log.Info(LogSource,
                $"{ordered.Count} model option(s) available (recommended='{recommendedAlias ?? "<none>"}', cached={ordered.Count(o => o.IsCached)}).");
            if (log.IsVerboseEnabled)
                log.Verbose(LogSource,
                    "Model options: " + string.Join(", ", ordered.Select(o =>
                        $"{o.Alias}[{o.DeviceLabel ?? "?"}{(o.IsRecommended ? ",recommended" : "")}{(o.IsCached ? ",cached" : "")}]")));
            return ordered;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task PrepareModelAsync(
        string? modelAlias, IProgress<SemanticTranslationProgress>? progress, CancellationToken cancellationToken)
    {
        if (!_enabled)
            throw new InvalidOperationException("Semantic search is disabled in settings.");

        var log = LogService.Instance;
        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var catalog = await EnsureCatalogLockedAsync(progress, cancellationToken).ConfigureAwait(false);
            string? alias = string.IsNullOrWhiteSpace(modelAlias) ? _preferredAlias : modelAlias.Trim();
            log.Info(LogSource, $"Preparing model (requested='{alias ?? "<auto>"}').");
            var model = await FoundryModelSelector.SelectAsync(catalog, alias, _deviceOrder, AvailableDevices(), AvailableMemoryBudgetMb(), cancellationToken).ConfigureAwait(false);
            if (model is null)
            {
                log.Warning(LogSource, $"No compatible local model is available for this machine (requested='{alias ?? "<auto>"}').");
                throw new InvalidOperationException("No compatible local model is available for this machine.");
            }

            bool cached = await model.IsCachedAsync(cancellationToken).ConfigureAwait(false);
            log.Info(LogSource, $"Preparing model '{model.Alias}' (alreadyCached={cached}).");
            progress?.Report(new SemanticTranslationProgress
            {
                Stage = SemanticTranslationStage.DownloadingModel,
                Detail = model.Alias,
                Percent = cached ? 100 : 0,
            });
            var downloadStopwatch = Stopwatch.StartNew();
            await model.DownloadAsync(
                pct => progress?.Report(new SemanticTranslationProgress
                {
                    Stage = SemanticTranslationStage.DownloadingModel,
                    Detail = model.Alias,
                    Percent = pct,
                }),
                cancellationToken).ConfigureAwait(false);
            downloadStopwatch.Stop();
            log.Info(LogSource,
                $"Model '{model.Alias}' ready in {downloadStopwatch.ElapsedMilliseconds} ms ({(cached ? "cache hit" : "downloaded")}).");

            // If a different model was previously loaded, drop it so the next translate uses the new pick.
            if (_model is not null &&
                !string.Equals(_model.Alias, model.Alias, StringComparison.OrdinalIgnoreCase))
            {
                log.Info(LogSource, $"Switching loaded model from '{_model.Alias}' to '{model.Alias}'.");
                try { await _model.UnloadAsync().ConfigureAwait(false); }
                catch (Exception ex) { log.Verbose(LogSource, $"Unloading previous model '{_model.Alias}' failed.", ex); }
                _model = null;
                _chatClient = null;
                _initialized = false;
            }

            _preferredAlias = model.Alias;
            SelectedModelAlias = model.Alias;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private static string AliasOf(IModel m) =>
        !string.IsNullOrWhiteSpace(m.Alias) ? m.Alias : m.Info?.Alias ?? m.Id;

    private static string? DeviceLabelOf(DeviceType? device) => device switch
    {
        DeviceType.NPU => "NPU",
        DeviceType.GPU => "GPU",
        DeviceType.CPU => "CPU",
        _ => null,
    };

    private static void ConfigureForDeterministicJson(OpenAIChatClient chat)
    {
        var s = chat.Settings;
        s.Temperature = 0f;
        s.TopP = 1f;
        // Keep input + output well under phi-3.5-mini's 4096-token LongRoPE boundary. This model's
        // ONNX export produces garbage ("token salad") once the TOTAL sequence length passes 4096
        // (the long-factor RoPE branch is broken in the export), so an over-long generation would
        // first emit the valid JSON object and then degenerate into junk. The trimmed system prompt
        // is ~2.8K tokens and the JSON object we need is <150 tokens, so a 512-token cap leaves a
        // wide safety margin (~2.8K + 512 ≈ 3.3K < 4096) while still fitting the largest realistic
        // plan, and it makes translation noticeably faster by stopping the model from rambling.
        s.MaxTokens = 512;
        s.RandomSeed = 0;

        // Repetition penalties are load-bearing for phi-3.5-mini in the Foundry Local runtime: with
        // both at 0, greedy decoding for this model collapses to an immediate stop and CompleteChat
        // returns EMPTY content (verified — every query failed with "empty response"). A small penalty
        // nudges it off that degenerate path and it reliably emits the JSON object. The downside is that
        // a non-zero penalty also discourages *legitimately* repeated tokens, and glob lists reuse '*',
        // '.', and shared substrings (e.g. "json" in both "*.jsonl" and "*.json"), which can bias the
        // model into dropping/merging similar extensions. We counter that in the system prompt with an
        // explicit "MULTIPLE NAMED EXTENSIONS — preserve every one" rule rather than by zeroing the
        // penalty, so we keep non-empty output AND every requested extension.
        s.FrequencyPenalty = 0.6f;
        s.PresencePenalty = 0.3f;
    }

    private string BuildSystemPrompt(SemanticTranslationContext context)
    {
        DateTimeOffset now = context.Now;
        return _systemPromptTemplate.Value
            .Replace("{{TODAY}}", now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Extracts the first usable JSON object from <paramref name="raw"/> (tolerating code fences,
    /// surrounding prose, repeats, or truncation) and deserializes it into a
    /// <see cref="SemanticSearchPlan"/>. Delegates to <see cref="SemanticPlanJsonExtractor"/>, which
    /// holds the dependency-free, unit-tested extraction/repair logic.
    /// </summary>
    internal static bool TryParsePlan(string raw, out SemanticSearchPlan? plan, out string? error) =>
        SemanticPlanJsonExtractor.TryParsePlan(raw, out plan, out error);

    /// <summary>Compact, allocation-light summary of a parsed plan for Verbose diagnostics. Only the
    /// fields the model actually populated are emitted, so the log shows exactly what the model
    /// produced before normalization.</summary>
    private static string DescribePlan(SemanticSearchPlan p)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(p.Directory)) parts.Add($"dir={p.Directory}");
        if (!string.IsNullOrWhiteSpace(p.Pattern)) parts.Add($"pattern='{p.Pattern}'");
        if (!string.IsNullOrWhiteSpace(p.SearchMode)) parts.Add($"mode={p.SearchMode}");
        if (p.CaseSensitive is { } cs) parts.Add($"caseSensitive={cs}");
        if (p.UseRegex is { } rx) parts.Add($"useRegex={rx}");
        if (p.ExactMatch is { } em) parts.Add($"exactMatch={em}");
        if (p.IncludeGlobs is { Count: > 0 } inc) parts.Add($"include=[{string.Join(",", inc)}]");
        if (p.ExcludeGlobs is { Count: > 0 } exc) parts.Add($"exclude=[{string.Join(",", exc)}]");
        if (p.ExcludeFileNames is { Count: > 0 } exn) parts.Add($"excludeNames=[{string.Join(",", exn)}]");
        if (p.MinFileSizeBytes is { } mn) parts.Add($"minSize={mn}");
        if (p.MaxFileSizeBytes is { } mx) parts.Add($"maxSize={mx}");
        if (!string.IsNullOrWhiteSpace(p.CreatedAfter)) parts.Add($"createdAfter={p.CreatedAfter}");
        if (!string.IsNullOrWhiteSpace(p.CreatedBefore)) parts.Add($"createdBefore={p.CreatedBefore}");
        if (!string.IsNullOrWhiteSpace(p.ModifiedAfter)) parts.Add($"modifiedAfter={p.ModifiedAfter}");
        if (!string.IsNullOrWhiteSpace(p.ModifiedBefore)) parts.Add($"modifiedBefore={p.ModifiedBefore}");
        if (p.MaxSearchDepth is { } md) parts.Add($"maxDepth={md}");
        if (p.ObeyGitignore is { } gi) parts.Add($"obeyGitignore={gi}");
        if (p.SearchInsideArchives is { } ar) parts.Add($"archives={ar}");
        if (!string.IsNullOrWhiteSpace(p.SortBy)) parts.Add($"sortBy={p.SortBy}");
        if (!string.IsNullOrWhiteSpace(p.SortDirection)) parts.Add($"sortDir={p.SortDirection}");
        if (!string.IsNullOrWhiteSpace(p.GroupBy)) parts.Add($"groupBy={p.GroupBy}");
        if (!string.IsNullOrWhiteSpace(p.GroupDirection)) parts.Add($"groupDir={p.GroupDirection}");
        if (!string.IsNullOrWhiteSpace(p.Explanation)) parts.Add($"explanation='{p.Explanation}'");
        return parts.Count == 0 ? "(empty plan)" : string.Join(", ", parts);
    }

    private static string LoadSystemPromptTemplate()
    {
        var asm = typeof(FoundryLocalSemanticQueryTranslator).Assembly;
        using Stream? stream = asm.GetManifestResourceStream(PromptResourceName);
        if (stream is null)
            throw new InvalidOperationException($"Embedded prompt resource '{PromptResourceName}' was not found.");
        using var reader = new StreamReader(stream);
        // The prompt is a VS Code ".prompt.md" with editor-only YAML front matter; strip it so the
        // model receives only the live prompt body.
        return SemanticPromptText.StripFrontMatter(reader.ReadToEnd());
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_model is not null)
            {
                LogService.Instance.Verbose(LogSource, $"Disposing; unloading model '{_model.Alias}'.");
                await _model.UnloadAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            // Best-effort cleanup.
            LogService.Instance.Verbose(LogSource, "Model unload during dispose failed (ignored).", ex);
        }
        _initLock.Dispose();
    }
}
