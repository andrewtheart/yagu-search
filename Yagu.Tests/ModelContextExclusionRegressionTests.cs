using System.IO;
using Xunit;

namespace Yagu.Tests;

/// <summary>
/// Source pins for the model context-window exclusion. A local chat model can only translate a query if
/// its context window can hold the system prompt + input + output; some Foundry variants ship a tiny
/// window (4224-token OpenVINO-NPU builds, Phi-3-mini-4k's 1536-token NPU build) that loads yet fails the
/// first inference. The pure math (<see cref="Yagu.Services.Ai.ModelContextBudget"/>) and config reader
/// (<see cref="Yagu.Services.Ai.GenAiConfigReader"/>) are unit-tested directly; the integration lives in
/// the Foundry-coupled translator, which is not compiled into the test assembly, so it is pinned here.
/// Also pins the <c>--semantic-batch</c> CLI evaluation mode used to benchmark models.
/// </summary>
public sealed class ModelContextExclusionRegressionTests
{
    private static string RepoRoot()
    {
        string dir = System.AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Yagu.sln")))
            dir = Directory.GetParent(dir)?.FullName!;
        return dir ?? throw new DirectoryNotFoundException("Yagu.sln not found above the test output directory.");
    }

    private static string Translator() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "Yagu", "Services", "Ai", "FoundryLocalSemanticQueryTranslator.cs"));

    [Fact]
    public void Translator_BoundsRunawayGenerationWithTokenCapTimeoutAndJsonMode()
    {
        string src = Translator();

        // (1) Hard output-token cap (192) is the primary bound on runaway/degenerate generation, which
        // at the old 512 budget ran ~120s on a VRAM-saturated GPU and tripped the watchdog.
        Assert.Contains("private const int MaxOutputTokens = 192;", src);
        Assert.Contains("s.MaxTokens = MaxOutputTokens;", src);

        // (2) JSON-object response mode. The Foundry ChatSettings API exposes NO stop-sequence field, so
        // this is the structural equivalent: the runtime stops when the top-level object closes.
        Assert.Contains("Type = \"json_object\"", src);

        // (3) Non-reasoning watchdog lowered 120s -> 45s so a wedged generation aborts quickly; reasoning
        // models keep the longer budget for their <think> trace.
        Assert.Contains("InferenceTimeout = TimeSpan.FromSeconds(45)", src);
        Assert.Contains("ReasoningInferenceTimeout = TimeSpan.FromSeconds(300)", src);
    }

    [Fact]
    public void Prompt_RoutesPhrasesAndWordsToLiteralNotRegex()
    {
        string prompt = File.ReadAllText(Path.Combine(
            RepoRoot(), "Yagu", "Services", "Ai", "Prompts", "SemanticSearchSystemPrompt.prompt.md"));
        // A word/phrase must be a literal content search, never a generated regex (the runaway trigger).
        Assert.Contains("LITERAL WORDS AND PHRASES ARE NOT REGEX", prompt);
        Assert.Contains("Regex is ONLY for the machine-format patterns", prompt);
    }

    [Fact]
    public void Translator_ResolvesCacheLocationForContextChecks()
    {
        string src = Translator();
        // The referenced SDK exposes no context length OR cache-location API, so the cache root is derived
        // from the AppName (%USERPROFILE%\.<AppName>\cache\models) to read each variant's genai_config.json.
        Assert.Contains("private string? _cacheLocation;", src);
        Assert.Contains("_cacheLocation = ResolveFoundryCacheRoot();", src);
        Assert.Contains("private static string? ResolveFoundryCacheRoot()", src);
        Assert.Contains("\".\" + FoundryAppName, \"cache\", \"models\"", src);
    }

    [Fact]
    public void Translator_ReadsVariantContextViaGenAiConfig()
    {
        string src = Translator();
        Assert.Contains("private int? VariantContextLength(IModel? model)", src);
        Assert.Contains("GenAiConfigReader.TryResolveContextLength(_cacheLocation, id, out int ctx)", src);
    }

    [Fact]
    public void Translator_GuardsAgainstTooSmallContextAfterDownloadBeforeLoad()
    {
        string src = Translator();
        Assert.Contains("private void EnsureModelContextFits(IModel model)", src);
        Assert.Contains("ModelContextBudget.Fits(contextLength)", src);
        // Guard runs after the model is on disk (so genai_config.json is readable) but before load, in
        // BOTH the translate path and the explicit-prepare path.
        Assert.Contains("EnsureModelContextFits(model);", src);
        int guardCount = src.Split("EnsureModelContextFits(model);").Length - 1;
        Assert.True(guardCount >= 2, $"expected the context guard at both load sites, found {guardCount}.");
    }

    [Fact]
    public void Translator_ExcludesTooSmallVariantsFromPicker()
    {
        string src = Translator();
        // The picker drops downloaded variants whose window is too small to hold the prompt.
        Assert.Contains("int? ctx = VariantContextLength(variant);", src);
        Assert.Contains("if (!ModelContextBudget.Fits(ctx))", src);
    }

    [Fact]
    public void Cli_ExposesSemanticBatchEvaluationMode()
    {
        string cli = File.ReadAllText(Path.Combine(RepoRoot(), "Yagu", "CliRunner.cs"));
        Assert.Contains("--semantic-batch", cli);
        Assert.Contains("a.SemanticBatch = v.Trim('\"');", cli);
        Assert.Contains("RunSemanticBatchAsync(args, batchSettings)", cli);
        Assert.Contains("private static async Task<int> RunSemanticBatchAsync(CliArgs args, AppSettings settings)", cli);
        // The model is loaded once and reused across queries (delimited blocks per query).
        Assert.Contains("===QUERY===", cli);
        Assert.Contains("===END===", cli);
    }
}
