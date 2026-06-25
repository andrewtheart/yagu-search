using System;
using System.Collections.Generic;
using System.Linq;

namespace Yagu.Services.Ai;

/// <summary>How a newly-seen catalog model relates to what the user has seen before.</summary>
public enum FoundryModelChangeKind
{
    /// <summary>A model whose alias was not present in the previous catalog snapshot.</summary>
    New,

    /// <summary>A new variant/version of a model alias that was already present (an update).</summary>
    Updated,
}

/// <summary>A single Foundry Local catalog model, reduced to the fields the update check needs.</summary>
public sealed record FoundryModelDescriptor(string Id, string Alias, string? DeviceLabel, long? SizeBytes);

/// <summary>A model that became available since the last check, with its classification.</summary>
public sealed record FoundryModelChange(
    string Id, string Alias, string? DeviceLabel, long? SizeBytes, FoundryModelChangeKind Kind);

/// <summary>
/// Outcome of <see cref="FoundryModelUpdateChecker.Detect"/>: the models that became available, the
/// id set to persist as the new baseline, and whether this run merely seeded the baseline (first run,
/// no alert).
/// </summary>
public sealed record FoundryModelUpdateResult(
    IReadOnlyList<FoundryModelChange> Changes,
    IReadOnlyList<string> CurrentIds,
    bool BaselineSeeded);

/// <summary>
/// Pure, UI-free logic for detecting newly-available Foundry Local models. Compares the catalog's
/// current variant ids against a persisted baseline and classifies each newcomer as a brand-new model
/// or an update/variant of a known alias. Kept free of the Foundry SDK and WinUI so it is unit-testable.
/// </summary>
public static class FoundryModelUpdateChecker
{
    /// <summary>Default minimum interval between catalog checks (throttles startup work).</summary>
    public static readonly TimeSpan DefaultCheckInterval = TimeSpan.FromHours(24);

    /// <summary>
    /// Whether enough time has elapsed since <paramref name="lastCheckUtc"/> to check again. A null
    /// last-check (never checked) always returns true.
    /// </summary>
    public static bool ShouldCheck(DateTimeOffset? lastCheckUtc, DateTimeOffset nowUtc, TimeSpan interval)
        => lastCheckUtc is not { } last || nowUtc - last >= interval;

    /// <summary>
    /// Diffs <paramref name="currentModels"/> against <paramref name="knownIds"/> (the baseline of
    /// variant ids seen at the previous check). When <paramref name="hasBaseline"/> is false this is
    /// the first check: it silently captures the baseline and reports no changes (so the user is not
    /// flagged about every pre-existing model). Otherwise it returns each previously-unseen variant id,
    /// classified <see cref="FoundryModelChangeKind.Updated"/> when its alias still has a known variant
    /// in the catalog and <see cref="FoundryModelChangeKind.New"/> when the whole alias is new.
    /// </summary>
    public static FoundryModelUpdateResult Detect(
        IReadOnlyCollection<string> knownIds,
        IReadOnlyCollection<FoundryModelDescriptor> currentModels,
        bool hasBaseline)
    {
        ArgumentNullException.ThrowIfNull(knownIds);
        ArgumentNullException.ThrowIfNull(currentModels);

        var currentIds = currentModels
            .Select(m => m.Id)
            .Where(id => !string.IsNullOrEmpty(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (!hasBaseline)
            return new FoundryModelUpdateResult(Array.Empty<FoundryModelChange>(), currentIds, BaselineSeeded: true);

        var known = new HashSet<string>(knownIds, StringComparer.Ordinal);

        // An alias is "existing" when at least one of its current variants was already known. Such an
        // alias's new variants are updates; an alias with no known variant is a brand-new model.
        var existingAliases = new HashSet<string>(
            currentModels.Where(m => known.Contains(m.Id)).Select(m => m.Alias),
            StringComparer.OrdinalIgnoreCase);

        var changes = new List<FoundryModelChange>();
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in currentModels)
        {
            if (string.IsNullOrEmpty(m.Id) || known.Contains(m.Id) || !emitted.Add(m.Id))
                continue;

            var kind = existingAliases.Contains(m.Alias)
                ? FoundryModelChangeKind.Updated
                : FoundryModelChangeKind.New;
            changes.Add(new FoundryModelChange(m.Id, m.Alias, m.DeviceLabel, m.SizeBytes, kind));
        }

        return new FoundryModelUpdateResult(changes, currentIds, BaselineSeeded: false);
    }
}
