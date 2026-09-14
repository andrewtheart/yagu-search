namespace Yagu.Tests;

/// <summary>
/// Source-pins the CLI machine-mode wiring. <c>CliRunner</c> is not compiled into this assembly (it
/// pulls in WinUI/Foundry-coupled types), so the invariants that make <c>--output json</c> a usable
/// protocol are asserted against the source text.
/// </summary>
public sealed class CliMachineModeRegressionTests
{
    [Fact]
    public void MachineMode_IsResolvedBeforeAnyCommandDispatches()
    {
        // A writer buried in the search path would leave --calc, --read, --load-session and the index
        // commands emitting human text only, which is exactly the gap the machine protocol closes.
        string source = ReadCliRunner();

        AssertContainsInOrder(
            source,
            "if (!TryConfigureMachineMode(args, out int configError))",
            "return configError;",
            "if (!string.IsNullOrWhiteSpace(args.ReadPath))",
            "return RunRead(args);",
            "if (!string.IsNullOrWhiteSpace(args.CalcExpression))",
            "return RunCalc(args.CalcExpression!);",
            "if (args.IsIndexManagementCommand)",
            "if (!string.IsNullOrWhiteSpace(args.LoadSessionPath))");
    }

    [Fact]
    public void NonInteractive_HardSuppressesEveryConsolePrompt()
    {
        // A Console.ReadLine would deadlock a bridge that holds stdin open but never answers.
        string runner = ReadCliRunner();
        string machineMode = ReadMachineMode();

        Assert.Contains("private static bool NonInteractive => s_nonInteractive || MachineMode;", machineMode);
        Assert.Contains("if (vtEnabled && !NonInteractive)\n            OfferEverythingSetupAsync()", Normalize(runner));
        Assert.Contains("if (!NonInteractive)\n            CliFirstRunPrompts.RunAsync(", Normalize(runner));
        Assert.Contains("if (!NonInteractive)\n            PromptEverythingIndexCoverageAsync(", Normalize(runner));
        Assert.Contains("ConfirmOcrDownloadOnConsole(requirement, args, vtEnabled && !NonInteractive)", runner);
        Assert.Contains("bool interactive = !Console.IsInputRedirected && !NonInteractive;", runner);
    }

    [Fact]
    public void MachineSearch_DisablesTheNativeDirectOutputSink()
    {
        // DirectOutputSink writes grep bytes straight from Rust's buffers and cannot carry the envelope.
        string source = ReadCliRunner();

        Assert.Contains(
            "Stream? directStream = (needsCollection || streamingMultiline || machine) ? null : new BufferedStream(",
            source);
        Assert.Contains("bool machine = s_machine is not null;", source);
    }

    [Fact]
    public void MachineSearch_StopsScanningOnceThePageIsFull()
    {
        // One bounded page per invocation: continuing to scan after the budget is spent burns IO on
        // results that can never be returned.
        string source = ReadCliRunner();

        AssertContainsInOrder(
            source,
            "else if (machine)",
            "if (ev is SearchEvent.Match m && !WriteConfinedMatch(m.Result))",
            "await cts.CancelAsync();");
    }

    [Fact]
    public void Read_RendersInvalidBytesInsteadOfFailingTheWholeRead()
    {
        // DetectEncoding's BOM-less fallback is UTF8Encoding(throwOnInvalidBytes: true). Every other
        // reader in the app downgrades it; --read did not, so one stray byte anywhere in the file
        // returned an internal-failure exit code and zero lines instead of the requested window.
        string machineMode = ReadMachineMode();

        AssertContainsInOrder(
            machineMode,
            "private static int RunRead(CliArgs args)",
            "var encoding = EncodingDetector.DetectEncoding(stream);",
            "if (encoding is UTF8Encoding)",
            "encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);",
            "using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true);");
    }

    [Fact]
    public void Read_IsBoundedByConfinement_BeforeItRevealsWhetherTheFileExists()
    {
        // --confine-to bounds every path the run emits, and a read is the most direct emission there is.
        // The check must precede File.Exists, or a refused path still answers "does this file exist?"
        // for anything outside the allowed folders.
        string machineMode = ReadMachineMode();

        AssertContainsInOrder(
            machineMode,
            "private static int RunRead(CliArgs args)",
            "if (s_confinement.IsActive && !s_confinement.Allows(fullPath))",
            "\"path-not-allowed\"",
            "if (!File.Exists(fullPath))",
            "\"file-not-found\"");
    }

    [Fact]
    public void ReplaceSummary_SaysWhetherAnythingWasActuallyWritten()
    {
        // filesChanged/totalReplacements are tallied before the dry-run guard, so a preview reports the
        // same numbers a real rewrite does. Without the marker a client cannot tell a proposal from a
        // completed write.
        string source = ReadCliRunner();

        AssertContainsInOrder(
            source,
            "w.WriteStartObject(\"replace\");",
            "w.WriteBoolean(\"dryRun\", replace.DryRun);",
            "w.WriteNumber(\"filesAttempted\", replace.FilesAttempted);",
            "w.WriteNumber(\"filesChanged\", replace.FilesChanged);");

        // Every construction site carries the flag, including the early failure returns.
        Assert.Contains("bool DryRun = false);", source);
        Assert.Contains("return new ReplaceOutcome(0, 0, 0, 0, Failed: 1, DryRun: dryRun);", source);
        Assert.Contains("return new ReplaceOutcome(0, 0, 0, archiveSkipped, Failed: 0, DryRun: dryRun);", source);
        Assert.Contains(
            "return new ReplaceOutcome(filePaths.Count, filesModified, totalReplacements, archiveSkipped, errors, DryRun: dryRun);",
            source);
    }

