using Yagu.Services;

namespace Yagu;

/// <summary>Predicts and tracks the guarded setup steps shown during the awaited startup-modal chain.</summary>
public sealed partial class MainWindow
{
    private const string SuppressStartupDialogsEnvVar = "YAGU_TEST_SUPPRESS_STARTUP_DIALOGS";

    private static bool SuppressStartupDialogsForTest => string.Equals(
        Environment.GetEnvironmentVariable(SuppressStartupDialogsEnvVar),
        "1",
        StringComparison.Ordinal);

    private enum StartupDialogStep
    {
        TelemetryConsent,
        WindowMode,
        ResultTempLocation,
        Everything,
        ContextMenu,
        IndexOnboarding,
        FontContrast,
        CpuSemanticWarning,
        SemanticQualification,
        AppUpdateConsent,
    }

    private sealed class StartupDialogPlan(IReadOnlyList<StartupDialogStep> steps)
    {
        public int Count => steps.Count;

        public bool TryGetPosition(StartupDialogStep step, out int position)
        {
            for (int i = 0; i < steps.Count; i++)
            {
                if (steps[i] == step)
                {
                    position = i + 1;
                    return true;
                }
            }

            position = 0;
            return false;
        }
    }

    private ResultStoreTempLocationProbe? _preparedResultStoreTempLocationProbe;
    private EverythingStartupDetection? _preparedEverythingStartupDetection;
    private bool? _preparedContextMenuRegistered;

    private async Task<StartupDialogPlan> PrepareStartupDialogPlanAsync()
    {
        Task<ResultStoreTempLocationProbe> tempLocationTask =
            ResultStoreTempLocationService.ProbeForStartupAsync(
                ViewModel.SearchResultTempDirectory,
                ViewModel.HasChosenSearchResultTempDirectory);
        Task<EverythingStartupDetection> everythingTask = Task.Run(DetectEverythingStartupState);
        Task<bool> contextMenuTask = ViewModel.HasCompletedFirstRun
            ? Task.FromResult(true)
            : Task.Run(IsContextMenuRegistered);

        _preparedResultStoreTempLocationProbe = await tempLocationTask.ConfigureAwait(true);
        _preparedEverythingStartupDetection = await everythingTask.ConfigureAwait(true);
        _preparedContextMenuRegistered = await contextMenuTask.ConfigureAwait(true);

        var steps = new List<StartupDialogStep>(10);
        if (!ViewModel.TelemetryConsentPromptShown)
            steps.Add(StartupDialogStep.TelemetryConsent);
        if (!ViewModel.Settings.HasPromptedWindowMode)
            steps.Add(StartupDialogStep.WindowMode);
        if (!_preparedResultStoreTempLocationProbe.CurrentDirectoryIsUsable)
            steps.Add(StartupDialogStep.ResultTempLocation);
        if (WillShowEverythingStartupPrompt(_preparedEverythingStartupDetection))
            steps.Add(StartupDialogStep.Everything);
        if (!ViewModel.HasCompletedFirstRun && !_preparedContextMenuRegistered.Value)
            steps.Add(StartupDialogStep.ContextMenu);
        if (!ViewModel.Settings.HasPromptedIndexOnboarding && ViewModel.Settings.IndexedRoots.Count == 0)
            steps.Add(StartupDialogStep.IndexOnboarding);
        if (WillShowFontContrastStartupPrompt())
            steps.Add(StartupDialogStep.FontContrast);
        if (ViewModel.ShouldShowCpuSemanticWarning)
            steps.Add(StartupDialogStep.CpuSemanticWarning);
        if (ViewModel.ShouldOfferSemanticModelQualification)
            steps.Add(StartupDialogStep.SemanticQualification);
        if (ViewModel.Settings.AppUpdateCheckMode == AppUpdateCheckMode.Prompt)
            steps.Add(StartupDialogStep.AppUpdateConsent);

        return new StartupDialogPlan(steps);
    }

    private bool WillShowEverythingStartupPrompt(EverythingStartupDetection detection)
    {
        if (detection.EverythingRunning)
            return false;
        if (detection.EverythingExePath is null)
            return true;
        return !ViewModel.SuppressEverythingNotRunningPrompt;
    }

    private bool WillShowFontContrastStartupPrompt()
    {
        if (_settingsWindow is not null ||
            !FontContrastWarningService.ShouldCheck(
                ViewModel.SuppressFontContrastWarnings,
                ViewModel.FontContrastReminderAfterUtc,
                DateTimeOffset.UtcNow))
        {
            return false;
        }

        return FontContrastWarningService.FindFirstIssue(
            ViewModel.GetFontContrastCandidates(),
            ResolveFontContrastTheme()) is not null;
    }

    private async Task RunStartupDialogStepAsync(
        StartupDialogPlan plan,
        StartupDialogStep step,
        Func<Task> action)
    {
        if (plan.Count <= 1 || !plan.TryGetPosition(step, out int position))
        {
            await action().ConfigureAwait(true);
            return;
        }

        using IDisposable progressScope = YaguDialog.BeginStartupProgress(_hwnd, position, plan.Count);
        await action().ConfigureAwait(true);
    }
}
