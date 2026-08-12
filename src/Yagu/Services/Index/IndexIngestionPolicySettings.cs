using Yagu.Helpers;

namespace Yagu.Services.Index;

/// <summary>App-only adapters that create worker-safe ingestion policies from persisted settings.</summary>
public sealed partial class IndexIngestionPolicy
{
    public static IndexIngestionPolicy FromSettings(AppSettings settings, int maxDepth = 0)
        => FromSettings(settings, rootFilter: null, maxDepth);

    public static IndexIngestionPolicy FromSettings(AppSettings settings, IndexedRootFilter? rootFilter, int maxDepth = 0)
    {
        ArgumentNullException.ThrowIfNull(settings);
        long capBytes = (long)AppSettings.NormalizeIndexMaxFileSizeMB(settings.IndexMaxFileSizeMB) * 1024 * 1024;
        List<string> excludes = SplitSettingsList(settings.IndexExcludedGlobs);
        List<string>? reAdmit = null;
        if (rootFilter is not null)
        {
            excludes.AddRange(SplitSettingsList(rootFilter.ExcludeGlobs));
            List<string> rootIncludes = SplitSettingsList(rootFilter.IncludeGlobs);
            if (rootIncludes.Count > 0)
                reAdmit = rootIncludes;
        }

        return new IndexIngestionPolicy(
            capBytes,
            excludes,
            ParseSettingsExtensions(settings.IndexExcludedExtensions),
            settings.IndexIncludeHiddenFiles,
            settings.IndexFollowReparsePoints,
            maxDepth,
            reAdmit,
            indexBinaryAsciiContent: true);
    }

    private static List<string> SplitSettingsList(string? raw)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(raw))
            return list;
        foreach (string part in GlobMatcher.SplitPatternList(raw))
            list.Add(part);
        return list;
    }

    private static HashSet<string> ParseSettingsExtensions(string? raw)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(raw))
            return set;
        foreach (string part in raw.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string ext = part;
            if (ext.StartsWith("*.", StringComparison.Ordinal)) ext = ext[2..];
            if (ext.StartsWith('.')) ext = ext[1..];
            if (ext.Length > 0)
                set.Add(ext);
        }
        return set;
    }
}