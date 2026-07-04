using System;
using System.IO;
using Yagu.Services.Ai;
using Xunit;

namespace Yagu.Tests;

/// <summary>
/// Unit tests for <see cref="ModelContextBudget"/> (the required-context math) and
/// <see cref="GenAiConfigReader"/> (reading <c>context_length</c> from a downloaded model's
/// <c>genai_config.json</c>). Together they let Yagu exclude local model variants whose context window
/// cannot hold the semantic-search system prompt plus the user input and generated plan — the small
/// OpenVINO-NPU builds (4224 tokens) and Phi-3-mini-4k's NPU build (1536) fail this, every real
/// full-context build (16384+) clears it.
/// </summary>
public sealed class ModelContextBudgetTests
{
    [Fact]
    public void RequiredContextTokens_IsPromptPlusOutputPlusHeadroom()
    {
        Assert.Equal(
            ModelContextBudget.SystemPromptTokens + ModelContextBudget.OutputReserveTokens + ModelContextBudget.InputHeadroomTokens,
            ModelContextBudget.RequiredContextTokens);
        // Sits strictly between the too-small NPU builds and the smallest real full-context build so the
        // exclusion cleanly splits the catalog.
        Assert.InRange(ModelContextBudget.RequiredContextTokens, 4225, 16384);
    }

    [Theory]
    [InlineData(1536, false)]   // Phi-3-mini-4k OpenVINO-NPU
    [InlineData(4224, false)]   // OpenVINO-NPU instruct builds
    [InlineData(448, false)]    // whisper
    [InlineData(16384, true)]   // Phi-4 GPU
    [InlineData(32768, true)]   // qwen2.5 / mistral CPU
    [InlineData(131072, true)]  // phi-4-mini CPU/CUDA
    [InlineData(262144, true)]  // qwen3.5
    public void Fits_ExcludesOnlyWindowsTooSmallForThePrompt(int contextLength, bool expected)
        => Assert.Equal(expected, ModelContextBudget.Fits(contextLength));

    [Fact]
    public void Fits_JustBelowAndAtThreshold()
    {
        Assert.False(ModelContextBudget.Fits(ModelContextBudget.RequiredContextTokens - 1));
        Assert.True(ModelContextBudget.Fits(ModelContextBudget.RequiredContextTokens));
    }

    [Theory]
    [InlineData(null)]  // not downloaded -> unknown -> assume it fits
    [InlineData(0)]     // unparseable / missing -> unknown -> assume it fits
    [InlineData(-5)]    // nonsense -> unknown -> assume it fits
    public void Fits_UnknownContextIsNotExcluded(int? contextLength)
        => Assert.True(ModelContextBudget.Fits(contextLength));

    [Fact]
    public void OptimizedContextTokens_HoldsTheWholeRequestAndReducesTheDefaultWindow()
    {
        // The clamp target must always be able to hold the full request (system prompt + query + plan),
        // so clamping to it never degrades translation quality...
        Assert.True(ModelContextBudget.OptimizedContextTokens >= ModelContextBudget.RequiredContextTokens);
        // ...and it must still be smaller than the common 16384-token full-context build, or clamping
        // would never actually free any VRAM.
        Assert.True(ModelContextBudget.OptimizedContextTokens < 16384);
    }
}

