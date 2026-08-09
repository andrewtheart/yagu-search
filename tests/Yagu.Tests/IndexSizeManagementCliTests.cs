namespace Yagu.Tests;

/// <summary>
/// Source pins for the per-index size-management CLI. Live testing found that a non-integer value
/// crashed the process with an unhandled <c>IndexOutOfRangeException</c>, that a bad value otherwise
/// applied the default while warning about an "unknown flag", and that an override could be written
/// for a folder that is not a registered index root — where it was invisible to --index-list-roots.
/// <c>CliRunner.cs</c> is not compiled into this assembly, so these are source pins.
/// </summary>
public sealed class IndexSizeManagementCliTests
{
    private static readonly string CliRunnerSource = File.ReadAllText(
        Path.Combine(FindRepoRoot(), "src", "Yagu", "CliRunner.cs"));

    [Fact]
    public void NumericFlags_ParseInsideTheBody_SoAFailedParseCannotWalkOffTheArgumentArray()
    {
        // The crash was `TryGetVal(...) && int.TryParse(...)`: TryGetVal advances the index past the
        // flag and its value, then a failed parse short-circuits the body so `continue` never runs and
        // the loop reads past the end. Both flags must consume the value inside the body instead.
        Assert.DoesNotContain("\"--root-size-budget-mb\")\n                && int.TryParse", CliRunnerSource.Replace("\r\n", "\n"));
        Assert.DoesNotContain("\"--root-auto-compaction-cap-mb\")\n                && int.TryParse", CliRunnerSource.Replace("\r\n", "\n"));

        Assert.Contains("if (TryGetVal(raw, ref i, out string rsb, \"--root-size-budget-mb\"))", CliRunnerSource);
        Assert.Contains("if (TryGetVal(raw, ref i, out string rac, \"--root-auto-compaction-cap-mb\"))", CliRunnerSource);
    }

    [Fact]
    public void InvalidNumericValue_WarnsAboutTheValue_NotAnUnknownFlag()
    {
        Assert.Contains("must be -1 or a non-negative whole number of MB - ignored.", CliRunnerSource);
        Assert.Contains("Use -1 to inherit the global setting or 0 for no limit.", CliRunnerSource);
        Assert.Contains("Use -1 to inherit the global setting or 0 for no cap.", CliRunnerSource);
        Assert.Contains("out int rsbValue) && rsbValue >= -1", CliRunnerSource);
        Assert.Contains("out int racValue) && racValue >= -1", CliRunnerSource);
    }

    [Fact]
    public void InvalidSizeMode_IsRejectedInsteadOfSilentlyBecomingTheDefault()
    {
        Assert.Contains("IndexSizeManagementModes.All.Any(m => string.Equals(m, mode, StringComparison.OrdinalIgnoreCase))", CliRunnerSource);
        Assert.Contains("warning: unknown size mode '{mode}' - ignored. ", CliRunnerSource);
        Assert.Contains("$\"Valid modes: {string.Join(\", \", IndexSizeManagementModes.All)}.\"", CliRunnerSource);
    }

    [Fact]
    public void SettingASizeOverride_RequiresARegisteredIndexRoot()
    {
        Assert.Contains("if (!IndexedRootsPolicy.Contains(settings.IndexedRoots, key))", CliRunnerSource);
        Assert.Contains("is not a registered indexed folder, so it has no size settings to override.", CliRunnerSource);
        Assert.Contains("return (int)ContentIndexExitCode.InvalidArguments;", CliRunnerSource);

        // The rejection must happen before anything is persisted.
        int guard = CliRunnerSource.IndexOf("is not a registered indexed folder", StringComparison.Ordinal);
        int save = CliRunnerSource.IndexOf("settings.IndexedRootSizePolicies = IndexSizeManagementPolicy.Set(", StringComparison.Ordinal);
        Assert.True(guard >= 0 && save > guard, "The registered-root guard must precede the settings write.");
    }

    [Fact]
    public void ClearingAnAbsentOverride_SaysSo_InsteadOfClaimingItCleared()
    {
        Assert.Contains("bool hadOverride = settings.IndexedRootSizePolicies.Any(p =>", CliRunnerSource);
        Assert.Contains("No size override was set for {key}; it already follows the global settings.", CliRunnerSource);
    }

    [Fact]
    public void InvalidOrOmittedFields_PreserveTheExistingOverride()
    {
        Assert.Contains("IndexedRootSizePolicy? existing = IndexSizeManagementPolicy.Find(", CliRunnerSource);
        Assert.Contains("Mode = mode,", CliRunnerSource);
        Assert.Contains("SizeBudgetMB = args.RootSizeBudgetMB ?? existing?.SizeBudgetMB ?? -1,", CliRunnerSource);
        Assert.Contains("?? existing?.MaxAutoCompactionSizeMB", CliRunnerSource);
    }

    [Fact]
    public void RemovingARoot_RemovesItsHiddenPerRootPolicies()
    {
        int removeRoot = CliRunnerSource.IndexOf("if (args.IndexRemoveRootPath is not null)", StringComparison.Ordinal);
        int nextCommand = CliRunnerSource.IndexOf("if (args.IndexSetRootFilterPath is not null)", removeRoot, StringComparison.Ordinal);
        string block = CliRunnerSource[removeRoot..nextCommand];

        Assert.Contains("settings.IndexedRootFilters = settings.IndexedRootFilters", block);
        Assert.Contains("settings.IndexedRootSizePolicies = IndexSizeManagementPolicy.Remove(", block);
    }

    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Yagu.slnx"))) return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new DirectoryNotFoundException("repo root");
    }
}
