using Yagu.Services;
using Yagu.Services.Ai;

namespace Yagu.Tests;

/// <summary>
/// Tests for the per-model generation-parameter override feature (<see cref="SemanticModelGenerationOverride"/>).
/// The DTO and <see cref="SettingsService"/> ARE compiled into the test assembly, so their behavior is
/// exercised for real (HasAny + settings round-trip). The consumers of the override —
/// <c>FoundryLocalSemanticQueryTranslator</c>, the worker protocol/host, the ViewModel, and CliRunner —
/// are Foundry/WinUI-coupled and are validated with source-pin assertions instead.
/// </summary>
public sealed class SemanticModelGenerationOverrideTests
{
    private static string ReadSrc(params string[] parts)
        => File.ReadAllText(Path.Combine(FindRepoRoot(), "src", Path.Combine(parts)));

    // --- Pure DTO behavior (compiled into the test assembly) -------------------------------------

    [Fact]
    public void HasAny_IsFalse_WhenAllFieldsNull()
    {
        Assert.False(new SemanticModelGenerationOverride().HasAny);
    }

    [Theory]
    [InlineData("temperature")]
    [InlineData("topP")]
    [InlineData("maxTokens")]
    [InlineData("randomSeed")]
    [InlineData("frequencyPenalty")]
    [InlineData("presencePenalty")]
    public void HasAny_IsTrue_WhenAnySingleFieldSet(string which)
    {
        var ov = new SemanticModelGenerationOverride();
        switch (which)
        {
            case "temperature": ov.Temperature = 0.5f; break;
            case "topP": ov.TopP = 0.9f; break;
            case "maxTokens": ov.MaxTokens = 256; break;
            case "randomSeed": ov.RandomSeed = 7; break;
            case "frequencyPenalty": ov.FrequencyPenalty = 0.4f; break;
            case "presencePenalty": ov.PresencePenalty = 0.2f; break;
        }
        Assert.True(ov.HasAny);
    }

    // --- Settings round-trip (real SettingsService, keyed by alias AND variant id) ---------------

    [Fact]
    public void SemanticModelParameterOverrides_DefaultsToEmptyCaseInsensitiveMap()
    {
        var s = new AppSettings();
        Assert.NotNull(s.SemanticModelParameterOverrides);
        Assert.Empty(s.SemanticModelParameterOverrides);
        // Keyed case-insensitively so an alias/id typed in any case resolves.
        s.SemanticModelParameterOverrides["Phi-4-Mini"] = new SemanticModelGenerationOverride { Temperature = 0.3f };
        Assert.True(s.SemanticModelParameterOverrides.ContainsKey("phi-4-mini"));
    }

