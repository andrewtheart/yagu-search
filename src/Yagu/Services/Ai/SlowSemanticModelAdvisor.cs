namespace Yagu.Services.Ai;

/// <summary>
/// Pure decision logic for the "AI interpretation is taking a while" prompt: given the locally-runnable
/// model options and which one is currently running, picks the models that are <em>smaller</em> (and
/// therefore typically faster on the same hardware) so they can be offered as a quicker alternative.
/// Kept free of Foundry/WinUI dependencies so it is unit-testable.
/// </summary>
public static class SlowSemanticModelAdvisor
{
    /// <summary>
    /// Returns the options that are smaller than the model currently running, ordered smallest-first.
    /// </summary>
    /// <param name="options">All locally-runnable options (already filtered to this machine's hardware).</param>
    /// <param name="currentModelKey">The running model's catalog variant id or alias (matched against
    /// <see cref="SemanticModelOption.Id"/> first, then <see cref="SemanticModelOption.Alias"/>). May be null.</param>
    /// <param name="currentAlias">The configured model override alias (empty = automatic/recommended pick).</param>
    /// <remarks>
    /// Returns an empty list when the current model cannot be identified or its size is unknown — in
    /// those cases "smaller/faster" cannot be reasoned about, so no (possibly wrong) alternative is offered.
    /// </remarks>
    public static IReadOnlyList<SemanticModelOption> SelectFasterOptions(
        IReadOnlyList<SemanticModelOption> options,
        string? currentModelKey,
        string? currentAlias)
    {
        if (options is null || options.Count == 0)
            return Array.Empty<SemanticModelOption>();

        SemanticModelOption? current = FindCurrentOption(options, currentModelKey, currentAlias);
        if (current?.SizeBytes is not { } currentSize || currentSize <= 0)
            return Array.Empty<SemanticModelOption>();

        var faster = new List<SemanticModelOption>(options.Count);
        foreach (var option in options)
        {
            if (ReferenceEquals(option, current)) continue;
            if (option.SizeBytes is { } size && size > 0 && size < currentSize)
                faster.Add(option);
        }

        faster.Sort(static (a, b) => (a.SizeBytes ?? long.MaxValue).CompareTo(b.SizeBytes ?? long.MaxValue));
        return faster;
    }

    /// <summary>
    /// Identifies which option is the model currently running: matched by variant id, then alias, then
    /// the configured override alias, and finally the recommended pick when selection is automatic.
    /// </summary>
    public static SemanticModelOption? FindCurrentOption(
        IReadOnlyList<SemanticModelOption> options,
        string? currentModelKey,
        string? currentAlias)
    {
        if (options is null || options.Count == 0)
            return null;

        string? key = string.IsNullOrWhiteSpace(currentModelKey) ? null : currentModelKey.Trim();
        if (key is not null)
        {
            foreach (var option in options)
            {
                if (!string.IsNullOrEmpty(option.Id) &&
                    string.Equals(option.Id, key, StringComparison.OrdinalIgnoreCase))
                    return option;
            }

            foreach (var option in options)
            {
                if (string.Equals(option.Alias, key, StringComparison.OrdinalIgnoreCase))
                    return option;
            }
        }

        string? alias = string.IsNullOrWhiteSpace(currentAlias) ? null : currentAlias.Trim();
        if (alias is not null)
        {
            foreach (var option in options)
            {
                if (string.Equals(option.Alias, alias, StringComparison.OrdinalIgnoreCase))
                    return option;
            }
        }

        // Automatic selection (no override): the recommended pick is the model that runs.
        foreach (var option in options)
        {
            if (option.IsRecommended)
                return option;
        }

        return null;
    }
}
