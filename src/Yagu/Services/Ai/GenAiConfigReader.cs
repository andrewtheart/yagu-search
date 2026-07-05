using System.Text.Json;
using System.Text.Json.Nodes;

namespace Yagu.Services.Ai;

/// <summary>
/// Reads the context-window size (<c>model.context_length</c>) from a downloaded Foundry Local model's
/// <c>genai_config.json</c>. The Foundry SDK does not expose a model's context length through its catalog
/// metadata (<c>ModelInfo</c> only carries <c>MaxOutputTokens</c>), and the value differs PER VARIANT —
/// e.g. Phi-4-mini is 131072 on its CPU/CUDA builds but only 4224 on its OpenVINO-NPU build — so the only
/// reliable source is the config file that ships inside each downloaded model directory. Used together
/// with <see cref="ModelContextBudget"/> to exclude variants whose window cannot hold the system prompt.
///
/// All parsing is done with <see cref="JsonDocument"/> (no reflection) so it stays Native-AOT safe.
/// </summary>
internal static class GenAiConfigReader
{
    private static readonly string[] ConfigFileNames = ["genai_config.json", "default_genai_config.json"];

    /// <summary>
    /// Maps a Foundry variant id (e.g. <c>Phi-4-mini-instruct-cuda-gpu:5</c>) to the on-disk folder name
    /// Foundry uses for it (e.g. <c>Phi-4-mini-instruct-cuda-gpu-5</c>): the version separator ':' becomes
    /// '-'. Returns the trimmed id unchanged when it has no version suffix.
    /// </summary>
    public static string VariantFolderName(string variantId)
        => string.IsNullOrWhiteSpace(variantId) ? string.Empty : variantId.Trim().Replace(':', '-');

    /// <summary>
    /// Reads <c>model.context_length</c> from the first <c>genai_config.json</c> (preferred) or
    /// <c>default_genai_config.json</c> found anywhere under <paramref name="modelDirectory"/> (the config
    /// lives in a <c>v&lt;N&gt;</c> sub-folder). Returns false when the directory is missing, no config is
    /// present, or the value is absent/unparseable — callers treat "unknown" as "assume it fits".
    /// </summary>
    public static bool TryReadContextLength(string? modelDirectory, out int contextLength)
    {
        contextLength = 0;
        if (string.IsNullOrWhiteSpace(modelDirectory) || !Directory.Exists(modelDirectory))
            return false;

        string? configPath = null;
        foreach (string name in ConfigFileNames)
        {
            configPath = FirstFileOrNull(modelDirectory, name);
            if (configPath is not null) break;
        }
        if (configPath is null) return false;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("model", out var model) &&
                model.ValueKind == JsonValueKind.Object &&
                model.TryGetProperty("context_length", out var ctx) &&
                ctx.ValueKind == JsonValueKind.Number &&
                ctx.TryGetInt32(out int value) &&
                value > 0)
            {
                contextLength = value;
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Corrupt/locked/partial config — treat as unknown.
        }
        return false;
    }

    /// <summary>
    /// Locates the model directory for <paramref name="variantId"/> under <paramref name="cacheRoot"/>
    /// (Foundry's model cache) and reads its context length. Returns false when the cache root or variant
    /// folder is missing, or the config carries no usable value.
    /// </summary>
    public static bool TryResolveContextLength(string? cacheRoot, string? variantId, out int contextLength)
    {
        contextLength = 0;
        if (string.IsNullOrWhiteSpace(cacheRoot) || string.IsNullOrWhiteSpace(variantId) || !Directory.Exists(cacheRoot))
            return false;

        string folder = VariantFolderName(variantId);
        if (folder.Length == 0) return false;

        string? variantDir;
        try
        {
            variantDir = Directory.EnumerateDirectories(cacheRoot, folder, SearchOption.AllDirectories).FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        return variantDir is not null && TryReadContextLength(variantDir, out contextLength);
    }

    private static string? FirstFileOrNull(string directory, string fileName)
    {
        try
        {
            return Directory.EnumerateFiles(directory, fileName, SearchOption.AllDirectories).FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Clamps a downloaded model's advertised context window down to <paramref name="targetContextLength"/>
    /// by rewriting <c>model.context_length</c> and <c>search.max_length</c> in every
    /// <c>genai_config.json</c>/<c>default_genai_config.json</c> found under <paramref name="modelDirectory"/>.
    /// Only ever REDUCES the window (a config already at/below the target is left untouched), so a model's
    /// KV cache and accelerator activation buffers are sized to what Yagu's request needs instead of the
    /// model's full window — freeing VRAM without changing translation quality. Idempotent: re-running it
    /// after Foundry re-writes the config re-applies the clamp. All edits go through <see cref="JsonNode"/>
    /// (DOM, no reflection) so it stays Native-AOT safe.
    /// </summary>
    /// <param name="modelDirectory">The downloaded model's on-disk directory (its config lives in a
    /// <c>v&lt;N&gt;</c> sub-folder).</param>
    /// <param name="targetContextLength">The window to clamp down to (tokens). Must be positive.</param>
    /// <param name="appliedContextLength">The smallest resulting context length across patched files, or 0
    /// when nothing was changed.</param>
    /// <returns>The number of config files actually modified.</returns>
    public static int TryClampContextWindow(string? modelDirectory, int targetContextLength, out int appliedContextLength)
    {
        appliedContextLength = 0;
        if (string.IsNullOrWhiteSpace(modelDirectory) || targetContextLength <= 0 || !Directory.Exists(modelDirectory))
            return 0;

        int patched = 0;
        foreach (string name in ConfigFileNames)
        {
            string[] paths;
            try
            {
                paths = Directory.EnumerateFiles(modelDirectory, name, SearchOption.AllDirectories).ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string path in paths)
            {
                if (TryClampConfigFile(path, targetContextLength, out int applied))
                {
                    patched++;
                    appliedContextLength = appliedContextLength == 0 ? applied : Math.Min(appliedContextLength, applied);
                }
            }
        }
        return patched;
    }

    /// <summary>
    /// Clamps <c>model.context_length</c> and <c>search.max_length</c> in a single config file down to
    /// <paramref name="targetContextLength"/>, writing the file back only when at least one value changed.
    /// Returns false (and leaves the file untouched) when the file is missing/locked/unparseable or both
    /// values are already at/below the target.
    /// </summary>
    private static bool TryClampConfigFile(string configPath, int targetContextLength, out int appliedContextLength)
    {
        appliedContextLength = targetContextLength;
        try
        {
            JsonNode? root = JsonNode.Parse(File.ReadAllText(configPath));
            if (root is not JsonObject obj) return false;

            bool changed = false;
            if (obj["model"] is JsonObject model && TryClampNumber(model, "context_length", targetContextLength))
                changed = true;
            if (obj["search"] is JsonObject search && TryClampNumber(search, "max_length", targetContextLength))
                changed = true;
            if (!changed) return false;

            File.WriteAllText(configPath, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            // Corrupt/locked config, or a value we cannot parse — leave the model as-is.
            return false;
        }
    }

    /// <summary>Sets <paramref name="obj"/>[<paramref name="property"/>] to <paramref name="target"/> when
    /// it is currently a positive number greater than the target. Returns whether it changed.</summary>
    private static bool TryClampNumber(JsonObject obj, string property, int target)
    {
        if (obj[property] is JsonValue value && value.TryGetValue(out int current) && current > target)
        {
            obj[property] = target;
            return true;
        }
        return false;
    }
}
