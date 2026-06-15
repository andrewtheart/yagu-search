namespace Yagu.Tests;

public sealed class InstallerPackagingRegressionTests
{
    [Fact]
    public void InstallerBuild_StagesWindowsAppRuntimePrerequisite()
    {
        string root = FindRepoRoot();
        string buildInstaller = File.ReadAllText(Path.Combine(root, "build-installer.ps1"));
        string publishScript = File.ReadAllText(Path.Combine(root, "scripts", "publish-to-azure.ps1"));

        Assert.Contains("windows-app-runtime-prereq.ps1", buildInstaller);
        Assert.Contains("Copy-YaguWindowsAppRuntimePrerequisite -ProjectXml $projectXml -RepoRoot $repoRoot -DestinationRoot $stagingDir", buildInstaller);
        Assert.Contains("Copy-YaguWindowsAppRuntimePrerequisite -ProjectXml $projectXml -RepoRoot $root -DestinationRoot $publishDir", publishScript);
        Assert.Contains("Installer app version: $version", buildInstaller);
        Assert.Contains("Packaging Yagu $version", publishScript);
        Assert.Contains("Microsoft.WindowsAppRuntime.$majorMinor.msix", File.ReadAllText(Path.Combine(root, "scripts", "windows-app-runtime-prereq.ps1")));
        Assert.Contains("Microsoft.WindowsAppRuntime.DDLM.$majorMinor.msix", File.ReadAllText(Path.Combine(root, "scripts", "windows-app-runtime-prereq.ps1")));
    }

    [Fact]
    public void Installers_RunWindowsAppRuntimePrerequisiteBeforeLaunchOrCopy()
    {
        string root = FindRepoRoot();
        string inno = File.ReadAllText(Path.Combine(root, "installer", "yagu-installer.iss"));
        string publishScript = File.ReadAllText(Path.Combine(root, "scripts", "publish-to-azure.ps1"));

        Assert.Contains("InstallWindowsAppRuntime", inno);
        Assert.Contains("Install-WindowsAppRuntime.ps1", inno);
        Assert.Contains("if not InstallWindowsAppRuntime() then", inno);
        Assert.Contains("Abort;", inno);

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
        string deployPrompt = File.ReadAllText(Path.Combine(root, ".github", "prompts", "deploy-to-azurestaticsite.prompt.md"));

        Assert.Contains("$installerName = \"YaguSetup-$version-x64.exe\"", publishScript);
        Assert.Contains("$buildInstallerScript = Join-Path $root \"build-installer.ps1\"", publishScript);
        Assert.Contains("& $buildInstallerScript -Architecture x64", publishScript);
        Assert.Contains("--name $installerName", publishScript);
        Assert.Contains("--content-type \"application/octet-stream\"", publishScript);
        Assert.Contains("data-blob=\"$installerName\"", publishScript);
        Assert.Contains("Download Installer", publishScript);
        Assert.Contains("data-blob=\"$zipName\"", publishScript);
        Assert.Contains("Download ZIP", publishScript);
        Assert.Contains("Installer: in '$DownloadsContainer' container as $installerName", publishScript);
        Assert.Contains("ZIP:       in '$DownloadsContainer' container as $zipName", publishScript);

        Assert.Contains("uploads ZIP and installer EXE", deployPrompt);
        Assert.Contains("installer build", deployPrompt);
        Assert.Contains("Download Installer", deployPrompt);
        Assert.Contains("YaguSetup-<version>-x64.exe", deployPrompt);
        Assert.Contains("Download ZIP", deployPrompt);
        Assert.Contains("Yagu-<version>.zip", deployPrompt);
    }

    [Fact]
    public void InnoInstaller_IsArchitectureParameterizedAndSelfContained()
    {
        string root = FindRepoRoot();
        string inno = File.ReadAllText(Path.Combine(root, "installer", "yagu-installer.iss"));

        // Per-architecture parametrization: build-installer.ps1 passes /DYaguArch.
        Assert.Contains("#ifndef YaguArch", inno);
        Assert.Contains("#define YaguArch \"x64\"", inno);
        Assert.Contains("OutputBaseFilename=YaguSetup-{#MyAppVersion}-{#YaguArch}", inno);
        Assert.Contains("#if YaguArch == \"arm64\"", inno);
        Assert.Contains("ArchitecturesAllowed=arm64", inno);
        Assert.Contains("ArchitecturesInstallIn64BitMode=arm64", inno);
        Assert.Contains("#elif YaguArch == \"x86\"", inno);
        Assert.Contains("ArchitecturesAllowed=x86compatible", inno);
        Assert.Contains("ArchitecturesAllowed=x64compatible", inno);
        Assert.Contains("ArchitecturesInstallIn64BitMode=x64compatible", inno);

        // Self-contained Native AOT: no .NET runtime check / winget / download fallback.
        Assert.DoesNotContain("DotNet10", inno);
        Assert.DoesNotContain("Microsoft.DotNet.DesktopRuntime", inno);
        Assert.DoesNotContain("EnsureDotNet10RuntimeInstalled", inno);
        Assert.DoesNotContain("winget", inno);
        Assert.DoesNotContain("windowsdesktop-runtime", inno);

        // The Windows App Runtime prerequisite is still installed at post-install.
        Assert.Contains("function InstallWindowsAppRuntime(): Boolean;", inno);
        Assert.Contains("if not InstallWindowsAppRuntime() then", inno);
    }

