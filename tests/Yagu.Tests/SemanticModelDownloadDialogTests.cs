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
            Path.Combine(Root, "Yagu", "UI", "Windows", "SemanticModelDownloadDialog.cs"));

        // The loading state is the disclosure surface shown while the execution providers (AI runtime)
        // download during "loading options" — before the user commits to a model. It must explicitly
        // disclose the one-time runtime download and give a size, not just mention "a small AI model".
        Assert.Contains("one-time AI runtime for your hardware", src);
        Assert.Contains("usually a few hundred MB", src);
        Assert.Contains("runs entirely on your PC", src);
        Assert.DoesNotContain("uses a small AI model that runs entirely on your PC", src);
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
