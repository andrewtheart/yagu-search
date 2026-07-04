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

    /// <summary>Below this available-RAM budget (MB) on a CPU-only machine, <see cref="BuildSystemPrompt"/>
    /// sends the condensed prompt (schema + rules, no few-shot examples) so a tiny model's KV cache fits.
    /// At/above it — or on accelerated hardware, where the budget is null — the full prompt is sent
    /// unchanged. 2048 MB targets genuinely extreme cases (e.g. a 4 GB machine with little free RAM).</summary>
    private const int PromptCondenseMemoryThresholdMb = 2048;

    /// <summary>Maximum time to wait for a single model inference before treating the translation as
    /// failed. A healthy response takes a few seconds on GPU/NPU (and is slower but still bounded on
    /// CPU); this watchdog fires when the Foundry Local runtime wedges mid-generation, so the search
    /// can fall back to a literal one instead of hanging. Kept modest (45s, was 120s): the output is
    /// now hard-capped at <see cref="MaxOutputTokens"/> tokens, so even the slow spilling-VRAM path
    /// finishes a full generation well inside this window; a query that still exceeds it is genuinely
    /// wedged (e.g. a runaway that the token cap somehow didn't bound) and should be aborted quickly
    /// rather than making a whole query set crawl.</summary>
    private static readonly TimeSpan InferenceTimeout = TimeSpan.FromSeconds(45);

    /// <summary>Watchdog for reasoning / chain-of-thought models. They legitimately generate a long
    /// <c>&lt;think&gt;</c> trace before the JSON answer, so a healthy response takes much longer than a
    /// plain instruct model — especially on CPU. The cap is correspondingly larger so a slow-but-working
    /// reasoning model isn't aborted before it reaches its answer.</summary>
    private static readonly TimeSpan ReasoningInferenceTimeout = TimeSpan.FromSeconds(300);

    /// <summary>Hard cap on generated tokens for a (non-reasoning) translation. A valid JSON plan is
    /// &lt;150 tokens; 192 fits the largest realistic plan with margin while bounding runaway/degenerate
    /// generations to a few seconds instead of the full 512-token budget. See the rationale in
    /// <see cref="ConfigureChatSettings"/>.</summary>
    private const int MaxOutputTokens = 192;

    /// <summary>Foundry Local <c>AppName</c> for this app; also determines the on-disk model cache root
    /// (<c>%USERPROFILE%\.&lt;AppName&gt;\cache\models</c>).</summary>
    private const string FoundryAppName = "Yagu";

    private bool _enabled;
    private string? _preferredAlias;
    private IReadOnlyList<DeviceType> _deviceOrder;
    private bool _hasGpu = true;
    private bool _hasNpu = true;
    private long _gpuMemoryBytes;
    private bool _selectedModelIsReasoning;
    private bool _unloadAfterUse;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly Lazy<string> _systemPromptTemplate = new(LoadSystemPromptTemplate);

    private ICatalog? _catalog;
    private OpenAIChatClient? _chatClient;
    private IModel? _model;
    private bool _initialized;

    /// <summary>Foundry's on-disk model cache root, resolved once during catalog init. Used to read a
    /// downloaded variant's <c>genai_config.json</c> so its context window can be checked against
    /// <see cref="ModelContextBudget"/> (the SDK does not expose context length through catalog metadata,
    /// and the value differs per variant).</summary>
    private string? _cacheLocation;

    /// <summary>The alias of the model that was actually selected/loaded, available after the first
    /// successful <see cref="TranslateAsync"/> call. Useful for diagnostics (which model ran).</summary>
    public string? SelectedModelAlias { get; private set; }

    /// <summary>The catalog variant id of the model that was actually selected/loaded, when the catalog
    /// exposes one. More specific than <see cref="SelectedModelAlias"/> (it pins the accelerator build
    /// and quantization), so it is the preferred key for per-variant warning suppression.</summary>
    public string? SelectedModelId { get; private set; }

    /// <inheritdoc />
    public string? CurrentModelKey =>
        !string.IsNullOrWhiteSpace(SelectedModelId) ? SelectedModelId
        : !string.IsNullOrWhiteSpace(SelectedModelAlias) ? SelectedModelAlias
        : !string.IsNullOrWhiteSpace(_preferredAlias) ? _preferredAlias
        : null;

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

    public void SetGpuMemoryBytes(long dedicatedVideoMemoryBytes)
    {
        long normalized = dedicatedVideoMemoryBytes > 0 ? dedicatedVideoMemoryBytes : 0;
        if (_gpuMemoryBytes == normalized) return;
        _gpuMemoryBytes = normalized;
        LogService.Instance.Info(LogSource, $"GPU memory set ({normalized / (1024L * 1024L)} MB); will re-select on next translation.");
        // The larger-model auto-upgrade decision depends on this, so re-select on next use.
        ResetLoadedModel();
    }

    public void SetUnloadAfterUse(bool unloadAfterUse)
    {
        if (_unloadAfterUse == unloadAfterUse) return;
        _unloadAfterUse = unloadAfterUse;
        LogService.Instance.Info(LogSource,
            $"Unload-after-use {(unloadAfterUse ? "enabled (model released from VRAM after each translation)" : "disabled (model stays resident)")}.");
        // No reload needed: the flag only changes what happens AFTER the next translation finishes.
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

    /// <summary>
    /// Dedicated GPU memory (MB) used to decide whether AUTO selection may upgrade to a larger, more
    /// accurate model (e.g. phi-4 14B) than the small default. 0 when there is no GPU or the amount is
    /// unknown — in which case no upgrade happens and the small default stands. Only meaningful on a
    /// machine with a real GPU (an NPU-only or CPU-only box gets 0).
    /// </summary>
    private int AvailableVramBudgetMb()
    {
        if (!_hasGpu || _gpuMemoryBytes <= 0) return 0;
        long mb = _gpuMemoryBytes / (1024L * 1024L);
        return (int)Math.Min(mb, int.MaxValue);
    }

    /// <summary>
    /// Reads a downloaded variant's context window (tokens) from its <c>genai_config.json</c> under the
    /// Foundry cache root, or null when it cannot be measured (cache root unknown, model not downloaded,
    /// or config missing/unparseable). The SDK exposes no context length, and it differs per variant.
    /// </summary>
    private int? VariantContextLength(IModel? model)
    {
        string? id = model?.Id;
        if (string.IsNullOrWhiteSpace(_cacheLocation) || string.IsNullOrWhiteSpace(id)) return null;
        return GenAiConfigReader.TryResolveContextLength(_cacheLocation, id, out int ctx) ? ctx : null;
    }

    /// <summary>
    /// Best-effort resolution of the on-disk Foundry model cache root, used to read a downloaded variant's
    /// <c>genai_config.json</c> (the referenced SDK version exposes no cache-location API). Foundry Local
    /// caches an app's models under <c>%USERPROFILE%\.&lt;AppName&gt;\cache\models</c>; a
    /// <c>FOUNDRY_CACHE_DIR</c> override is honored first when set. Returns the first existing candidate,
    /// or null (which disables the context check, so models are then assumed to fit).
    /// </summary>
    private static string? ResolveFoundryCacheRoot()
    {
        foreach (string candidate in CacheRootCandidates())
        {
            try { if (Directory.Exists(candidate)) return candidate; }
            catch { /* invalid path — skip */ }
        }
        return null;
    }

    private static IEnumerable<string> CacheRootCandidates()
    {
        string? env = Environment.GetEnvironmentVariable("FOUNDRY_CACHE_DIR");
        if (!string.IsNullOrWhiteSpace(env))
        {
            yield return Path.Combine(env, "models");
            yield return env;
        }
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile))
            yield return Path.Combine(profile, "." + FoundryAppName, "cache", "models");
    }

    /// <summary>
    /// Guards against loading a model whose context window is too small to hold the system prompt plus
    /// the input and output (e.g. the 4224-token OpenVINO-NPU builds, or Phi-3-mini-4k's 1536-token NPU
    /// build). Called after the model is on disk (so its genai_config.json is readable) and before it is
    /// loaded. Throws a clear, actionable error instead of letting the first inference fail with an opaque
    /// "exceeds the model's maximum context length". No-op when the context cannot be measured.
    /// </summary>
    private void EnsureModelContextFits(IModel model)
    {
        int? ctx = VariantContextLength(model);
        if (ctx is not int contextLength || ModelContextBudget.Fits(contextLength)) return;

        string alias = model.Alias ?? model.Id ?? "<unknown>";
        LogService.Instance.Warning(LogSource,
            $"Model '{alias}' has a context window of {contextLength} tokens, below the ~{ModelContextBudget.RequiredContextTokens} " +
            "needed for the system prompt + query + plan; refusing to use it.");
        throw new InvalidOperationException(
            $"The selected model's context window ({contextLength} tokens) is too small to run AI search " +
            $"(it needs about {ModelContextBudget.RequiredContextTokens}). Choose a different model.");
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
        SelectedModelId = null;
    }

    /// <summary>
    /// Unloads the currently loaded model from memory (freeing GPU VRAM) via the Foundry SDK's
    /// <see cref="IModel.UnloadAsync"/>, then drops the cached references so the next translation reloads
    /// it. Called after a translation finishes when "unload after use" is enabled. Best-effort and never
    /// throws: a failure to unload is logged and the search continues (the model simply stays resident).
    /// </summary>
    private async Task UnloadLoadedModelAfterUseAsync()
    {
        IModel? model;
        await _initLock.WaitAsync().ConfigureAwait(false);
        try
        {
            model = _model;
            // Drop references first so a concurrent/next translation re-selects and reloads cleanly.
            _chatClient = null;
            _model = null;
            _initialized = false;
        }
        finally
        {
            _initLock.Release();
        }

        if (model is null) return;
        try
        {
            var stopwatch = Stopwatch.StartNew();
            await model.UnloadAsync().ConfigureAwait(false);
            stopwatch.Stop();
            LogService.Instance.Info(LogSource,
                $"Unloaded model '{model.Alias ?? model.Id}' from memory after use in {stopwatch.ElapsedMilliseconds} ms (freed VRAM).");
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning(LogSource,
                $"Failed to unload model '{model.Alias ?? model.Id}' after use; it stays resident until re-selected.", ex);
        }
    }

    /// <summary>
    /// Clamps a downloaded model's advertised context window down to
    /// <see cref="ModelContextBudget.OptimizedContextTokens"/> before it is loaded, so its KV cache and
    /// accelerator (TensorRT/DirectML) activation buffers are sized to what Yagu's request actually needs
    /// (~10K tokens) instead of the model's full window (often 16384–131072). This frees VRAM without
    /// changing translation quality. Re-applied on every load so a Foundry re-download that restores the
    /// original config is re-clamped. Skipped for REASONING models (their long &lt;think&gt; output needs
    /// the larger window) and never grows a window that is already small. Best-effort: any failure is
    /// logged and the model loads with its original window.
    /// </summary>
    private async Task ClampModelContextWindowAsync(IModel model, bool isReasoning, CancellationToken cancellationToken)
    {
        if (isReasoning) return;

        string? modelDir = await ResolveModelDirectoryAsync(model, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(modelDir)) return;

        try
        {
            int patched = GenAiConfigReader.TryClampContextWindow(
                modelDir, ModelContextBudget.OptimizedContextTokens, out int applied);
            if (patched > 0)
                LogService.Instance.Info(LogSource,
                    $"Clamped context window of '{model.Alias ?? model.Id}' to {applied} tokens " +
                    $"({patched} config file(s)) to reduce reserved VRAM; translation quality is unaffected.");
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning(LogSource,
                $"Could not clamp context window for '{model.Alias ?? model.Id}'; loading with its original window.", ex);
        }
    }

    /// <summary>Resolves the on-disk directory of a downloaded model, preferring the SDK's
    /// <see cref="IModel.GetPathAsync"/> (robust to a custom Foundry model dir such as a secondary drive)
    /// and falling back to the resolved Foundry cache root + variant folder. Returns null when neither
    /// yields an existing directory.</summary>
    private async Task<string?> ResolveModelDirectoryAsync(IModel model, CancellationToken cancellationToken)
    {
        try
        {
            string? path = await model.GetPathAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(path))
            {
                if (Directory.Exists(path)) return path;
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir)) return dir;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogService.Instance.Verbose(LogSource, $"GetPathAsync failed for '{model.Alias ?? model.Id}': {ex.Message}; using cache-root fallback.");
        }

        string? id = model.Id;
        if (string.IsNullOrWhiteSpace(_cacheLocation) || string.IsNullOrWhiteSpace(id)) return null;
        try
        {
            string folder = GenAiConfigReader.VariantFolderName(id);
            return folder.Length == 0
                ? null
                : Directory.EnumerateDirectories(_cacheLocation, folder, SearchOption.AllDirectories).FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void RefreshCatalog()
    {
        _catalog = null;
        SelectedModelAlias = null;
        ResetLoadedModel();
        LogService.Instance.Info(LogSource, "Foundry catalog cache cleared; will re-query Foundry Local on next use.");
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

        // Everything below runs against the loaded model. Wrap it so that, when "unload after use" is
        // enabled, the model is released from memory (freeing VRAM) once interpretation finishes —
        // whether it succeeded, failed, hit the watchdog, or was cancelled.
        try
        {
            string raw;
            var inferenceStopwatch = Stopwatch.StartNew();

            // Watchdog: a wedged model inference must not leave the search stuck on "Interpreting your
            // request…" forever. The Foundry Local runtime occasionally hangs mid-generation (the call
            // never returns), so cap it with a timeout linked to the user's cancellation token. Reasoning
            // models get a longer budget because their <think> trace legitimately takes much longer.
            TimeSpan inferenceTimeout = _selectedModelIsReasoning ? ReasoningInferenceTimeout : InferenceTimeout;
            using var inferenceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            inferenceCts.CancelAfter(inferenceTimeout);
            try
            {
                string systemPrompt = BuildSystemPrompt(context);
                var messages = new List<ChatMessage>
                {
                    ChatMessage.FromSystem(systemPrompt),
                    ChatMessage.FromUser(trimmedQuery),
                };
                log.Verbose(LogSource,
                    $"Sending chat completion (model={SelectedModelAlias ?? "<unknown>"}, systemPromptChars={systemPrompt.Length}, " +
                    $"userQueryChars={trimmedQuery.Length}, watchdogTimeout={inferenceTimeout.TotalSeconds:F0}s).");

                var response = await chat.CompleteChatAsync(messages, inferenceCts.Token).ConfigureAwait(false);
                raw = response?.Choices?.FirstOrDefault()?.Message?.Content ?? string.Empty;
                inferenceStopwatch.Stop();
                log.Info(LogSource,
                    $"Model responded in {inferenceStopwatch.ElapsedMilliseconds} ms (model={SelectedModelAlias ?? "<unknown>"}, responseChars={raw.Length}).");
            }
            catch (OperationCanceledException) when (inferenceCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                // The watchdog fired (not the user) — the model wedged and never returned. Report a failed
                // translation so the caller falls back to a literal Traditional search instead of silently
                // aborting the whole submit (which is what a real user-cancellation does).
                inferenceStopwatch.Stop();
                log.Warning(LogSource,
                    $"Inference watchdog fired: the local AI model did not respond within {inferenceTimeout.TotalSeconds:F0}s " +
                    $"(elapsed={inferenceStopwatch.ElapsedMilliseconds} ms, model={SelectedModelAlias ?? "<unknown>"}); " +
                    "treating as a failed translation so the search falls back to a literal one.");
                return SemanticTranslationResult.Fail(
                    $"The local AI model did not respond within {inferenceTimeout.TotalSeconds:F0} seconds.");
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
        finally
        {
            if (_unloadAfterUse)
                await UnloadLoadedModelAfterUseAsync().ConfigureAwait(false);
        }
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
            IModel? model = null;
            // Ample-GPU auto-upgrade: when the user hasn't pinned a model and this machine has plenty of
            // dedicated VRAM, prefer a larger, more-accurate family (e.g. phi-4 14B) over the small
            // default. Resolve it via the normal override path so it downloads + upgrades to the best
            // runnable variant exactly like a manual pick; if that family isn't in the catalog we fall
            // through to normal auto selection (never null just because the upgrade was unavailable).
            if (string.IsNullOrWhiteSpace(_preferredAlias)
                && HighAccuracyModelPolicy.UpgradeAliasFor(AvailableVramBudgetMb()) is { } upgradeAlias)
            {
                model = await FoundryModelSelector.SelectAsync(catalog, upgradeAlias, _deviceOrder, AvailableDevices(), AvailableMemoryBudgetMb(), cancellationToken).ConfigureAwait(false);
                if (model is not null)
                    log.Info(LogSource, $"Ample GPU VRAM ({AvailableVramBudgetMb()} MB): upgraded auto-selection to '{upgradeAlias}'.");
                else
                    log.Verbose(LogSource, $"VRAM upgrade '{upgradeAlias}' not available in catalog; using normal auto-selection.");
            }
            model ??= await FoundryModelSelector.SelectAsync(catalog, _preferredAlias, _deviceOrder, AvailableDevices(), AvailableMemoryBudgetMb(), cancellationToken).ConfigureAwait(false);
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

            // Now that the model is on disk, verify its context window can hold the system prompt + input
            // + output before loading it — some variants (e.g. 4224-token OpenVINO-NPU builds) load fine
            // yet fail the first inference with an opaque context-length error.
            EnsureModelContextFits(model);

            // Shrink an over-large context window (e.g. phi-4's 16384) down to what Yagu actually needs
            // BEFORE loading, so the KV cache / accelerator activation buffers reserve far less VRAM. This
            // must happen before LoadAsync (which sizes those buffers from genai_config.json) and is a
            // no-op for reasoning models and windows already at/below the target.
            bool willBeReasoning = FoundryModelSelector.IsReasoningAlias(model.Alias);
            await ClampModelContextWindowAsync(model, willBeReasoning, cancellationToken).ConfigureAwait(false);

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
            bool isReasoning = FoundryModelSelector.IsReasoningAlias(model.Alias);
            ConfigureChatSettings(chat, isReasoning);
            log.Verbose(LogSource, isReasoning
                ? "Chat client configured for a REASONING model (temperature=0.7, topP=0.95, maxTokens=8192, no repetition penalty; <think> trace is stripped before parsing)."
                : "Chat client configured for deterministic JSON (temperature=0, topP=1, maxTokens=512, frequencyPenalty=0.6, presencePenalty=0.3).");

            _model = model;
            _chatClient = chat;
            _initialized = true;
            _preferredAlias = model.Alias;
            SelectedModelAlias = model.Alias;
            SelectedModelId = model.Id;
            _selectedModelIsReasoning = isReasoning;
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
            log.Info(LogSource, $"Initializing Foundry Local manager (AppName={FoundryAppName}).");
            var config = new Configuration { AppName = FoundryAppName };
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

        // Resolve the on-disk model cache root so downloaded variants' context windows can be read from
        // their genai_config.json (the referenced SDK exposes no context length OR cache-location API).
        // Best-effort — a null root just disables the context check (models are then assumed to fit).
        _cacheLocation = ResolveFoundryCacheRoot();
        log.Verbose(LogSource, $"Foundry model cache root: {_cacheLocation ?? "<unknown>"}.");
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
            // Screen by alias AND task: the catalog Task field is often unset (e.g. whisper-tiny), so a
            // task-only filter would leave speech/embedding models in the list. If nothing qualifies we
            // return an empty list rather than re-adding non-chat models (they can never translate).
            var usable = models
                .Where(m => m is not null && FoundryModelSelector.IsTextChatModel(AliasOf(m), m.Info?.Task))
                .Select(m => m!)
                .ToList();

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
                if (variant is null) continue;

                // Exclude a downloaded variant whose context window is too small to hold the system prompt
                // + query + plan (e.g. a 4224-token OpenVINO-NPU build): it would load but fail every
                // request. Not-yet-downloaded variants can't be measured (context unknown -> assumed to
                // fit) and are caught by the post-download guard if picked.
                int? ctx = VariantContextLength(variant);
                if (!ModelContextBudget.Fits(ctx))
                {
                    log.Info(LogSource,
                        $"Excluding '{AliasOf(family)}' [{variant.Id}] from options: context window {ctx} < {ModelContextBudget.RequiredContextTokens}.");
                    continue;
                }
                resolved.Add((family, variant));
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
            // Ample-GPU upgrade: mirror the auto-load decision so the picker recommends the larger, more
            // accurate model (e.g. phi-4) when this machine has plenty of VRAM AND that family is in the
            // resolved list. Keeps the "recommended" pill consistent with what auto-select actually loads.
            if (HighAccuracyModelPolicy.UpgradeAliasFor(AvailableVramBudgetMb()) is { } upgradeAlias
                && resolved.Any(p => string.Equals(AliasOf(p.Family), upgradeAlias, StringComparison.OrdinalIgnoreCase)))
            {
                recommendedAlias = upgradeAlias;
            }
            int recommendedRank = recommendedAlias is null
                ? int.MaxValue
                : FoundryModelSelector.RankOf(recommendedAlias);

            // CPU-only memory budget (null on accelerated hardware) used to flag models too big to load.
            int? memBudgetMb = AvailableMemoryBudgetMb();

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
                    ExceedsAvailableMemory = memBudgetMb is { } budget && sizeMb is { } sz && !ModelMemoryBudget.Fits(sz, budget),
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

            // Reject a model whose context window is too small to run semantic search, so an explicit
            // pick of an unusable variant fails clearly here rather than at the next translate.
            EnsureModelContextFits(model);

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
            SelectedModelId = model.Id;
            _selectedModelIsReasoning = FoundryModelSelector.IsReasoningAlias(model.Alias);
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

    private static void ConfigureChatSettings(OpenAIChatClient chat, bool isReasoning)
    {
        var s = chat.Settings;

        if (isReasoning)
        {
            // Reasoning / chain-of-thought models (e.g. phi-4-reasoning) think out loud in a <think>
            // block before emitting the answer, which we strip in SemanticPlanJsonExtractor. Tune for
            // that workload rather than for a terse instruct model:
            //  • A generous token budget so the reasoning trace does NOT starve the JSON that follows it
            //    (a 512-token cap let the <think> block consume the whole budget and the plan never
            //    appeared). These models have a large context window, so this stays well within bounds.
            //  • A modest temperature + top-p, not greedy. Microsoft's phi reasoning guidance recommends
            //    sampling (~0.7); pure greedy decoding (temperature 0) makes these models collapse into
            //    repetition loops ("The question: The question: …") and never finish the answer.
            //  • No repetition penalties: a reasoning trace legitimately repeats tokens while it works a
            //    problem, and penalizing that degrades the reasoning. RandomSeed is still pinned so the
            //    sampled output is reproducible run-to-run for the same query.
            s.Temperature = 0.7f;
            s.TopP = 0.95f;
            s.MaxTokens = 8192;
            s.RandomSeed = 0;
            s.FrequencyPenalty = 0f;
            s.PresencePenalty = 0f;
            return;
        }

        s.Temperature = 0f;
        s.TopP = 1f;
        // Hard output-token cap. The JSON plan we need is <150 tokens, so 192 fits the largest
        // realistic plan (directory + several globs + dates + a short explanation) with margin while
        // TIGHTLY bounding the worst case. This is the primary defence against runaway generation:
        // some queries ("the phrase X", "MAC addresses", "phone numbers") push the model to BUILD a
        // regex, and under greedy decoding it can fall into a repetition loop, emitting a degenerate
        // pattern like "(?:\s+\w+\s+){1,}" over and over until it exhausts the budget. At the old 512
        // cap that runaway ran for ~120s on a VRAM-saturated GPU (~4 tok/s) and tripped the watchdog;
        // capping at 192 bounds it to a few seconds. It also keeps input+output under phi-3.5-mini's
        // 4096-token LongRoPE boundary (past which its ONNX export emits token salad). A rare legit
        // plan that would exceed 192 tokens is truncated in its (optional) explanation field, which
        // SemanticPlanJsonExtractor repairs, so the search-affecting fields still parse.
        s.MaxTokens = MaxOutputTokens;
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

        // Constrain the reply to a JSON object. The Foundry ChatSettings API exposes no stop-sequence
        // field, so this is the structural equivalent: with json_object mode the runtime stops as soon
        // as the top-level object closes, which both bounds rambling (a backstop to the MaxTokens cap)
        // and reinforces valid-JSON output that SemanticPlanJsonExtractor then parses. If a model/EP
        // ignores or rejects the format, the existing translate/parse fallback still applies.
        s.ResponseFormat = new global::Microsoft.AI.Foundry.Local.OpenAI.ResponseFormatExtended { Type = "json_object" };
    }

    private string BuildSystemPrompt(SemanticTranslationContext context)
    {
        // All the testable logic (low-memory condense gating + {{TODAY}} substitution) lives in the pure,
        // unit-tested SemanticPromptText.BuildSystemPrompt; this wrapper only gathers the live inputs (the
        // embedded template, today's date, and the CPU-only memory budget). AvailableMemoryBudgetMb() is
        // null on accelerated hardware, so the full prompt is sent unchanged there.
        return SemanticPromptText.BuildSystemPrompt(
            _systemPromptTemplate.Value,
            context.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            AvailableMemoryBudgetMb(),
            PromptCondenseMemoryThresholdMb);
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