    [Fact]
    public void RoundTrip_PersistsPerModelOverrides()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "qg-genov-" + Guid.NewGuid() + ".json");
        try
        {
            var svc = new SettingsService(tmp);
            var s = new AppSettings();
            s.SemanticModelParameterOverrides["phi-4-mini-reasoning"] =
                new SemanticModelGenerationOverride { Temperature = 0.6f, TopP = 0.9f };
            s.SemanticModelParameterOverrides["Phi-4-mini-instruct-cuda-gpu:5"] =
                new SemanticModelGenerationOverride { MaxTokens = 256, FrequencyPenalty = 0.4f };
            svc.Save(s);

            var loaded = svc.Load();

            Assert.Equal(2, loaded.SemanticModelParameterOverrides.Count);

            var reasoning = loaded.SemanticModelParameterOverrides["phi-4-mini-reasoning"];
            Assert.Equal(0.6f, reasoning.Temperature);
            Assert.Equal(0.9f, reasoning.TopP);
            // Unset fields stay null so the built-in default is preserved for those.
            Assert.Null(reasoning.MaxTokens);
            Assert.Null(reasoning.RandomSeed);

            var instruct = loaded.SemanticModelParameterOverrides["Phi-4-mini-instruct-cuda-gpu:5"];
            Assert.Equal(256, instruct.MaxTokens);
            Assert.Equal(0.4f, instruct.FrequencyPenalty);
            Assert.Null(instruct.Temperature);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    [Fact]
    public void SettingsService_RegistersOverrideTypesOnTheAotJsonContext()
    {
        // Both the dictionary and the element type must be registered on the source-gen context or the
        // AOT (reflection-free) serializer would throw at runtime.
        string svc = ReadSrc("Yagu", "Services", "SettingsService.cs");
        Assert.Contains("[JsonSerializable(typeof(Dictionary<string, Ai.SemanticModelGenerationOverride>))]", svc);
        Assert.Contains("[JsonSerializable(typeof(Ai.SemanticModelGenerationOverride))]", svc);
        Assert.Contains(
            "public Dictionary<string, Ai.SemanticModelGenerationOverride> SemanticModelParameterOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);",
            svc);
    }

    // --- Interface + translator (source-pinned; Foundry-coupled, not compiled into tests) ---------

    [Fact]
    public void Interface_ExposesTheSetter()
    {
        string iface = ReadSrc("Yagu", "Services", "Ai", "ISemanticQueryTranslator.cs");
        Assert.Contains(
            "void SetModelGenerationOverrides(IReadOnlyDictionary<string, SemanticModelGenerationOverride>? modelOverrides);",
            iface);
    }

    [Fact]
    public void FoundryTranslator_AppliesOverridesOnTopOfDefaultsForBothModelClasses()
    {
        string t = ReadSrc("Yagu", "Services", "Ai", "FoundryLocalSemanticQueryTranslator.cs");

        // Field + setter that resets the loaded model so the change takes effect on next translation.
        Assert.Contains("private IReadOnlyDictionary<string, SemanticModelGenerationOverride>? _generationOverrides;", t);
        Assert.Contains(
            "public void SetModelGenerationOverrides(IReadOnlyDictionary<string, SemanticModelGenerationOverride>? modelOverrides)",
            t);

        // The override overlay runs AFTER the built-in defaults in BOTH branches (reasoning + instruct).
        Assert.Contains("ConfigureChatSettings(chat, isReasoning, model.Alias, model.Id);", t);
        Assert.Equal(2, CountOccurrences(t, "ApplyGenerationOverride(chat, modelAlias, modelId);"));

        // Only non-null fields overwrite the defaults (partial overrides supported).
        Assert.Contains("if (ov.Temperature is { } temperature) s.Temperature = temperature;", t);
        Assert.Contains("if (ov.MaxTokens is { } maxTokens) s.MaxTokens = maxTokens;", t);

        // Resolution precedence: variant id first (most specific), then alias, case-insensitively.
        Assert.Contains(
            "if (!string.IsNullOrWhiteSpace(modelId) && TryMatchOverride(map, modelId!, out var byId)) return byId;",
            t);
        Assert.Contains(
            "if (!string.IsNullOrWhiteSpace(modelAlias) && TryMatchOverride(map, modelAlias!, out var byAlias)) return byAlias;",
            t);
        Assert.Contains("StringComparison.OrdinalIgnoreCase", t);
    }

    // --- Worker protocol + host (source-pinned) --------------------------------------------------

    [Fact]
    public void WorkerProtocol_DefinesOpAndRegistersTheDictionaryType()
    {
        string proto = ReadSrc("Yagu", "Services", "Ai", "Worker", "SemanticWorkerProtocol.cs");
        Assert.Contains("public const string SetModelGenerationOverrides = \"setModelGenerationOverrides\";", proto);
        Assert.Contains("[JsonSerializable(typeof(Dictionary<string, SemanticModelGenerationOverride>))]", proto);
    }

    [Fact]
    public void WorkerTranslator_SendsAndReplaysTheOverrides()
    {
        string wt = ReadSrc("Yagu", "Services", "Ai", "Worker", "WorkerSemanticQueryTranslator.cs");
        Assert.Contains(
            "public void SetModelGenerationOverrides(IReadOnlyDictionary<string, SemanticModelGenerationOverride>? modelOverrides)",
            wt);
        // Config is replayed to a freshly (re)started worker so a crash-restart keeps the overrides.
        Assert.Contains("_generationOverridesJson", wt);
    }

    [Fact]
    public void WorkerHost_DispatchesTheOp()
    {
        string prog = ReadSrc("Yagu.SemanticWorker", "Program.cs");
        Assert.Contains(
            "case SemanticWorkerProtocol.Ops.SetModelGenerationOverrides: _translator.SetModelGenerationOverrides(DeserializeGenerationOverrides(req.StringValue)); return true;",
            prog);
        Assert.Contains("private static Dictionary<string, SemanticModelGenerationOverride>? DeserializeGenerationOverrides(string? json)", prog);
    }

    [Fact]
    public void WorkerProject_SourceLinksTheSharedOverrideDto()
    {
        string csproj = ReadSrc("Yagu.SemanticWorker", "Yagu.SemanticWorker.csproj");
        Assert.Contains("SemanticModelGenerationOverride.cs", csproj);
    }

    // --- Push sites: GUI (ViewModel) + CLI both feed settings into the translator -----------------

    [Fact]
    public void ViewModel_PushesOverridesFromSettingsAtInit()
    {
        string vm = MainViewModelPartials.Text;
        Assert.Contains(
            "_semanticTranslator.SetModelGenerationOverrides(_settings.SemanticModelParameterOverrides);",
            vm);
    }

    [Fact]
    public void Cli_PushesOverridesFromSettings_AtEverySemanticTranslatorCreation()
    {
        string cli = ReadSrc("Yagu", "CliRunner.cs");
        Assert.Contains("ApplyModelGenerationOverrides(", cli);
        // The helper must be called at BOTH FoundryLocalSemanticQueryTranslator creation sites
        // (the run path and the model-qualification path) so parity holds no matter how semantic runs.
        Assert.True(CountOccurrences(cli, "ApplyModelGenerationOverrides(") >= 3,
            "Expected the helper definition plus a call at each of the two translator creation sites.");
    }

    // --- Helpers ---------------------------------------------------------------------------------

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { count++; i += needle.Length; }
        return count;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Yagu.slnx")))
            dir = dir.Parent;
        if (dir == null) throw new InvalidOperationException("Could not locate repo root (Yagu.slnx).");
        return dir.FullName;
    }
}
