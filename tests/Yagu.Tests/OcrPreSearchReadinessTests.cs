using Yagu.Services.Ocr;

namespace Yagu.Tests;

/// <summary>
/// Covers the pre-search OCR gate decision and its dialog strings. The gate exists so a search that
/// uses image-text search downloads its components up front instead of discovering them mid-search,
/// so the cases that matter are "must ask", "already approved, just download", and "get out of the way".
/// </summary>
public class OcrPreSearchReadinessTests
{
    private static OcrAssetRequirement Missing(long bytes = 349L * 1024 * 1024) => new()
    {
        EngineDisplayName = "PaddleSharp",
        DownloadNeeded = true,
        ApproxBytes = bytes,
        MissingComponents = ["OCR engine runtime (~349 MB)"],
    };

    private static OcrAssetRequirement Present() => new()
    {
        EngineDisplayName = "PaddleSharp",
        DownloadNeeded = false,
        ApproxBytes = 0,
        MissingComponents = [],
    };

    [Fact]
    public void Decide_WithImageSearchOff_NeverInterrupts()
    {
        Assert.Equal(
            OcrPreSearchAction.Proceed,
            OcrPreSearchReadiness.Decide(searchImageText: false, Missing(), consentGranted: false));
    }

    [Fact]
    public void Decide_WithEveryComponentPresent_StartsTheSearchImmediately()
    {
        Assert.Equal(
            OcrPreSearchAction.Proceed,
            OcrPreSearchReadiness.Decide(searchImageText: true, Present(), consentGranted: false));
    }

    [Fact]
    public void Decide_WithoutARequirement_StartsTheSearchImmediately()
    {
        // A probe failure must not block the search; the engine still gates its own download later.
        Assert.Equal(
            OcrPreSearchAction.Proceed,
            OcrPreSearchReadiness.Decide(searchImageText: true, requirement: null, consentGranted: false));
    }

    [Fact]
    public void Decide_WithMissingComponentsAndNoConsent_AsksFirst()
    {
        Assert.Equal(
            OcrPreSearchAction.AskForConsent,
            OcrPreSearchReadiness.Decide(searchImageText: true, Missing(), consentGranted: false));
    }

    [Fact]
    public void Decide_WithMissingComponentsAfterConsent_DownloadsWithoutAskingAgain()
    {
        // Consent is remembered across sessions, so a user who already approved must not be re-prompted
        // every search — but the components still have to arrive before the search runs.
        Assert.Equal(
            OcrPreSearchAction.Download,
            OcrPreSearchReadiness.Decide(searchImageText: true, Missing(), consentGranted: true));
    }

