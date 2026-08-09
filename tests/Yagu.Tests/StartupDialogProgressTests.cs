namespace Yagu.Tests;

public sealed class StartupDialogProgressTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string StartupChecks = ReadAppFile(
        "UI", "Windows", "MainWindow", "MainWindow.StartupChecks.cs");
    private static readonly string StartupProgress = ReadAppFile(
        "UI", "Windows", "MainWindow", "MainWindow.StartupDialogProgress.cs");
    private static readonly string YaguDialog = ReadAppFile(
        "UI", "Windows", "YaguDialog.cs");
    private static readonly string TestSettingsIsolation = File.ReadAllText(
        Path.Combine(RepoRoot, "tests", "Yagu.Tests", "TestSettingsIsolation.cs"));

    [Fact]
    public void TestHarness_SuppressesStartupDialogsForInheritedChildProcesses()
    {
        Assert.Contains(
            "SuppressStartupDialogsEnvVar = \"YAGU_TEST_SUPPRESS_STARTUP_DIALOGS\"",
            StartupProgress);
        Assert.Contains("if (!SuppressStartupDialogsForTest)", StartupChecks);
        Assert.Contains(
            "Environment.SetEnvironmentVariable(SuppressStartupDialogsEnvVar, \"1\");",
            TestSettingsIsolation);
    }

    [Fact]
    public void StartupChain_PreparesOnePlanBeforeShowingSerializedSteps()
    {
        string[] calls =
        [
            "StartupDialogPlan startupDialogPlan = await PrepareStartupDialogPlanAsync();",
            "StartupDialogStep.TelemetryConsent, ShowTelemetryConsentIfNeededAsync",
            "StartupDialogStep.WindowMode, CheckFirstRunWindowModeAsync",
            "StartupDialogStep.ResultTempLocation, CheckFirstRunResultStoreTempLocationAsync",
            "StartupDialogStep.Everything, CheckEverythingAsync",
            "StartupDialogStep.ContextMenu, CheckFirstRunContextMenuAsync",
            "StartupDialogStep.IndexOnboarding, CheckFirstRunIndexOnboardingAsync",
            "StartupDialogStep.FontContrast, ShowFontContrastWarningIfNeededAsync",
            "StartupDialogStep.CpuSemanticWarning, ShowCpuSemanticWarningIfNeededAsync",
            "StartupDialogStep.SemanticQualification, OfferSemanticModelQualificationIfNeededAsync",
            "StartupDialogStep.AppUpdateConsent, MaybeShowAppUpdateConsentPromptAsync",
        ];

        int previous = -1;
        foreach (string call in calls)
        {
            int current = StartupChecks.IndexOf(call, StringComparison.Ordinal);
            Assert.True(current > previous, $"Expected startup call in order: {call}");
            previous = current;
        }
    }

    [Fact]
    public void Plan_PredictsGuardedPromptsAndCachesSlowDiscovery()
    {
        Assert.Contains(
            "ResultStoreTempLocationService.ProbeForStartupAsync(",
            StartupProgress);
        Assert.Contains(
            "Task<EverythingStartupDetection> everythingTask = Task.Run(DetectEverythingStartupState);",
            StartupProgress);
        Assert.Contains(
            "Task.Run(IsContextMenuRegistered)",
            StartupProgress);
        Assert.Contains(
            "_preparedResultStoreTempLocationProbe = await tempLocationTask",
            StartupProgress);
        Assert.Contains(
            "_preparedEverythingStartupDetection = await everythingTask",
            StartupProgress);
        Assert.Contains(
            "_preparedContextMenuRegistered = await contextMenuTask",
            StartupProgress);
        Assert.Contains("if (!ViewModel.TelemetryConsentPromptShown)", StartupProgress);
        Assert.Contains("if (!ViewModel.Settings.HasPromptedWindowMode)", StartupProgress);
        Assert.Contains("if (!_preparedResultStoreTempLocationProbe.CurrentDirectoryIsUsable)", StartupProgress);
        Assert.Contains("if (WillShowEverythingStartupPrompt(_preparedEverythingStartupDetection))", StartupProgress);
        Assert.Contains("if (detection.EverythingRunning)", StartupProgress);
        Assert.Contains("if (detection.EverythingExePath is null)", StartupProgress);
        Assert.Contains("return !ViewModel.SuppressEverythingNotRunningPrompt;", StartupProgress);
        Assert.Contains("if (!ViewModel.HasCompletedFirstRun && !_preparedContextMenuRegistered.Value)", StartupProgress);
        Assert.Contains(
            "if (!ViewModel.Settings.HasPromptedIndexOnboarding && ViewModel.Settings.IndexedRoots.Count == 0)",
            StartupProgress);
        Assert.Contains("if (WillShowFontContrastStartupPrompt())", StartupProgress);
        Assert.Contains("FontContrastWarningService.ShouldCheck(", StartupProgress);
        Assert.Contains("FontContrastWarningService.FindFirstIssue(", StartupProgress);
        Assert.Contains("if (ViewModel.ShouldShowCpuSemanticWarning)", StartupProgress);
        Assert.Contains("if (ViewModel.ShouldOfferSemanticModelQualification)", StartupProgress);
        Assert.Contains(
            "if (ViewModel.Settings.AppUpdateCheckMode == AppUpdateCheckMode.Prompt)",
            StartupProgress);

        foreach (string step in new[]
        {
            "TelemetryConsent",
            "WindowMode",
            "ResultTempLocation",
            "Everything",
            "ContextMenu",
            "IndexOnboarding",
            "FontContrast",
            "CpuSemanticWarning",
            "SemanticQualification",
            "AppUpdateConsent",
        })
        {
            Assert.Contains($"steps.Add(StartupDialogStep.{step});", StartupProgress);
        }
    }

    [Fact]
    public void SharedDialogFooter_ShowsStepCountPercentageAndProgressBar()
    {
        Assert.Contains("internal sealed record YaguStartupProgress(int Step, int Total)", YaguDialog);
        Assert.Contains("Step {progress.Step} of {progress.Total} - {progress.Percentage}% complete", YaguDialog);
        Assert.Contains("new ProgressBar", YaguDialog);
        Assert.Contains("Value = progress.Percentage", YaguDialog);
        Assert.Contains("using IDisposable progressScope = YaguDialog.BeginStartupProgress(", StartupProgress);
        Assert.Contains("if (plan.Count <= 1", StartupProgress);
        Assert.Contains("StartupProgressByOwner.Remove(ownerHwnd)", YaguDialog);
        Assert.Contains("StartupProgressByOwner[ownerHwnd] = previous", YaguDialog);
    }

    [Fact]
    public void CustomStartupWindows_UseTheSameProgressFooter()
    {
        string resultTemp = ReadAppFile("ResultStoreTempLocationWindow.cs");
        string semanticQualification = ReadAppFile(
            "UI", "Windows", "SemanticModelQualificationDialog.cs");

        Assert.Contains("YaguDialog.GetStartupProgress(_ownerHwnd)", resultTemp);
        Assert.Contains("YaguDialog.CreateStartupProgressElement(startupProgress)", resultTemp);
        Assert.Contains("YaguDialog.GetStartupProgress(_ownerHwnd)", semanticQualification);
        Assert.Contains("YaguDialog.CreateStartupProgressElement(startupProgress)", semanticQualification);
    }

    [Fact]
    public void StartupStepScope_CoversNestedFollowUpDialogs()
    {
        int begin = StartupProgress.IndexOf(
            "using IDisposable progressScope = YaguDialog.BeginStartupProgress(",
            StringComparison.Ordinal);
        int awaitAction = StartupProgress.IndexOf(
            "await action().ConfigureAwait(true);",
            begin,
            StringComparison.Ordinal);

        Assert.True(begin >= 0 && awaitAction > begin);
    }

    private static string ReadAppFile(params string[] relativeSegments) =>
        File.ReadAllText(Path.Combine([RepoRoot, "src", "Yagu", .. relativeSegments]));

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Yagu.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }
}