    [Fact]
    public void MachineMatches_AreFilteredByConfinement()
    {
        // --confine-to documents that it bounds what is EMITTED, not just what is exported or rewritten.
        // Every machine-mode emit site therefore goes through the filter rather than the raw writer.
        string machineMode = ReadMachineMode();

        Assert.Contains("private static bool WriteConfinedMatch(SearchResult result)", machineMode);
        Assert.Contains("if (s_confinement.IsActive && !s_confinement.Allows(result.FilePath))", machineMode);

        // A filtered-out match must not look like the end of the page, or one excluded file would
        // silently truncate everything after it.
        Assert.Contains("return !s_machine!.NoteBudgetExhausted();", machineMode);

        // ...and when the page IS full, the writer has to learn about it here. A confinement-filtered
        // match never reaches WriteMatch, so nothing else would set Truncated and the self-cancel that
        // follows would be reported as a plain user `cancelled` with truncated:false.
        Assert.DoesNotContain("return !s_machine!.BudgetExhausted;", machineMode);

        string source = ReadCliRunner();
        Assert.DoesNotContain("s_machine!.WriteMatch(", source);
    }

    [Fact]
    public void MachineSearch_TreatsASelfCancelledFullPageAsSuccess_NotCancellation()
    {
        string source = ReadCliRunner();

        AssertContainsInOrder(
            source,
            "if (machine && s_machine!.Truncated)",
            "return CompleteSearchMachineRun(CliExitCode.Success, summary: null);",
            "WriteError(\"search cancelled.\");",
            "return CompleteSearchMachineRun(CliExitCode.Cancelled, summary: null);");
    }

    [Fact]
    public void LoadSession_HonorsTheOutputModeAndTheResultLimit()
    {
        // The historical signature took only (path, vtEnabled) and streamed every stored result.
        string source = ReadCliRunner();

        Assert.Contains("private static int RunLoadSession(string path, bool vtEnabled, CliArgs args)", source);
        Assert.DoesNotContain("private static int RunLoadSession(string path, bool vtEnabled)\n", Normalize(source));
        Assert.Contains("int limit = args.MaxResults is > 0 ? args.MaxResults.Value : int.MaxValue;", source);
        Assert.Contains("else if (!WriteConfinedMatch(r))", source);
    }

    [Fact]
    public void SemanticPlan_IsEmittedAndTheSearchContinuesInTheSameInvocation()
    {
        // Running the CLI twice to get the plan separately would double the model load and resample.
        string source = ReadCliRunner();

        AssertContainsInOrder(
            source,
            "args.ApplySemanticOverlay(SemanticPlanApplier.ToOverlay(resolved));",
            "WriteMachineSemanticPlan(args, resolved, translator.SelectedModelAlias);");

        // The human --explain dry-run behavior is preserved, not reused.
        Assert.Contains("return (0, true); // dry-run: stop before searching", source);
    }

    [Fact]
    public void SettingsSource_PinsTheSettingsFileAheadOfTheWorkingDirectorySearch()
    {
        string source = ReadCliRunner();

        AssertContainsInOrder(
            source,
            "switch (s_settingsSource.Kind)",
            "case CliSettingsSourceKind.Global:",
            "return new SettingsService(SettingsService.ResolveInstanceSettingsPath());",
            "case CliSettingsSourceKind.File when !string.IsNullOrWhiteSpace(s_settingsSource.Path):",
            "// 1. Prefer a .yagu.json beside the current working directory.");
    }

    [Fact]
    public void MachineMode_ReportsTheEffectiveSettingsSource()
    {
        string source = ReadMachineMode();

        AssertContainsInOrder(
            source,
            "EmitSettingsSourceDiagnostic(args);",
            "w.WriteString(\"settingsSourceKind\"",
            "w.WriteString(\"settingsPath\"",
            "w.WriteString(\"workingDirectory\"");
    }

    [Fact]
    public void OnlineOnlyCloudFiles_AreReachableFromTheCli()
    {
        // Settings-only options break GUI/CLI parity and cannot be driven by a machine caller at all.
        string source = ReadCliRunner();

        Assert.Contains("\"--online-only\", \"--search-online-only\"", source);
        Assert.Contains("\"--no-online-only\", \"--no-search-online-only\"", source);
        Assert.Contains("SearchOnlineOnlyFiles = args.SearchOnlineOnlyFiles ?? s.SearchOnlineOnlyFiles,", source);
    }

    [Fact]
    public void MatchLineTrimming_HonorsThePersistedUserSetting()
    {
        // The GUI applies LineTruncationLength through MainViewModel; without the equivalent line here
        // the CLI silently used the built-in 500 no matter what the user configured.
        string source = ReadCliRunner();

        Assert.Contains("Helpers.LineTruncator.TruncatedLength = settings.LineTruncationLength;", source);
    }

    private static string ReadCliRunner() =>
        File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "CliRunner.cs"));

    private static string ReadMachineMode() =>
        File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "CliRunner.MachineMode.cs"));

    private static string Normalize(string source) => source.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Yagu.slnx")))
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
