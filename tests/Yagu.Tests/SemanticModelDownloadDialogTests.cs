using System.IO;
using Xunit;

namespace Yagu.Tests;

/// <summary>
/// Source-pin regression tests for <c>SemanticModelDownloadDialog</c>. The dialog pulls in
/// WindowsAppSDK/Foundry so it can't be exercised at runtime here; these pins guard the first-run
/// consent/disclosure text against regressions.
/// </summary>
public sealed class SemanticModelDownloadDialogTests
{
    [Fact]
    public void LoadingState_DisclosesOneTimeAiRuntimeDownloadAndSize()
    {
        string src = File.ReadAllText(
            Path.Combine(Root, "src", "Yagu", "UI", "Windows", "SemanticModelDownloadDialog.cs"));

        // The loading state is the disclosure surface shown while the execution providers (AI runtime)
        // download during "loading options" — before the user commits to a model. It must explicitly
        // disclose the one-time runtime download and give a size, not just mention "a small AI model".
        Assert.Contains("one-time AI runtime for your hardware", src);
        Assert.Contains("usually a few hundred MB", src);
        Assert.Contains("runs entirely on your PC", src);
        Assert.DoesNotContain("uses a small AI model that runs entirely on your PC", src);
    }

    [Fact]
    public void StartDownload_PinsExplicitlySelectedModel_EvenWhenRecommended()
    {
        string src = File.ReadAllText(
            Path.Combine(Root, "src", "Yagu", "UI", "Windows", "SemanticModelDownloadDialog.cs"));

        // An explicit pick must PIN that model as an override so it persists and shows as the current
        // model, even when the recommended model is selected. Previously the recommended pick was
        // collapsed to "auto" (empty), so selecting it did not stick and the current-model label showed
        // the last-loaded model instead.
        Assert.Contains("string? aliasToStore = _selected.Alias;", src);
        Assert.DoesNotContain("_selected.IsRecommended ? string.Empty : _selected.Alias", src);
    }

    private static string Root => FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Yagu.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Could not locate repo root (Yagu.sln).");
    }
}