    [Fact]
    public void BuildInstaller_ProducesOneInstallerPerArchitecture()
    {
        string root = FindRepoRoot();
        string buildInstaller = File.ReadAllText(Path.Combine(root, "build-installer.ps1"));

        // Accepts an architecture selector defaulting to all three.
        Assert.Contains("[ValidateSet('x64', 'x86', 'arm64', 'all')]", buildInstaller);
        Assert.Contains("$architectures = @('x64', 'x86', 'arm64')", buildInstaller);

        // Publishes self-contained per RID and suppresses the recursive installer hook.
        Assert.Contains("dotnet publish $projectPath -c Release -r $rid", buildInstaller);
        Assert.Contains("--self-contained", buildInstaller);
        Assert.Contains("-p:BuildInstallerOnPublish=false", buildInstaller);

        // Compiles one installer per architecture and keeps the latest per arch.
        Assert.Contains("/DYaguArch=$arch", buildInstaller);
        Assert.Contains("YaguSetup-$version-$arch.exe", buildInstaller);
        Assert.Contains("-Filter \"YaguSetup-*-$arch.exe\"", buildInstaller);
    }

    [Fact]
    public void Csproj_CrossCompilesRustCoreAndPackagesPerArchitecture()
    {
        string root = FindRepoRoot();
        string csproj = File.ReadAllText(Path.Combine(root, "Yagu", "Yagu.csproj"));

        // RuntimeIdentifier maps to an installer architecture token, and the
        // AfterPublish hook packages exactly that architecture (only when a RID is set).
        Assert.Contains("<YaguInstallerArch Condition=\"'$(YaguInstallerArch)' == '' And '$(RuntimeIdentifier)' == 'win-x64'\">x64</YaguInstallerArch>", csproj);
        Assert.Contains("-SkipBuild -Architecture $(YaguInstallerArch)", csproj);
        Assert.Contains("And '$(YaguInstallerArch)' != ''", csproj);

        // The Rust core is cross-compiled to match the RID via cargo --target.
        Assert.Contains("x86_64-pc-windows-msvc", csproj);
        Assert.Contains("i686-pc-windows-msvc", csproj);
        Assert.Contains("aarch64-pc-windows-msvc", csproj);
        Assert.Contains("--target $(RustTargetTriple)", csproj);
        Assert.Contains("target add $(RustTargetTriple)", csproj);
    }

    [Fact]
    public void Csproj_BarePublishBuildsAllThreeInstallers()
    {
        string root = FindRepoRoot();
        string csproj = File.ReadAllText(Path.Combine(root, "Yagu", "Yagu.csproj"));

        // A bare `dotnet publish` (no -r) lets the SDK auto-infer the host RID, which it
        // signals via UseCurrentRuntimeIdentifier == 'true'. That case fans out to build
        // all three installers rather than packaging a single architecture.
        Assert.Contains("<Target Name=\"BuildAllInstallersAfterPublish\"", csproj);
        Assert.Contains("'$(UseCurrentRuntimeIdentifier)' == 'true'", csproj);
        Assert.Contains("build-installer.ps1&quot; -Architecture all", csproj);

        // The fan-out still honors the opt-out flag used by build-installer.ps1 and
        // the local install/publish scripts so it never recurses.
        Assert.Contains("'$(BuildInstallerOnPublish)' != 'false' And '$(DesignTimeBuild)' != 'true' And '$(UseCurrentRuntimeIdentifier)' == 'true'", csproj);
    }

    [Fact]
    public void RuntimePrerequisiteInstaller_UsesMsixManifestIdentity()
    {
        string root = FindRepoRoot();
        string installScript = File.ReadAllText(Path.Combine(root, "scripts", "install-windows-app-runtime.ps1"));

        Assert.Contains("System.IO.Compression.ZipFile", installScript);
        Assert.Contains("AppxManifest.xml", installScript);
        Assert.DoesNotContain("[string]$RuntimeDir = (Join-Path $PSScriptRoot", installScript);
        Assert.Contains("if ([string]::IsNullOrWhiteSpace($RuntimeDir))", installScript);
        Assert.Contains("Get-AppxPackage -Name $Name -PackageTypeFilter Main,Framework", installScript);
        Assert.Contains("Add-AppxPackage -Path $msixPath -ErrorAction Stop", installScript);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Yagu.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }
}