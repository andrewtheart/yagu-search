namespace Yagu.Services;

/// <summary>Pure helpers for decoding volume-unit masks from <c>WM_DEVICECHANGE</c>.</summary>
public static class DeviceVolumeChange
{
    public static IReadOnlyList<string> ExpandVolumeUnitMask(uint unitMask)
    {
        var roots = new List<string>();
        for (int bit = 0; bit < 26; bit++)
        {
            if ((unitMask & (1u << bit)) != 0)
                roots.Add($"{(char)('A' + bit)}:\\");
        }
        return roots;
    }

    public static bool IntersectsAnyRoot(string? path, IEnumerable<string> volumeRoots)
    {
        if (string.IsNullOrWhiteSpace(path) || volumeRoots is null)
            return false;
        string normalized;
        try { normalized = Path.GetFullPath(path); }
        catch { return false; }
        return volumeRoots.Any(root =>
            normalized.StartsWith(root, StringComparison.OrdinalIgnoreCase));
    }
}
