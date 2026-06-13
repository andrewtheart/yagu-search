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
    public void InnoInstaller_RequiresDotNet10RuntimeBeforeInstall()
    {
        string root = FindRepoRoot();
        string inno = File.ReadAllText(Path.Combine(root, "installer", "yagu-installer.iss"));

        Assert.Contains("DotNet10RuntimeDownloadUrl", inno);
        Assert.Contains("https://builds.dotnet.microsoft.com/dotnet/Runtime/10.0.9/dotnet-runtime-10.0.9-win-x64.exe", inno);
        Assert.Contains("DotNet10RuntimeRegistrySubkey", inno);
        Assert.Contains(@"SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.NETCore.App", inno);
        Assert.Contains("RegGetValueNames(HKLM64, '{#DotNet10RuntimeRegistrySubkey}', RuntimeVersions)", inno);
        Assert.Contains("RegGetValueNames(HKLM32, '{#DotNet10RuntimeRegistrySubkey}', RuntimeVersions)", inno);
        Assert.Contains("Copy(RuntimeVersions[I], 1, 3) = '10.'", inno);
        Assert.Contains("function EnsureDotNet10RuntimeInstalled(): Boolean;", inno);
        Assert.Contains("function NextButtonClick(CurPageID: Integer): Boolean;", inno);
        Assert.Contains("if CurPageID = wpReady then", inno);
        Assert.Contains("Result := EnsureDotNet10RuntimeInstalled()", inno);
        Assert.Contains("if IsDotNet10RuntimeInstalled() then", inno);
        Assert.Contains("Yagu requires the .NET 10.0 Runtime for Windows x64.", inno);
        Assert.Contains("Download and install {#DotNet10RuntimeDisplayName} now?", inno);

        int dotNetCheck = inno.IndexOf("function EnsureDotNet10RuntimeInstalled(): Boolean;", StringComparison.Ordinal);
        int windowsAppRuntimeInstall = inno.IndexOf("function InstallWindowsAppRuntime(): Boolean;", StringComparison.Ordinal);
        Assert.True(dotNetCheck >= 0, "Installer should check .NET 10 before installing files.");
        Assert.True(windowsAppRuntimeInstall >= 0, "Installer should still include the Windows App Runtime prerequisite installer.");
        Assert.True(dotNetCheck < windowsAppRuntimeInstall, ".NET runtime check should happen before post-install prerequisite handling.");
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