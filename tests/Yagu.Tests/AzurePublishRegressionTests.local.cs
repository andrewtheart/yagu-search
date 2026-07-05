namespace Yagu.Tests;

// Local-only regression tests for the Azure publish/deploy script (scripts/publish-to-azure.ps1).
//
// This file is intentionally gitignored and is NOT committed to any remote branch. The publish
// script is a local-only maintainer tool that is never committed or run in CI, so these tests
// would fail on a fresh clone (the script would be missing). They are kept here so the local
// publish workflow stays covered on the maintainer's machine.
public sealed class AzurePublishRegressionTests
{
    [Fact]
    public void AzurePublish_CopiesWindowsAppRuntimePrerequisiteToPublishDir()
    {
        string root = FindRepoRoot();
        string publishScript = File.ReadAllText(Path.Combine(root, "scripts", "publish-to-azure.ps1"));

        Assert.Contains("Copy-YaguWindowsAppRuntimePrerequisite -ProjectXml $projectXml -RepoRoot $root -DestinationRoot $publishDir", publishScript);
        Assert.Contains("Packaging Yagu $version", publishScript);
    }

    [Fact]
    public void AzurePublish_InstallsRuntimePrerequisiteBeforeCopyingFiles()
    {
        string root = FindRepoRoot();
        string publishScript = File.ReadAllText(Path.Combine(root, "scripts", "publish-to-azure.ps1"));

        int runtimeInstaller = publishScript.IndexOf("$runtimeInstaller = Join-Path $sourceDir", StringComparison.Ordinal);
        int copyFiles = publishScript.IndexOf("# Copy files", StringComparison.Ordinal);
        Assert.True(runtimeInstaller >= 0, "Generated Install-Yagu.ps1 must invoke the runtime prerequisite installer.");
        Assert.True(copyFiles >= 0, "Generated Install-Yagu.ps1 should still copy files.");
        Assert.True(runtimeInstaller < copyFiles, "Generated Install-Yagu.ps1 should install prerequisites before copying files.");
    }

    [Fact]
    public void AzurePublish_UploadsInstallerAndAddsItToYaguCard()
    {
        string root = FindRepoRoot();
        string publishScript = File.ReadAllText(Path.Combine(root, "scripts", "publish-to-azure.ps1"));

        Assert.Contains("$installerName = \"YaguSetup-$version-x64.exe\"", publishScript);
        Assert.Contains("$buildInstallerScript = Join-Path $root \"build-installer.ps1\"", publishScript);
        Assert.Contains("& $buildInstallerScript -Architecture x64", publishScript);
        Assert.Contains("--name $installerName", publishScript);
        Assert.Contains("--content-type \"application/octet-stream\"", publishScript);
        Assert.Contains("data-blob=\"$installerName\"", publishScript);
        Assert.Contains("Download Installer", publishScript);
        Assert.Contains("Installer: in '$DownloadsContainer' container as $installerName", publishScript);

        // Installer-only publish: the ZIP is still built locally but is NOT uploaded, and the
        // Yagu card no longer offers a ZIP download link.
        Assert.Contains("Skipping ZIP upload; publishing only", publishScript);
        Assert.DoesNotContain("data-blob=\"$zipName\"", publishScript);
        Assert.DoesNotContain("Download ZIP", publishScript);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Yagu.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }
}
