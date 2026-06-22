namespace Yagu.Tests;

public sealed class CliRunnerRegressionTests
{
    [Fact]
    public void CliSearch_ForcesConsoleLoggingToCritical()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "Yagu", "CliRunner.cs"));

        Assert.Contains("LogService.Init((LogLevel)settings.LogLevelIndex, LogLevel.Critical);", source);
        Assert.DoesNotContain("LogService.Init((LogLevel)settings.LogLevelIndex, (LogLevel)settings.ConsoleLogLevelIndex);", source);
    }

    [Fact]
    public void CliParser_RecognizesDashHelpAlias()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "Yagu", "CliRunner.cs"));

        Assert.Contains("Eq(tok, \"--help\", \"-help\"", source);
    }

    [Fact]
    public void ProgramHelpShortcut_ExitsProcessAfterPrintingHelp()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "Yagu", "Program.cs"));

        Assert.Matches("CliRunner\\.RunHelp\\(\\);\\s*Environment\\.Exit\\(0\\);", source);
    }

    [Fact]
    public void YaguExecutable_UsesConsoleSubsystemForCliHelp()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "Yagu", "Yagu.csproj"));

        Assert.Contains("<OutputType>Exe</OutputType>", source);
        Assert.DoesNotContain("<OutputType>WinExe</OutputType>", source);
    }

    [Fact]
    public void ProgramGuiMode_RelaunchesDetachedBeforeStartingWinUi()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "Yagu", "Program.cs"));

        Assert.Contains("TryRelaunchDetachedGui(args)", source);
        Assert.Contains("CreateNoWindow = true", source);
        Assert.Contains("FreeConsole();", source);
    }

    [Fact]
    public void CliHelp_IncludesTwoHundredExamples()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "Yagu", "CliRunner.cs"));

        int exampleCount = System.Text.RegularExpressions.Regex.Matches(source, @"(?m)^\s+\d{3}\. ").Count;
        int explanationCount = System.Text.RegularExpressions.Regex.Matches(source, @"(?m)^\s+Does: ").Count;
        int commandCount = System.Text.RegularExpressions.Regex.Matches(source, @"(?m)^\s+Cmd:\s+Yagu\.exe --cli ").Count;

        Assert.Equal(210, exampleCount);
        Assert.Equal(210, explanationCount);
        Assert.Equal(210, commandCount);
        Assert.Contains("EXAMPLES (210):", source);
        Assert.Contains("001. Basic search in the current folder", source);
        Assert.Contains("Does: Finds TODO anywhere under the current directory.", source);
        Assert.Contains("Cmd:  Yagu.exe --cli --directory . \"TODO\"", source);
        Assert.Contains("101. Search for API key patterns", source);
        Assert.Contains("Cmd:  Yagu.exe --cli --directory . -e \"api[_-]?key\"", source);
        Assert.Contains("201. Search source and write a compact audit", source);
        Assert.Contains("Cmd:  Yagu.exe --cli --directory src \"TODO\" -g \"*.cs\" --export .\\reports\\todo-audit.json --export-no-markers", source);
        Assert.Contains("204. Semantic search with a specific local model", source);
        Assert.Contains("205. Semantic search in a script (auto-download the recommended model)", source);
        Assert.Contains("206. Exclude hidden files and folders", source);
        Assert.Contains("207. Force-include hidden files", source);
        Assert.Contains("208. Group results by directory", source);
        Assert.Contains("210. Group by modified date, oldest groups first", source);
    }

    [Fact]
    public void CliSettings_LoadsCurrentDirectoryThenProcessLaunchDirectoryThenGlobal()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "Yagu", "CliRunner.cs"));

        AssertContainsInOrder(source,
            "Path.Combine(Directory.GetCurrentDirectory(), LocalSettingsFileName)",
            "var launchSettings = ResolveProcessLaunchSettingsPath();",
            "return new SettingsService().Load();");
        Assert.Contains("Environment.ProcessPath", source);
        Assert.Contains("AppContext.BaseDirectory", source);
        Assert.Contains("If not, Yagu checks the running process launch", source);
        Assert.Contains("directory next, then falls back to global AppData settings", source);
    }

    [Fact]
    public void CliParser_RecognizesAcceptModelDownloadFlag()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "Yagu", "CliRunner.cs"));

        Assert.Contains("public bool             AcceptModelDownload { get; private set; }", source);
        Assert.Contains("Eq(tok, \"--accept-model-download\", \"--yes-download\")", source);
    }

    [Fact]
    public void CliParser_RecognizesGroupFlags()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "Yagu", "CliRunner.cs"));

        // The --group value flag plus the asc/desc orientation flags are parsed.
        Assert.Contains("TryGetVal(raw, ref i, out v, \"--group\")", source);
        Assert.Contains("Eq(tok, \"--group-desc\", \"--group-descending\")", source);
        Assert.Contains("Eq(tok, \"--group-asc\", \"--group-ascending\")", source);
        // Backing args properties exist.
        Assert.Contains("public string?          GroupBy { get; private set; }", source);
        Assert.Contains("public bool             GroupDescending { get; private set; }", source);
        // The canonical-key normalizer exists and maps the documented keys.
        Assert.Contains("internal static string? NormalizeGroupKey(string raw)", source);
        // Help documents the flag and its keys.
        Assert.Contains("--group <key>", source);
        Assert.Contains("directory, extension, size, modified,", source);
    }

    [Fact]
    public void CliParser_SortFlagAcceptsDirectoryKey()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "Yagu", "CliRunner.cs"));

        // --sort now accepts the directory/dir keys (previously rejected as unknown).
        Assert.Contains("or \"size\" or \"name\" or \"filename\" or \"directory\" or \"dir\" or \"path\")", source);
    }

    [Fact]
    public void CliSearch_GroupingWaitsForCompletionThenRendersGrouped()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "Yagu", "CliRunner.cs"));

        // Grouping forces result collection (no live streaming) and renders via WriteGroupedResults.
        Assert.Contains("bool grouping = !string.IsNullOrWhiteSpace(args.GroupBy);", source);
        Assert.Contains("exporting || replacing || sorting || grouping || savingSession", source);
        Assert.Contains("WriteGroupedResults(collectedResults, args, useColor);", source);
        Assert.Contains("private static void WriteGroupedResults(", source);
    }

    [Fact]
    public void CliSemanticOverlay_FoldsSortAndGroupWhenUnset()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "Yagu", "CliRunner.cs"));

        // The semantic overlay only fills sort/group when the user did not set them explicitly.
        Assert.Contains("if (overlay.SortBy is { } sortKey && SortBy is null)", source);
        Assert.Contains("if (overlay.GroupBy is { } groupKey && GroupBy is null)", source);
    }

    [Fact]
    public void SemanticSystemPrompt_DocumentsSortAndGroupFields()
    {
        string prompt = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "Yagu", "Services", "Ai", "Prompts", "SemanticSearchSystemPrompt.prompt.md"));

        Assert.Contains("\"sortBy\"", prompt);
        Assert.Contains("\"sortDirection\"", prompt);
        Assert.Contains("\"groupBy\"", prompt);
        Assert.Contains("\"groupDirection\"", prompt);
        Assert.Contains("SORTING & GROUPING", prompt);
    }

    [Fact]
    public void CliParser_RecognizesHiddenFileFlags()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "Yagu", "CliRunner.cs"));

        // Parser recognizes both enable and disable forms (with aliases).
        Assert.Contains("Eq(tok, \"--hidden\", \"--search-hidden\")", source);
        Assert.Contains("Eq(tok, \"--no-hidden\", \"--no-search-hidden\")", source);
        // Nullable arg property exists so settings default applies when the flag is omitted.
        Assert.Contains("public bool?            SearchHiddenFiles { get; private set; }", source);
        // Built into SearchOptions with the settings value as the fallback.
        Assert.Contains("SearchHiddenFiles     = args.SearchHiddenFiles ?? s.SearchHiddenFiles", source);
        // Help mentions the flags.
        Assert.Contains("--no-hidden", source);
    }

    [Fact]
    public void SemanticFirstRun_OffersModelPickWithTraditionalFallback()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "Yagu", "CliRunner.cs"));

        // First-run prompt is skipped for an explicit model or a previously downloaded one.
        AssertContainsInOrder(source,
            "if (!explicitModel && !settings.SemanticModelDownloaded)",
            "EnsureSemanticModelReadyAsync(translator, args, settings, progress",
            "case SemanticModelSetup.Declined:",
            "return FallBackToTraditional(args);");

        // Declining drops to a literal Traditional search of the typed text.
        Assert.Contains("args.FallBackSemanticToTraditional();", source);
        Assert.Contains("Using Traditional search for:", source);
    }

    [Fact]
    public void FallBackSemanticToTraditional_ReusesSemanticTextAsLiteralPattern()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "Yagu", "CliRunner.cs"));

        AssertContainsInOrder(source,
            "internal void FallBackSemanticToTraditional()",
            "if (string.IsNullOrWhiteSpace(Pattern) && !string.IsNullOrWhiteSpace(SemanticPattern))",
            "Pattern = SemanticPattern;",
            "if (string.IsNullOrWhiteSpace(Directory))",
            "Directory = Environment.CurrentDirectory;");
    }

    [Fact]
    public void SemanticModelSetup_NonInteractiveConsoleFallsBackWithoutAcceptFlag()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "Yagu", "CliRunner.cs"));

        AssertContainsInOrder(source,
            "bool interactive = !Console.IsInputRedirected;",
            "if (!interactive && !args.AcceptModelDownload)",
            "return SemanticModelSetup.Declined;");
    }

    [Fact]
    public void SemanticModelSetup_PersistsChoiceToGlobalSettings()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "Yagu", "CliRunner.cs"));

        // Recommended pick is stored as an empty alias (auto) and the downloaded flag is set,
        // mirroring the GUI so later runs (CLI or GUI) skip the prompt.
        AssertContainsInOrder(source,
            "PersistSemanticModelChoice(chosenIsRecommended ? string.Empty : chosenAlias);",
            "settings.SemanticModelDownloaded = true;");
        AssertContainsInOrder(source,
            "private static void PersistSemanticModelChoice(string aliasToPersist)",
            "global.SemanticModelAlias = aliasToPersist ?? string.Empty;",
            "global.SemanticModelDownloaded = true;",
            "service.Save(global);");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Yagu.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }

    private static void AssertContainsInOrder(string text, params string[] expected)
    {
        int position = 0;
        foreach (var item in expected)
        {
            int found = text.IndexOf(item, position, StringComparison.Ordinal);
            Assert.True(found >= 0, $"Expected to find '{item}' after position {position}.");
            position = found + item.Length;
        }
    }
}