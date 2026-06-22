using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Microsoft.AI.Foundry.Local;
using Microsoft.Extensions.Logging.Abstractions;
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
    private const string PromptResourceName = "Yagu.Services.Ai.Prompts.SemanticSearchSystemPrompt.txt";

    private readonly bool _enabled;
    private string? _preferredAlias;
    private readonly FoundryModelSelector _selector;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly Lazy<string> _systemPromptTemplate = new(LoadSystemPromptTemplate);

    private ICatalog? _catalog;
    private OpenAIChatClient? _chatClient;
    private IModel? _model;
    private bool _initialized;

    /// <summary>The alias of the model that was actually selected/loaded, available after the first
    /// successful <see cref="TranslateAsync"/> call. Useful for diagnostics (which model ran).</summary>
    public string? SelectedModelAlias { get; private set; }

    public FoundryLocalSemanticQueryTranslator(bool enabled, string? modelOverrideAlias = null, FoundryModelSelector? selector = null)
    {
        _enabled = enabled;
        _preferredAlias = string.IsNullOrWhiteSpace(modelOverrideAlias) ? null : modelOverrideAlias.Trim();
        _selector = selector ?? new FoundryModelSelector();
    }

    public bool IsAvailable => _enabled;

    public async Task<SemanticTranslationResult> TranslateAsync(
        string naturalLanguageQuery,
        SemanticTranslationContext context,
        IProgress<SemanticTranslationProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!_enabled)
            return SemanticTranslationResult.Fail("Semantic search is disabled in settings.");
        if (string.IsNullOrWhiteSpace(naturalLanguageQuery))
            return SemanticTranslationResult.Fail("Enter a request to translate.");

        context ??= new SemanticTranslationContext();

        OpenAIChatClient chat;
        try
        {
            chat = await EnsureChatClientAsync(progress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return SemanticTranslationResult.Fail($"Could not start the local AI model: {ex.Message}");
        }

        progress?.Report(new SemanticTranslationProgress { Stage = SemanticTranslationStage.Interpreting });

        string raw;
        try
        {
            string systemPrompt = BuildSystemPrompt(context);
            var messages = new List<ChatMessage>
            {
                ChatMessage.FromSystem(systemPrompt),
                ChatMessage.FromUser(naturalLanguageQuery.Trim()),
            };

            var response = await chat.CompleteChatAsync(messages, cancellationToken).ConfigureAwait(false);
            raw = response?.Choices?.FirstOrDefault()?.Message?.Content ?? string.Empty;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return SemanticTranslationResult.Fail($"The local AI model failed to respond: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(raw))
            return SemanticTranslationResult.Fail("The local AI model returned an empty response.", raw);

        if (!TryParsePlan(raw, out var plan, out var parseError))
            return SemanticTranslationResult.Fail(parseError ?? "Could not understand the model output.", raw);

        return SemanticTranslationResult.Ok(plan!, raw);
    }

    private async Task<OpenAIChatClient> EnsureChatClientAsync(
        IProgress<SemanticTranslationProgress>? progress, CancellationToken cancellationToken)
    {
        if (_initialized && _chatClient is not null) return _chatClient;

        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized && _chatClient is not null) return _chatClient;

            var catalog = await EnsureCatalogLockedAsync(progress, cancellationToken).ConfigureAwait(false);
            var model = await _selector.SelectAsync(catalog, _preferredAlias, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("No compatible local model is available for this machine.");

            bool cached = await model.IsCachedAsync(cancellationToken).ConfigureAwait(false);
            // Only surface the download UI when the model isn't already on disk. A cached model
            // makes DownloadAsync a fast no-op, so flashing "Downloading model — 100%" on every
            // launch wrongly implies a re-download that never actually happens.
            if (!cached)
                progress?.Report(new SemanticTranslationProgress
                {
                    Stage = SemanticTranslationStage.DownloadingModel,
                    Detail = model.Alias,
                    Percent = 0,
                });
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

            progress?.Report(new SemanticTranslationProgress
            {
                Stage = SemanticTranslationStage.LoadingModel,
                Detail = model.Alias,
            });
            await model.LoadAsync(cancellationToken).ConfigureAwait(false);

            var chat = await model.GetChatClientAsync(cancellationToken).ConfigureAwait(false);
            ConfigureForDeterministicJson(chat);

            _model = model;
            _chatClient = chat;
            _initialized = true;
            _preferredAlias = model.Alias;
            SelectedModelAlias = model.Alias;
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

        progress?.Report(new SemanticTranslationProgress { Stage = SemanticTranslationStage.Initializing });

        if (!FoundryLocalManager.IsInitialized)
        {
            var config = new Configuration { AppName = "Yagu" };
            await FoundryLocalManager.CreateAsync(config, NullLogger.Instance, cancellationToken).ConfigureAwait(false);
        }

        var manager = FoundryLocalManager.Instance;

        // Execution providers are cached on disk by Foundry Local after the first run. The
        // register step still runs each process, but performs no download when already cached.
        // Surface the "Downloading AI runtime" stage only while a real download is in flight
        // (callback reports < 100%), so repeat launches don't appear to re-download every time.
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

        _catalog = await manager.GetCatalogAsync(cancellationToken).ConfigureAwait(false);
        return _catalog;
    }

    public async Task<IReadOnlyList<SemanticModelOption>> ListModelOptionsAsync(
        IProgress<SemanticTranslationProgress>? progress, CancellationToken cancellationToken)
    {
        if (!_enabled) return Array.Empty<SemanticModelOption>();

        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var catalog = await EnsureCatalogLockedAsync(progress, cancellationToken).ConfigureAwait(false);
            var models = await catalog.ListModelsAsync(cancellationToken).ConfigureAwait(false);
            if (models is null || models.Count == 0) return Array.Empty<SemanticModelOption>();

            // Restrict to text-chat models and rank them so the recommended pick can be flagged.
            var usable = models
                .Where(m => m is not null && FoundryModelSelector.IsTextChatModel(m.Info?.Task))
                .Select(m => m!)
                .ToList();
            if (usable.Count == 0)
                usable = models.Where(m => m is not null).Select(m => m!).ToList();

            string? recommendedAlias = FoundryModelSelector.SelectAlias(
                usable.Select(m => new FoundryModelSelector.ModelCandidate(
                    Alias: AliasOf(m),
                    FileSizeMb: m.Info?.FileSizeMb,
                    Task: m.Info?.Task,
                    Device: m.Info?.Runtime?.DeviceType ?? DeviceType.CPU)).ToList());
            int recommendedRank = recommendedAlias is null
                ? int.MaxValue
                : FoundryModelSelector.RankOf(recommendedAlias);

            var options = new List<SemanticModelOption>(usable.Count);
            foreach (var m in usable)
            {
                string alias = AliasOf(m);
                bool isRecommended = recommendedAlias is not null &&
                    string.Equals(alias, recommendedAlias, StringComparison.OrdinalIgnoreCase);
                int rank = FoundryModelSelector.RankOf(alias);
                bool isCached = await m.IsCachedAsync(cancellationToken).ConfigureAwait(false);
                int? sizeMb = m.Info?.FileSizeMb;

                options.Add(new SemanticModelOption
                {
                    Alias = alias,
                    DisplayName = alias,
                    SizeBytes = sizeMb is > 0 ? sizeMb.Value * 1024L * 1024L : null,
                    IsRecommended = isRecommended,
                    IsBelowRecommended = !isRecommended && rank > recommendedRank,
                    IsCached = isCached,
                    DeviceLabel = DeviceLabelOf(m.Info?.Runtime?.DeviceType),
                });
            }

            // Recommended first, then better-ranked models, then the rest; cached/smaller as tiebreakers.
            return options
                .OrderByDescending(o => o.IsRecommended)
                .ThenBy(o => FoundryModelSelector.RankOf(o.Alias))
                .ThenByDescending(o => o.IsCached)
                .ThenBy(o => o.SizeBytes ?? long.MaxValue)
                .ThenBy(o => o.Alias, StringComparer.OrdinalIgnoreCase)
                .ToList();
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

        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var catalog = await EnsureCatalogLockedAsync(progress, cancellationToken).ConfigureAwait(false);
            string? alias = string.IsNullOrWhiteSpace(modelAlias) ? _preferredAlias : modelAlias.Trim();
            var model = await _selector.SelectAsync(catalog, alias, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("No compatible local model is available for this machine.");

            bool cached = await model.IsCachedAsync(cancellationToken).ConfigureAwait(false);
            progress?.Report(new SemanticTranslationProgress
            {
                Stage = SemanticTranslationStage.DownloadingModel,
                Detail = model.Alias,
                Percent = cached ? 100 : 0,
            });
            await model.DownloadAsync(
                pct => progress?.Report(new SemanticTranslationProgress
                {
                    Stage = SemanticTranslationStage.DownloadingModel,
                    Detail = model.Alias,
                    Percent = pct,
                }),
                cancellationToken).ConfigureAwait(false);

            // If a different model was previously loaded, drop it so the next translate uses the new pick.
            if (_model is not null &&
                !string.Equals(_model.Alias, model.Alias, StringComparison.OrdinalIgnoreCase))
            {
                try { await _model.UnloadAsync().ConfigureAwait(false); } catch { }
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

    private static string LoadSystemPromptTemplate()
    {
        var asm = typeof(FoundryLocalSemanticQueryTranslator).Assembly;
        using Stream? stream = asm.GetManifestResourceStream(PromptResourceName);
        if (stream is null)
            throw new InvalidOperationException($"Embedded prompt resource '{PromptResourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_model is not null)
                await _model.UnloadAsync().ConfigureAwait(false);
        }
        catch
        {
            // Best-effort cleanup.
        }
        _initLock.Dispose();
    }
}
