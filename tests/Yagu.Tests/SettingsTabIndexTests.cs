using System.Text.RegularExpressions;
using Xunit;

namespace Yagu.Tests;

/// <summary>
/// Settings tabs are sorted alphabetically at build time, but the main window still deep-links to a few
/// of them by index. Adding or renaming a tab silently shifts those indices, and the failure is quiet —
/// the link opens the wrong tab rather than throwing. These derive the expected indices from the
/// <c>AddTab(...)</c> headers so a stale constant fails a test instead of shipping.
/// </summary>
public sealed class SettingsTabIndexTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    private static readonly IReadOnlyList<string> AlphabeticalTabHeaders = ReadTabHeaders();

    private static readonly string AdminSettingsSource =
        File.ReadAllText(Path.Combine(RepoRoot, "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.AdminSettings.cs"));

    [Theory]
    [InlineData("SettingsPerformanceTabIndex", "Performance")]
    [InlineData("SettingsDisplayTabIndex", "Display")]
    public void DeepLinkConstant_MatchesTheTabsAlphabeticalPosition(string constantName, string tabHeader)
    {
        int expected = AlphabeticalTabHeaders
            .ToList()
            .FindIndex(h => string.Equals(h, tabHeader, StringComparison.OrdinalIgnoreCase));
        Assert.True(expected >= 0, $"No settings tab named '{tabHeader}' was found.");

        Match match = Regex.Match(AdminSettingsSource, $@"private const int {constantName} = (\d+);");
        Assert.True(match.Success, $"{constantName} was not found in MainWindow.AdminSettings.cs.");

        int actual = int.Parse(match.Groups[1].Value);
        Assert.True(
            expected == actual,
            $"{constantName} is {actual} but '{tabHeader}' sorts to index {expected}. "
                + $"Tab order: {string.Join(", ", AlphabeticalTabHeaders.Select((h, i) => $"{h}({i})"))}");
    }

    [Fact]
    public void EverySettingsTabHeaderIsUnique()
    {
        var duplicates = AlphabeticalTabHeaders
            .GroupBy(h => h, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.Empty(duplicates);
    }

    private static IReadOnlyList<string> ReadTabHeaders()
    {
        string settingsDirectory = Path.Combine(RepoRoot, "src", "Yagu", "UI", "Windows", "Settings");
        var headers = new List<string>();

        foreach (string file in Directory.EnumerateFiles(settingsDirectory, "*.cs"))
        {
            foreach (Match match in Regex.Matches(File.ReadAllText(file), @"AddTab\(""([^""]+)""\)"))
                headers.Add(match.Groups[1].Value);
        }

        Assert.NotEmpty(headers);
        headers.Sort(StringComparer.OrdinalIgnoreCase);
        return headers;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Yagu.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Cannot find repo root (Yagu.slnx)");
    }
}
