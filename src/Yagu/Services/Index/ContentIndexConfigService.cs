using System.Globalization;

namespace Yagu.Services.Index;

/// <summary>The outcome of a config set/reset operation.</summary>
public readonly record struct ContentIndexConfigResult(bool Success, string? Error)
{
    public static ContentIndexConfigResult Ok => new(true, null);
    public static ContentIndexConfigResult Fail(string error) => new(false, error);
}

/// <summary>
/// Get/set/reset for every persisted Indexing setting through the same normalization/validation used by
/// Settings (plan §6.3 <c>--index-config</c>). Each key maps to an <see cref="AppSettings"/> field; a
/// batch <see cref="SetMany"/> validates all pairs first and applies none on any failure, so an invalid
/// or unknown key never partially saves other changes. Numeric values are clamped by the shared
/// normalizers (matching Settings); enums and booleans are strictly validated.
/// </summary>
public static class ContentIndexConfigService
{
    private sealed record Descriptor(
        string Name,
        Func<AppSettings, string> Get,
        Func<string, string?> Validate,
        Action<AppSettings, string> Apply);

    private static string Bool(bool value) => value ? "true" : "false";

    private static bool TryParseBool(string value, out bool result)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "true": case "1": case "yes": case "on": result = true; return true;
            case "false": case "0": case "no": case "off": result = false; return true;
            default: result = false; return false;
        }
    }

    private static string? ValidateBool(string value)
        => TryParseBool(value, out _) ? null : "expected a boolean (true/false)";

    private static string? ValidateInt(string value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) ? null : "expected an integer";

    private static string? ValidateEnum(string value, params string[] allowed)
    {
        foreach (string a in allowed)
            if (string.Equals(value.Trim(), a, StringComparison.OrdinalIgnoreCase))
                return null;
        return $"expected one of: {string.Join(", ", allowed)}";
    }

    private static readonly char[] FlagSeparators = { ',', ';', '+', ' ', '\t' };

    private static string? ValidateFlags(string value, params string[] allowed)
    {
        foreach (string token in value.Split(FlagSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            bool ok = false;
            foreach (string a in allowed)
            {
                if (string.Equals(token, a, StringComparison.OrdinalIgnoreCase))
                {
                    ok = true;
                    break;
                }
            }
            if (!ok)
                return $"expected any combination of: {string.Join(", ", allowed)}";
        }
        return null;
    }

    private static int ParseInt(string value)
        => int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);

    private static bool ParseBool(string value) => TryParseBool(value, out bool b) && b;

    private static readonly IReadOnlyList<Descriptor> Descriptors = BuildDescriptors();

    private static readonly Dictionary<string, Descriptor> ByName =
        Descriptors.ToDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>All known config keys, in stable order (for <c>--help</c> / documentation).</summary>
    public static IReadOnlyList<string> Keys { get; } = Descriptors.Select(d => d.Name).ToList();

    /// <summary>Every index setting as a key→value map (for <c>--index-config</c> with no arguments).</summary>
    public static IReadOnlyDictionary<string, string> GetAll(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var d in Descriptors)
            map[d.Name] = d.Get(settings);
        return map;
    }

    /// <summary>Reads a single config value, or null when the key is unknown.</summary>
    public static string? Get(AppSettings settings, string key)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return ByName.TryGetValue(key, out var d) ? d.Get(settings) : null;
    }

    /// <summary>Validates and applies a single <c>key=value</c> change.</summary>
    public static ContentIndexConfigResult Set(AppSettings settings, string key, string value)
        => SetMany(settings, new[] { (key, value) });

    /// <summary>
    /// Validates all <paramref name="pairs"/> first; if any key is unknown or any value invalid, returns
    /// a failure and applies nothing. Otherwise applies all changes.
    /// </summary>
    public static ContentIndexConfigResult SetMany(AppSettings settings, IReadOnlyList<(string Key, string Value)> pairs)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(pairs);

        foreach (var (key, value) in pairs)
        {
            if (!ByName.TryGetValue(key, out var d))
                return ContentIndexConfigResult.Fail($"unknown config key '{key}'");
            string? error = d.Validate(value ?? string.Empty);
            if (error is not null)
                return ContentIndexConfigResult.Fail($"invalid value for '{key}': {error}");
        }

        foreach (var (key, value) in pairs)
            ByName[key].Apply(settings, value ?? string.Empty);
        return ContentIndexConfigResult.Ok;
    }

    /// <summary>Restores every Indexing setting to its default (<c>--index-config reset</c>).</summary>
    public static void Reset(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var defaults = new AppSettings();
        foreach (var d in Descriptors)
            d.Apply(settings, d.Get(defaults));
    }

    private static List<Descriptor> BuildDescriptors()
    {
        var list = new List<Descriptor>();

        void AddBool(string name, Func<AppSettings, bool> get, Action<AppSettings, bool> set)
            => list.Add(new Descriptor(name, s => Bool(get(s)), ValidateBool, (s, v) => set(s, ParseBool(v))));

        void AddInt(string name, Func<AppSettings, int> get, Action<AppSettings, int> setNormalized)
            => list.Add(new Descriptor(name, s => get(s).ToString(CultureInfo.InvariantCulture), ValidateInt, (s, v) => setNormalized(s, ParseInt(v))));

        void AddEnum(string name, Func<AppSettings, string> get, Action<AppSettings, string> setNormalized, params string[] allowed)
            => list.Add(new Descriptor(name, get, v => ValidateEnum(v, allowed), setNormalized));

        // Like AddEnum but accepts a combination of the allowed values (comma/space/plus separated).
        void AddFlags(string name, Func<AppSettings, string> get, Action<AppSettings, string> setNormalized, params string[] allowed)
            => list.Add(new Descriptor(name, get, v => ValidateFlags(v, allowed), setNormalized));

        void AddString(string name, Func<AppSettings, string> get, Action<AppSettings, string> setNormalized)
            => list.Add(new Descriptor(name, get, _ => null, setNormalized));

        AddBool("EnableContentIndex", s => s.EnableContentIndex, (s, v) => s.EnableContentIndex = v);
        AddBool("UseContentIndexByDefault", s => s.UseContentIndexByDefault, (s, v) => s.UseContentIndexByDefault = v);
        AddBool("IndexAccelerateLiterals", s => s.IndexAccelerateLiterals, (s, v) => s.IndexAccelerateLiterals = v);
        AddBool("IndexAccelerateWholeWord", s => s.IndexAccelerateWholeWord, (s, v) => s.IndexAccelerateWholeWord = v);
        AddBool("IndexAccelerateRegex", s => s.IndexAccelerateRegex, (s, v) => s.IndexAccelerateRegex = v);
        AddBool("IndexAccelerateMultiline", s => s.IndexAccelerateMultiline, (s, v) => s.IndexAccelerateMultiline = v);
        AddBool("IndexUseNativeWorker", s => s.IndexUseNativeWorker, (s, v) => s.IndexUseNativeWorker = v);
        AddBool("IndexUseWorkerQuerySessions", s => s.IndexUseWorkerQuerySessions, (s, v) => s.IndexUseWorkerQuerySessions = v);
        AddBool("IndexBuildPdfTextExtendedSource", s => s.IndexBuildPdfTextExtendedSource, (s, v) => s.IndexBuildPdfTextExtendedSource = v);
        AddBool("IndexBuildImageTextExtendedSource", s => s.IndexBuildImageTextExtendedSource, (s, v) => s.IndexBuildImageTextExtendedSource = v);
        AddBool("IndexProduceV3QueryStructures", s => s.IndexProduceV3QueryStructures, (s, v) => s.IndexProduceV3QueryStructures = v);
        AddBool("IndexUseV3QueryReader", s => s.IndexUseV3QueryReader, (s, v) => s.IndexUseV3QueryReader = v);
        AddInt("IndexQueryStartupBudgetMs", s => s.IndexQueryStartupBudgetMs, (s, v) => s.IndexQueryStartupBudgetMs = AppSettings.NormalizeIndexQueryStartupBudgetMs(v));
        AddInt("IndexMaxCandidatePercent", s => s.IndexMaxCandidatePercent, (s, v) => s.IndexMaxCandidatePercent = AppSettings.NormalizeIndexMaxCandidatePercent(v));
        AddInt("IndexMaxInProcessSizeMB", s => s.IndexMaxInProcessSizeMB, (s, v) => s.IndexMaxInProcessSizeMB = AppSettings.NormalizeIndexMaxInProcessSizeMB(v));
        AddInt("IndexMaxWorkerQuerySizeMB", s => s.IndexMaxWorkerQuerySizeMB, (s, v) => s.IndexMaxWorkerQuerySizeMB = AppSettings.NormalizeIndexMaxWorkerQuerySizeMB(v));
        AddInt("IndexQueryMemoryBudgetMB", s => s.IndexQueryMemoryBudgetMB, (s, v) => s.IndexQueryMemoryBudgetMB = AppSettings.NormalizeIndexQueryMemoryBudgetMB(v));
        AddInt("IndexQueryWorkerParallelism", s => s.IndexQueryWorkerParallelism, (s, v) => s.IndexQueryWorkerParallelism = AppSettings.NormalizeIndexQueryWorkerParallelism(v));
        AddString("IndexStorageDirectory", s => s.IndexStorageDirectory, (s, v) => s.IndexStorageDirectory = AppSettings.NormalizeIndexStorageDirectory(v));
        AddInt("IndexMaxFileSizeMB", s => s.IndexMaxFileSizeMB, (s, v) => s.IndexMaxFileSizeMB = AppSettings.NormalizeIndexMaxFileSizeMB(v));
        AddInt("IndexMaxDiskSizeMB", s => s.IndexMaxDiskSizeMB, (s, v) => s.IndexMaxDiskSizeMB = AppSettings.NormalizeIndexMaxDiskSizeMB(v));
        AddInt("IndexMinimumFreeSpaceMB", s => s.IndexMinimumFreeSpaceMB, (s, v) => s.IndexMinimumFreeSpaceMB = AppSettings.NormalizeIndexMinimumFreeSpaceMB(v));
        AddInt("IndexMaxDiskUsagePercent", s => s.IndexMaxDiskUsagePercent, (s, v) => s.IndexMaxDiskUsagePercent = AppSettings.NormalizeIndexMaxDiskUsagePercent(v));
        AddInt("IndexRetainedGenerationCount", s => s.IndexRetainedGenerationCount, (s, v) => s.IndexRetainedGenerationCount = AppSettings.NormalizeIndexRetainedGenerationCount(v));
        AddInt("IndexStaleTemporaryHours", s => s.IndexStaleTemporaryHours, (s, v) => s.IndexStaleTemporaryHours = AppSettings.NormalizeIndexStaleTemporaryHours(v));
        AddInt("IndexQuarantineRetentionDays", s => s.IndexQuarantineRetentionDays, (s, v) => s.IndexQuarantineRetentionDays = AppSettings.NormalizeIndexQuarantineRetentionDays(v));
        AddFlags("IndexBuildTrigger", s => s.IndexBuildTrigger, (s, v) => s.IndexBuildTrigger = AppSettings.NormalizeIndexBuildTrigger(v), "Manual", "WhenEnabled", "AtStartup", "WhenIdle", "Continuous", "OnSchedule");
        AddEnum("IndexScheduleMode", s => s.IndexScheduleMode, (s, v) => s.IndexScheduleMode = AppSettings.NormalizeIndexScheduleMode(v), "Interval", "Weekly");
        AddInt("IndexScheduleIntervalMinutes", s => s.IndexScheduleIntervalMinutes, (s, v) => s.IndexScheduleIntervalMinutes = AppSettings.NormalizeIndexScheduleIntervalMinutes(v));
        AddInt("IndexScheduleDaysOfWeekMask", s => s.IndexScheduleDaysOfWeekMask, (s, v) => s.IndexScheduleDaysOfWeekMask = AppSettings.NormalizeIndexScheduleDaysOfWeekMask(v));
        AddString("IndexScheduleTimeOfDay", s => s.IndexScheduleTimeOfDay, (s, v) => s.IndexScheduleTimeOfDay = AppSettings.NormalizeIndexScheduleTimeOfDay(v));
        AddEnum("IndexUpdateMode", s => s.IndexUpdateMode, (s, v) => s.IndexUpdateMode = AppSettings.NormalizeIndexUpdateMode(v), "ManualFullRebuild", "AutomaticFullRebuildWhenDirty", "AutomaticIncremental");
        AddInt("IndexIdleDelayMinutes", s => s.IndexIdleDelayMinutes, (s, v) => s.IndexIdleDelayMinutes = AppSettings.NormalizeIndexIdleDelayMinutes(v));
        AddInt("IndexBuildMemoryBudgetMB", s => s.IndexBuildMemoryBudgetMB, (s, v) => s.IndexBuildMemoryBudgetMB = AppSettings.NormalizeIndexBuildMemoryBudgetMB(v));
        AddInt("IndexBuildWorkerParallelism", s => s.IndexBuildWorkerParallelism, (s, v) => s.IndexBuildWorkerParallelism = AppSettings.NormalizeIndexBuildWorkerParallelism(v));
        AddBool("IndexPauseDuringForegroundSearch", s => s.IndexPauseDuringForegroundSearch, (s, v) => s.IndexPauseDuringForegroundSearch = v);
        AddBool("IndexPauseOnBattery", s => s.IndexPauseOnBattery, (s, v) => s.IndexPauseOnBattery = v);
        AddEnum("IndexRemovableDrivePolicy", s => s.IndexRemovableDrivePolicy, (s, v) => s.IndexRemovableDrivePolicy = AppSettings.NormalizeIndexRemovableDrivePolicy(v), "Never", "ExplicitRootsOnly");
        AddBool("IndexFollowReparsePoints", s => s.IndexFollowReparsePoints, (s, v) => s.IndexFollowReparsePoints = v);
        AddBool("IndexIncludeHiddenFiles", s => s.IndexIncludeHiddenFiles, (s, v) => s.IndexIncludeHiddenFiles = v);
        AddInt("IndexMaxJournalCatchupMB", s => s.IndexMaxJournalCatchupMB, (s, v) => s.IndexMaxJournalCatchupMB = AppSettings.NormalizeIndexMaxJournalCatchupMB(v));
        AddInt("IndexMaxJournalCatchupRecords", s => s.IndexMaxJournalCatchupRecords, (s, v) => s.IndexMaxJournalCatchupRecords = AppSettings.NormalizeIndexMaxJournalCatchupRecords(v));
        AddInt("IndexPostBuildCatchUpThresholdChanges", s => s.IndexPostBuildCatchUpThresholdChanges, (s, v) => s.IndexPostBuildCatchUpThresholdChanges = AppSettings.NormalizeIndexPostBuildCatchUpThresholdChanges(v));
        AddBool("IndexUseWatcherHints", s => s.IndexUseWatcherHints, (s, v) => s.IndexUseWatcherHints = v);
        AddInt("IndexMaxDeltaSegments", s => s.IndexMaxDeltaSegments, (s, v) => s.IndexMaxDeltaSegments = AppSettings.NormalizeIndexMaxDeltaSegments(v));
        AddInt("IndexCompactionThresholdMB", s => s.IndexCompactionThresholdMB, (s, v) => s.IndexCompactionThresholdMB = AppSettings.NormalizeIndexCompactionThresholdMB(v));
        AddInt("IndexMaxAutoCompactionSizeMB", s => s.IndexMaxAutoCompactionSizeMB, (s, v) => s.IndexMaxAutoCompactionSizeMB = AppSettings.NormalizeIndexMaxAutoCompactionSizeMB(v));
        AddBool("ShareAggregateIndexTelemetry", s => s.ShareAggregateIndexTelemetry, (s, v) => s.ShareAggregateIndexTelemetry = v);
        AddBool("IndexAutoRepair", s => s.IndexAutoRepair, (s, v) => s.IndexAutoRepair = v);
        AddBool("ShowIndexStatusInMainWindow", s => s.ShowIndexStatusInMainWindow, (s, v) => s.ShowIndexStatusInMainWindow = v);
        AddBool("ShowIndexBuildNotifications", s => s.ShowIndexBuildNotifications, (s, v) => s.ShowIndexBuildNotifications = v);
        AddBool("ShowIndexProvenanceInResults", s => s.ShowIndexProvenanceInResults, (s, v) => s.ShowIndexProvenanceInResults = v);
        AddString("IndexExcludedGlobs", s => s.IndexExcludedGlobs, (s, v) => s.IndexExcludedGlobs = v?.Trim() ?? string.Empty);
        AddString("IndexExcludedExtensions", s => s.IndexExcludedExtensions, (s, v) => s.IndexExcludedExtensions = v?.Trim() ?? string.Empty);

        return list;
    }
}