/// <summary>Unit tests for <see cref="GenAiConfigReader"/>.</summary>
public sealed class GenAiConfigReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "yagu-genai-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string MakeVariant(string variantFolder, string configFileName, string json, string version = "v2")
    {
        string dir = Path.Combine(_root, "Microsoft", variantFolder, version);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, configFileName), json);
        return Path.Combine(_root, "Microsoft", variantFolder);
    }

    [Theory]
    [InlineData("Phi-4-mini-instruct-cuda-gpu:5", "Phi-4-mini-instruct-cuda-gpu-5")]
    [InlineData("qwen2.5-0.5b-instruct-generic-cpu:4", "qwen2.5-0.5b-instruct-generic-cpu-4")]
    [InlineData("no-version-suffix", "no-version-suffix")]
    [InlineData("", "")]
    [InlineData("  spaced:1  ", "spaced-1")]
    public void VariantFolderName_ReplacesColonWithDash(string variantId, string expected)
        => Assert.Equal(expected, GenAiConfigReader.VariantFolderName(variantId));

    [Fact]
    public void TryReadContextLength_ReadsModelContextLength()
    {
        string dir = MakeVariant("phi-4-mini-instruct-cuda-gpu-5", "genai_config.json",
            "{ \"model\": { \"context_length\": 131072 }, \"search\": { \"max_length\": 131072 } }");

        Assert.True(GenAiConfigReader.TryReadContextLength(dir, out int ctx));
        Assert.Equal(131072, ctx);
    }

    [Fact]
    public void TryReadContextLength_FallsBackToDefaultGenaiConfig()
    {
        string dir = MakeVariant("phi-4-mini-instruct-openvino-npu-4", "default_genai_config.json",
            "{ \"model\": { \"context_length\": 4224 } }");

        Assert.True(GenAiConfigReader.TryReadContextLength(dir, out int ctx));
        Assert.Equal(4224, ctx);
    }

    [Fact]
    public void TryReadContextLength_PrefersGenaiConfigOverDefault()
    {
        string dir = MakeVariant("dual", "default_genai_config.json", "{ \"model\": { \"context_length\": 4224 } }");
        File.WriteAllText(Path.Combine(dir, "v2", "genai_config.json"), "{ \"model\": { \"context_length\": 32768 } }");

        Assert.True(GenAiConfigReader.TryReadContextLength(dir, out int ctx));
        Assert.Equal(32768, ctx);
    }

    [Theory]
    [InlineData("{ \"model\": { } }")]                              // no context_length
    [InlineData("{ \"model\": { \"context_length\": 0 } }")]        // non-positive
    [InlineData("{ \"model\": { \"context_length\": \"x\" } }")]    // wrong type
    [InlineData("{ not json")]                                       // malformed
    [InlineData("{ \"other\": 1 }")]                                 // no model object
    public void TryReadContextLength_UnusableConfigReturnsFalse(string json)
    {
        string dir = MakeVariant("odd", "genai_config.json", json);
        Assert.False(GenAiConfigReader.TryReadContextLength(dir, out int ctx));
        Assert.Equal(0, ctx);
    }

    [Fact]
    public void TryReadContextLength_MissingDirectoryOrConfigReturnsFalse()
    {
        Assert.False(GenAiConfigReader.TryReadContextLength(Path.Combine(_root, "nope"), out _));
        Assert.False(GenAiConfigReader.TryReadContextLength(null, out _));
        string empty = Path.Combine(_root, "empty");
        Directory.CreateDirectory(empty);
        Assert.False(GenAiConfigReader.TryReadContextLength(empty, out _));
    }

    [Fact]
    public void TryResolveContextLength_FindsVariantUnderCacheRoot()
    {
        MakeVariant("Phi-4-mini-instruct-cuda-gpu-5", "genai_config.json",
            "{ \"model\": { \"context_length\": 131072 } }");

        Assert.True(GenAiConfigReader.TryResolveContextLength(_root, "Phi-4-mini-instruct-cuda-gpu:5", out int ctx));
        Assert.Equal(131072, ctx);
    }

    [Fact]
    public void TryResolveContextLength_UnknownVariantOrRootReturnsFalse()
    {
        Assert.False(GenAiConfigReader.TryResolveContextLength(_root, "does-not-exist:9", out _));
        Assert.False(GenAiConfigReader.TryResolveContextLength(null, "x:1", out _));
        Assert.False(GenAiConfigReader.TryResolveContextLength(_root, null, out _));
        Assert.False(GenAiConfigReader.TryResolveContextLength(Path.Combine(_root, "missing"), "x:1", out _));
    }

    [Fact]
    public void TryClampContextWindow_ReducesContextLengthAndMaxLength()
    {
        string dir = MakeVariant("Phi-4-trtrtx-gpu-2", "genai_config.json",
            "{ \"model\": { \"context_length\": 16384 }, \"search\": { \"max_length\": 16384 } }");

        int patched = GenAiConfigReader.TryClampContextWindow(dir, 12288, out int applied);

        Assert.Equal(1, patched);
        Assert.Equal(12288, applied);
        // Both fields are clamped, and it is now readable at the reduced value.
        Assert.True(GenAiConfigReader.TryReadContextLength(dir, out int ctx));
        Assert.Equal(12288, ctx);
        string written = File.ReadAllText(Path.Combine(dir, "v2", "genai_config.json"));
        Assert.Contains("\"context_length\": 12288", written);
        Assert.Contains("\"max_length\": 12288", written);
    }

    [Fact]
    public void TryClampContextWindow_LeavesAlreadySmallWindowsUntouched()
    {
        string dir = MakeVariant("small", "genai_config.json",
            "{ \"model\": { \"context_length\": 4224 }, \"search\": { \"max_length\": 4224 } }");

        int patched = GenAiConfigReader.TryClampContextWindow(dir, 12288, out int applied);

        Assert.Equal(0, patched);
        Assert.Equal(0, applied);
        Assert.True(GenAiConfigReader.TryReadContextLength(dir, out int ctx));
        Assert.Equal(4224, ctx);   // never grown up to the target
    }

    [Fact]
    public void TryClampContextWindow_ClampsOnlyTheOversizedField()
    {
        // context_length already small, only search.max_length is oversized -> still a patch.
        string dir = MakeVariant("mixed", "genai_config.json",
            "{ \"model\": { \"context_length\": 8192 }, \"search\": { \"max_length\": 131072 } }");

        int patched = GenAiConfigReader.TryClampContextWindow(dir, 12288, out _);

        Assert.Equal(1, patched);
        string written = File.ReadAllText(Path.Combine(dir, "v2", "genai_config.json"));
        Assert.Contains("\"context_length\": 8192", written);   // untouched (below target)
        Assert.Contains("\"max_length\": 12288", written);      // clamped
    }

    [Fact]
    public void TryClampContextWindow_PatchesDefaultConfigToo()
    {
        string dir = MakeVariant("defaulted", "default_genai_config.json",
            "{ \"model\": { \"context_length\": 32768 } }");

        int patched = GenAiConfigReader.TryClampContextWindow(dir, 12288, out int applied);

        Assert.Equal(1, patched);
        Assert.Equal(12288, applied);
    }

    [Fact]
    public void TryClampContextWindow_IsIdempotent()
    {
        string dir = MakeVariant("idem", "genai_config.json",
            "{ \"model\": { \"context_length\": 16384 }, \"search\": { \"max_length\": 16384 } }");

        Assert.Equal(1, GenAiConfigReader.TryClampContextWindow(dir, 12288, out _));
        // Second run finds nothing over the target, so it makes no further changes.
        Assert.Equal(0, GenAiConfigReader.TryClampContextWindow(dir, 12288, out int applied2));
        Assert.Equal(0, applied2);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void TryClampContextWindow_InvalidArgsAreNoOps(int target)
    {
        string dir = MakeVariant("args", "genai_config.json", "{ \"model\": { \"context_length\": 16384 } }");
        Assert.Equal(0, GenAiConfigReader.TryClampContextWindow(dir, target, out int applied));
        Assert.Equal(0, applied);
        Assert.Equal(0, GenAiConfigReader.TryClampContextWindow(null, 12288, out _));
        Assert.Equal(0, GenAiConfigReader.TryClampContextWindow(Path.Combine(_root, "nope"), 12288, out _));
    }
}