    [Theory]
    [InlineData(0, "Downloading… 0s elapsed")]
    [InlineData(45, "Downloading… 45s elapsed")]
    [InlineData(60, "Downloading… 1m 0s elapsed")]
    [InlineData(135, "Downloading… 2m 15s elapsed")]
    public void DescribeElapsed_ReadsAsElapsedTime(int seconds, string expected)
        => Assert.Equal(expected, OcrPreSearchReadiness.DescribeElapsed(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void DescribeElapsed_ClampsNegativeTime()
        => Assert.Equal("Downloading… 0s elapsed", OcrPreSearchReadiness.DescribeElapsed(TimeSpan.FromSeconds(-5)));

    [Fact]
    public void DescribeComponents_NamesTheEngineAndTheMissingParts()
    {
        Assert.Equal(
            "PaddleSharp: OCR engine runtime (~349 MB)",
            OcrPreSearchReadiness.DescribeComponents(Missing()));
    }

    [Fact]
    public void DescribeComponents_FallsBackToTheApproximateSize()
    {
        var requirement = new OcrAssetRequirement
        {
            EngineDisplayName = "Tesseract",
            DownloadNeeded = true,
            ApproxBytes = 5L * 1024 * 1024,
            MissingComponents = [],
        };
        Assert.Equal("Tesseract: about 5 MB", OcrPreSearchReadiness.DescribeComponents(requirement));
    }

    // The window layer is not compiled into this assembly, so its wiring is source-pinned.
    private static readonly string MainWindowSource = ReadMainWindowSources();

    [Fact]
    public void PreSearchGate_RunsTheOcrCheckBeforeEveryOtherGate()
    {
        int gate = MainWindowSource.IndexOf("CheckOcrComponentsAndWarnAsync()", StringComparison.Ordinal);
        int cloud = MainWindowSource.IndexOf("CheckCloudDriveScanAndWarnAsync()", StringComparison.Ordinal);
        Assert.True(gate >= 0, "The pre-search OCR gate is missing.");
        Assert.True(cloud >= 0, "The cloud-drive gate is missing.");
        // Components must be resolved before any other prompt so the download happens up front.
        Assert.True(gate < cloud, "The OCR gate must run before the other pre-search gates.");
    }

    [Fact]
    public void PreSearchGate_DownloadsBeforeSearching_AndSurvivesFailureWithSearchAnyway()
    {
        Assert.Contains("OcrPreSearchReadiness.Decide(", MainWindowSource);
        Assert.Contains("engine.DescribeAssetRequirement()", MainWindowSource);
        Assert.Contains("OcrDownloadConsentDialog.RequestConsentAsync(this, requirement)", MainWindowSource);
        Assert.Contains("DownloadOcrComponentsWithProgressAsync", MainWindowSource);
        // The search must wait on readiness, not race it.
        Assert.Contains("engine.EnsureReadyAsync(cancellation.Token)", MainWindowSource);
        Assert.Contains("\"Search anyway\"", MainWindowSource);
        Assert.Contains("ShowAppToast(\"OCR components downloaded\")", MainWindowSource);
    }

    [Fact]
    public void PreSearchGate_DoesNotReprompt_OnceAnEngineIsProvenReadyThisSession()
    {
        // A misreading on-disk probe must not turn into a download prompt on every single search.
        Assert.Contains("_ocrComponentsVerified.Contains(engineId)", MainWindowSource);
        Assert.Contains("_ocrComponentsVerified.Add(engineId)", MainWindowSource);
    }

    [Fact]
    public void DeveloperOptions_CanResetOcrConsentWithoutDeletingInstalledAssets()
    {
        string viewModel = MainViewModelPartials.Text;
        Assert.Contains("public async Task ResetOcrDownloadConsentAsync()", viewModel);
        Assert.Contains("OcrDownloadGate.ConsentGranted = false;", viewModel);
        Assert.Contains("settings => settings.OcrDownloadConsented = false", viewModel);
        int start = viewModel.IndexOf("public async Task ResetOcrDownloadConsentAsync()", StringComparison.Ordinal);
        string reset = viewModel.Substring(start, Math.Min(650, viewModel.Length - start));
        Assert.DoesNotContain("Delete", reset);

        string settings = File.ReadAllText(Path.Combine(
            RepoRoot, "src", "Yagu", "UI", "Windows", "Settings", "SettingsWindow.xaml.cs"));
        Assert.Contains("Reset OCR download consent", settings);
        Assert.Contains("await _viewModel.ResetOcrDownloadConsentAsync();", settings);
        Assert.Contains("RegisterDefaultResetButton(resetOcrDownloadConsent", settings);
    }

    [Fact]
    public void DownloadDialog_ShowsLiveElapsedProgress_AndIsTitleBarLess()
    {
        Assert.Contains("OcrPreSearchReadiness.DescribeElapsed(started.Elapsed)", MainWindowSource);
        Assert.Contains("new ProgressBar { IsIndeterminate = true", MainWindowSource);
        // Repo rule: every modal is title-bar-less unless the user asks otherwise.
        int dialog = MainWindowSource.IndexOf("Title = \"Downloading OCR components\"", StringComparison.Ordinal);
        Assert.True(dialog >= 0, "The OCR download progress dialog is missing.");
        Assert.Contains("ShowTitleBar = false", MainWindowSource[dialog..(dialog + 700)]);
    }

    [Fact]
    public void AppToast_IsAnInAppSnackbarAtTheBottomOfTheWindow()
    {
        string xaml = File.ReadAllText(Path.Combine(
            RepoRoot, "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.xaml"));
        int toast = xaml.IndexOf("x:Name=\"AppToast\"", StringComparison.Ordinal);
        Assert.True(toast >= 0, "The app-level toast is missing from MainWindow.xaml.");
        string window = xaml[toast..Math.Min(xaml.Length, toast + 1200)];
        Assert.Contains("VerticalAlignment=\"Bottom\"", window);
        Assert.Contains("x:Name=\"AppToastText\"", window);
        Assert.Contains("Click=\"OnAppToastDismissClick\"", window);
    }

    private static string RepoRoot => FindRepoRoot(AppContext.BaseDirectory);

    private static string FindRepoRoot(string start)
    {
        var dir = new DirectoryInfo(start);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Yagu.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string ReadMainWindowSources()
    {
        string root = Path.Combine(RepoRoot, "src", "Yagu", "UI", "Windows", "MainWindow");
        return string.Join(
            Environment.NewLine,
            Directory.GetFiles(root, "MainWindow*.cs")
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(File.ReadAllText));
    }
}
