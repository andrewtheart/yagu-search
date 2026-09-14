using Yagu.Services.Mcp;

namespace Yagu.Tests;

/// <summary>
/// Where Yagu decides a client's configuration lives.
/// </summary>
/// <remarks>
/// VS Code is the awkward one: two editions, any number of profiles, and each one a separate
/// <c>mcp.json</c>. Yagu writes exactly ONE of them, so picking the wrong file means "installed" points
/// at an editor the user does not actually run. These drive the roaming-data-root overload rather than
/// <see cref="McpConfigSnippetBuilder.VsCodeConfigCandidates"/>, whose answer depends on whatever VS Code
/// installations happen to exist on the machine running the suite.
/// </remarks>
public sealed class McpConfigSnippetBuilderTests : IDisposable
{
    private readonly string _appData = Path.Combine(
        Path.GetTempPath(), "yagu-appdata-" + Guid.NewGuid().ToString("N"));

    public McpConfigSnippetBuilderTests() => Directory.CreateDirectory(_appData);

    public void Dispose()
    {
        try { Directory.Delete(_appData, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    /// <summary>Creates an <c>mcp.json</c> for an edition's default profile, with the given write time.</summary>
    private string WriteUserConfig(string edition, DateTime writtenUtc)
    {
        string path = Path.Combine(_appData, edition, "User", "mcp.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{}");
        File.SetLastWriteTimeUtc(path, writtenUtc);
        return path;
    }

    /// <summary>Creates an <c>mcp.json</c> for a named profile of an edition.</summary>
    private string WriteProfileConfig(string edition, string profile, DateTime writtenUtc)
    {
        string path = Path.Combine(_appData, edition, "User", "profiles", profile, "mcp.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{}");
        File.SetLastWriteTimeUtc(path, writtenUtc);
        return path;
    }

    [Fact]
    public void NoVsCodeAtAll_YieldsNoCandidates()
    {
        Assert.Empty(McpConfigSnippetBuilder.VsCodeConfigCandidatesIn(_appData));
    }

    [Fact]
    public void DefaultCandidatesWrapper_UsesTheCurrentRoamingDataRoot()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        Assert.Equal(
            McpConfigSnippetBuilder.VsCodeConfigCandidatesIn(appData),
            McpConfigSnippetBuilder.VsCodeConfigCandidates(),
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnEmptyProfilesFolder_IsNotMistakenForAConfiguration()
    {
        Directory.CreateDirectory(Path.Combine(_appData, "Code", "User", "profiles", "work"));

        // The directory exists but holds no mcp.json, so there is nothing to write into.
        Assert.Empty(McpConfigSnippetBuilder.VsCodeConfigCandidatesIn(_appData));
    }

    [Fact]
    public void BothEditionsAndEveryProfileAreFound()
    {
        var now = DateTime.UtcNow;
        string stable = WriteUserConfig("Code", now.AddMinutes(-30));
        string insiders = WriteUserConfig("Code - Insiders", now.AddMinutes(-20));
        string work = WriteProfileConfig("Code", "work", now.AddMinutes(-10));

        string[] candidates = [.. McpConfigSnippetBuilder.VsCodeConfigCandidatesIn(_appData)];

        Assert.Equal(3, candidates.Length);
        Assert.Contains(stable, candidates, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(insiders, candidates, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(work, candidates, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void CandidatesAreOrderedMostRecentlyWrittenFirst()
    {
        var now = DateTime.UtcNow;
        WriteUserConfig("Code", now.AddHours(-5));
        string newest = WriteProfileConfig("Code - Insiders", "experiment", now.AddMinutes(-1));
        WriteUserConfig("Code - Insiders", now.AddHours(-2));

        // "Most recently written" is the whole heuristic for guessing which one the user actually uses.
        Assert.Equal(newest, McpConfigSnippetBuilder.VsCodeConfigCandidatesIn(_appData)[0], StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveVsCodePath_PicksTheMostRecentlyWrittenExistingConfig()
    {
        var now = DateTime.UtcNow;
        WriteUserConfig("Code", now.AddHours(-3));
        string newest = WriteUserConfig("Code - Insiders", now.AddMinutes(-2));

        Assert.Equal(newest, McpConfigSnippetBuilder.ResolveVsCodePath(_appData), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveVsCodePath_WithNothingConfigured_FallsBackToTheInstalledEdition()
    {
        // Only Insiders is present, so proposing a path under stable would create a folder for an editor
        // that is not on this machine.
        Directory.CreateDirectory(Path.Combine(_appData, "Code - Insiders", "User"));

        string resolved = McpConfigSnippetBuilder.ResolveVsCodePath(_appData);

        Assert.Equal(Path.Combine(_appData, "Code - Insiders", "User", "mcp.json"), resolved, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveVsCodePath_PrefersStableWhenBothEditionsAreInstalledButNeitherIsConfigured()
    {
        Directory.CreateDirectory(Path.Combine(_appData, "Code", "User"));
        Directory.CreateDirectory(Path.Combine(_appData, "Code - Insiders", "User"));

        Assert.Equal(
            Path.Combine(_appData, "Code", "User", "mcp.json"),
            McpConfigSnippetBuilder.ResolveVsCodePath(_appData),
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveVsCodePath_WithNoVsCodeAtAll_StillProposesTheStableLocation()
    {
        // Nothing to install into yet, but the snippet the user copies has to name a real path.
        Assert.Equal(
            Path.Combine(_appData, "Code", "User", "mcp.json"),
            McpConfigSnippetBuilder.ResolveVsCodePath(_appData),
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void AProfilesEntryThatIsAFileRatherThanAFolder_DoesNotAbortTheScan()
    {
        var now = DateTime.UtcNow;
        string user = WriteUserConfig("Code", now.AddMinutes(-5));

        string profiles = Path.Combine(_appData, "Code", "User", "profiles");
        Directory.CreateDirectory(profiles);
        File.WriteAllText(Path.Combine(profiles, "stray.txt"), "not a profile");

        // Scanning happens while the Settings tab is being built, so one odd entry must not lose the
        // configurations found before it.
        Assert.Contains(user, McpConfigSnippetBuilder.VsCodeConfigCandidatesIn(_appData), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProfileEnumerationFailure_PreservesConfigurationsAlreadyFound()
    {
        string user = WriteUserConfig("Code", DateTime.UtcNow);
        Directory.CreateDirectory(Path.Combine(_appData, "Code", "User", "profiles"));

        IReadOnlyList<string> candidates = McpConfigSnippetBuilder.VsCodeConfigCandidatesIn(
            _appData,
            _ => throw new IOException("profile directory unavailable"));

        Assert.Contains(user, candidates, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnUnknownClientValue_DegradesInsteadOfThrowing()
    {
        // These switches run while the Settings tab is being built and while the CLI parses arguments.
        // A new McpClientKind added without touching every switch must not take the window down with it.
        var unknown = (McpClientKind)9999;

        Assert.Equal("mcpServers", McpConfigSnippetBuilder.ServersPropertyName(unknown));
        Assert.Equal(unknown.ToString(), McpConfigSnippetBuilder.DisplayName(unknown));
        Assert.Equal(unknown.ToString().ToLowerInvariant(), McpConfigSnippetBuilder.CommandLineName(unknown));
        Assert.Equal(string.Empty, McpConfigSnippetBuilder.ConfigLocation(unknown));
        Assert.Equal(string.Empty, McpConfigSnippetBuilder.ResolveConfigPath(unknown));
        Assert.False(McpConfigSnippetBuilder.SupportsHttpEntry(unknown));
    }

    [Fact]
    public void TheYaguExecutableIsAlwaysAnAbsolutePathEndingInYaguExe()
    {
        // The server stores this on a client's command line, so a relative path would resolve against
        // whatever working directory the client happened to launch it from.
        string resolved = McpConfigSnippetBuilder.ResolveYaguExecutablePath();

        Assert.True(Path.IsPathFullyQualified(resolved), $"resolved to '{resolved}'.");
    }

    [Fact]
    public void TheServerExecutableIsResolvedInsideTheStagedServerFolder()
    {
        string resolved = McpConfigSnippetBuilder.ResolveServerExecutablePath();

        Assert.True(Path.IsPathFullyQualified(resolved), $"resolved to '{resolved}'.");
        Assert.Equal(McpConfigSnippetBuilder.ServerExecutableName, Path.GetFileName(resolved));
        Assert.Equal(McpConfigSnippetBuilder.ServerDirectoryName, Path.GetFileName(Path.GetDirectoryName(resolved)));
    }
}
