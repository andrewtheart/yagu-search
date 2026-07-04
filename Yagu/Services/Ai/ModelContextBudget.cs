namespace Yagu.Services.Ai;

/// <summary>
/// Pure, dependency-free context-window budget math for on-device model selection. Isolated from
/// <see cref="FoundryModelSelector"/> and <see cref="FoundryLocalSemanticQueryTranslator"/> (which pull
/// in Foundry Local / WindowsAppSDK types and so cannot be unit-tested) so the fit logic can be
/// exercised directly.
///
/// A local chat model can only translate a query if its context window (the maximum number of tokens it
/// can process at once) is large enough to hold the whole request: the semantic-search SYSTEM PROMPT,
/// the user's INPUT query, and the model's generated OUTPUT (the JSON plan). Some Foundry variants ship
/// with a tiny context window — e.g. the OpenVINO-NPU builds of several families expose only 4224 tokens,
/// and Phi-3-mini-4k's NPU build only 1536 — which cannot even hold the system prompt. Loading such a
/// model succeeds, but the very first inference throws a hard "exceeds the model's maximum context
/// length" error. This budget lets Yagu exclude those variants up front instead of failing at run time.
/// </summary>
internal static class ModelContextBudget
{
    /// <summary>
    /// Tokens consumed by the semantic-search system prompt plus the chat-template overhead and a short
    /// user query. Measured empirically: on a model with a 448-token window, Foundry reported the full
    /// request as "8517 input + 512 output" tokens, so ~8517 is the input side dominated by the system
    /// prompt. Kept as a named constant so it can be re-tuned if the prompt grows.
    /// </summary>
    public const int SystemPromptTokens = 8517;

    /// <summary>Output tokens reserved for the model's generated JSON plan (matches the translator's
    /// non-reasoning <c>MaxTokens</c> of 512).</summary>
    public const int OutputReserveTokens = 512;

    /// <summary>Extra input headroom so a longer-than-typical natural-language query still fits.</summary>
    public const int InputHeadroomTokens = 1024;

    /// <summary>
    /// Minimum context window (tokens) a model must support to run semantic translation reliably:
    /// the system prompt, the user's query (with headroom), and the generated plan. Variants below this
    /// cannot hold even the system prompt (4224 or 1536) and are excluded; every real full-context build
    /// in the catalog (16384+) clears it comfortably.
    /// </summary>
    public const int RequiredContextTokens = SystemPromptTokens + OutputReserveTokens + InputHeadroomTokens;

    /// <summary>
    /// Context window (tokens) that Yagu clamps an over-large non-reasoning model down to before loading
    /// it, so the model's KV cache and TensorRT/DirectML activation buffers are sized to what Yagu's
    /// request actually needs instead of the model's full advertised window (often 16384–131072). Holds
    /// the whole request (<see cref="RequiredContextTokens"/> ≈ 10K) with ~2K margin, so translation
    /// quality is unchanged, while freeing VRAM that would otherwise be reserved for tokens Yagu never
    /// uses. Must stay ≥ <see cref="RequiredContextTokens"/> (guarded by a test); only ever used to
    /// REDUCE a larger window, never to grow a smaller one.
    /// </summary>
    public const int OptimizedContextTokens = 12288;

    /// <summary>
    /// Whether a model whose context window is <paramref name="contextLength"/> can hold the full
    /// request. A null <paramref name="contextLength"/> (unknown — e.g. the model is not downloaded yet
    /// so its genai_config.json cannot be read) is treated as fitting: the guard never excludes a model
    /// it cannot measure, so selection is not blocked on missing metadata. A non-positive value is also
    /// treated as unknown.
    /// </summary>
    public static bool Fits(int? contextLength)
        => contextLength is not int ctx || ctx <= 0 || ctx >= RequiredContextTokens;
}
