namespace Yagu.Tests;

public sealed class InstallerPackagingRegressionTests
{
    [Fact]
    public void InstallerBuild_StagesWindowsAppRuntimePrerequisite()
    {
        string root = FindRepoRoot();
        string buildInstaller = File.ReadAllText(Path.Combine(root, "build-installer.ps1"));

        Assert.Contains("windows-app-runtime-prereq.ps1", buildInstaller);
        Assert.Contains("Copy-YaguWindowsAppRuntimePrerequisite -ProjectXml $projectXml -RepoRoot $repoRoot -DestinationRoot $stagingDir", buildInstaller);
        Assert.Contains("Installer app version: $version", buildInstaller);
        // The MSIX filename version token is discovered from the package (works for WAR 1.x "1.8" and
        // 2.x "2"), not derived as major.minor from the SDK version.
        Assert.Contains("Microsoft.WindowsAppRuntime.$runtimeToken.msix", File.ReadAllText(Path.Combine(root, "scripts", "windows-app-runtime-prereq.ps1")));
        Assert.Contains("Microsoft.WindowsAppRuntime.DDLM.$runtimeToken.msix", File.ReadAllText(Path.Combine(root, "scripts", "windows-app-runtime-prereq.ps1")));
    }

    [Fact]
    public void App_ShipsGplLicenseAndThirdPartyNoticesInInstallDir()
    {
        string root = FindRepoRoot();
        string csproj = File.ReadAllText(Path.Combine(root, "src", "Yagu", "Yagu.csproj"));
        string buildInstaller = File.ReadAllText(Path.Combine(root, "build-installer.ps1"));

        // The GPLv3 license and the consolidated third-party notices are copied to the app output so
        // every binary distribution carries the required license/attribution texts (GPLv3 conveyance +
        // the MIT/BSD/Apache/LGPL and 7-Zip/unRAR notice requirements).
        Assert.Contains("<Content Include=\"..\\..\\LICENSE\" Link=\"LICENSE\">", csproj);
        Assert.Contains("<Content Include=\"..\\..\\THIRD-PARTY-NOTICES.txt\" Link=\"THIRD-PARTY-NOTICES.txt\">", csproj);

        // The installer stages the whole publish output, so those files ship as <app>\LICENSE and
        // <app>\THIRD-PARTY-NOTICES.txt.
        Assert.Contains("Copy-Item -Path \"$publishDir\\*\" -Destination $stagingDir -Recurse -Force", buildInstaller);

        // Sanity: the notices file exists and carries the unRAR redistribution statement required by
        // the bundled 7-Zip 7z.dll.
        string notices = File.ReadAllText(Path.Combine(root, "THIRD-PARTY-NOTICES.txt"));
        Assert.Contains("develop a RAR (WinRAR) compatible archiver", notices);
    }

    [Fact]
    public void OcrWorker_IsPublishedSelfContained_SoItRunsWithoutDotnetInstalled()
    {
        string root = FindRepoRoot();
        string csproj = File.ReadAllText(Path.Combine(root, "src", "Yagu", "Yagu.csproj"));

        // The OCR worker is a separate managed process (PaddleSharp/Tesseract are not Native-AOT safe).
        // It must be PUBLISHED self-contained for win-x64 so it carries its own .NET runtime: Yagu.exe is
        // self-contained Native AOT and provides no shared runtime, so a framework-dependent worker fails
        // with "You must install .NET" on clean/offline machines (image-text OCR silently broke there).
        Assert.Contains("dotnet publish &quot;$(OcrWorkerProject)&quot; -c $(Configuration) -r win-x64 --self-contained true", csproj);
        Assert.Contains("net10.0\\win-x64\\publish\\", csproj);
        Assert.DoesNotContain("dotnet build &quot;$(OcrWorkerProject)&quot;", csproj);
    }

    [Fact]
    public void Installers_RunWindowsAppRuntimePrerequisiteBeforeLaunchOrCopy()
    {
        string root = FindRepoRoot();
        string inno = File.ReadAllText(Path.Combine(root, "installer", "yagu-installer.iss"));

        Assert.Contains("InstallWindowsAppRuntime", inno);
        Assert.Contains("Install-WindowsAppRuntime.ps1", inno);
        Assert.Contains("if not InstallWindowsAppRuntime() then", inno);
        Assert.Contains("Abort;", inno);
    }

    [Fact]
    public void Uninstaller_PromptsToKeepOrDeleteUserSettingsFile()
    {
        string root = FindRepoRoot();
        string inno = File.ReadAllText(Path.Combine(root, "installer", "yagu-installer.iss"));

        // Interactive uninstall asks whether to keep the per-user settings file; the prompt targets
        // the exact %APPDATA%\Yagu\settings.json that SettingsService uses.
        Assert.Contains("procedure MaybeRemoveUserSettings();", inno);
        Assert.Contains(@"ExpandConstant('{userappdata}\Yagu\settings.json')", inno);
        Assert.Contains("Do you want to keep your Yagu settings and preferences?", inno);
        Assert.Contains("mbConfirmation, MB_YESNO) = IDNO then", inno);

        // Default is to KEEP: silent uninstalls never prompt/delete, and only an explicit "No"
        // removes the file (folder removed only if it becomes empty, preserving logs/other data).
        Assert.Contains("if UninstallSilent() then", inno);
        Assert.Contains("DeleteFile(SettingsFile);", inno);
        Assert.Contains(@"RemoveDir(ExpandConstant('{userappdata}\Yagu'));", inno);

        // The prompt is wired into the post-uninstall step alongside the registry cleanup.
        Assert.Contains("MaybeRemoveUserSettings();", inno);
    }

    [Fact]
    public void Installer_KillsRunningYaguProcessesBeforeInstallAndUninstall()
    {
        string root = FindRepoRoot();
        string inno = File.ReadAllText(Path.Combine(root, "installer", "yagu-installer.iss"));

        // One helper force-terminates the app PLUS both out-of-process workers so no file is locked
        // while setup writes (install) or deletes (uninstall) them. "/T" also takes the worker subtree.
        Assert.Contains("procedure KillYaguProcesses();", inno);
        Assert.Contains(@"ExpandConstant('{sys}\taskkill.exe')", inno);
        Assert.Contains("'/F /T /IM Yagu.exe'", inno);
        Assert.Contains("'/F /IM Yagu.SemanticWorker.exe'", inno);
        Assert.Contains("'/F /IM Yagu.OcrWorker.exe'", inno);

        // Install path: PrepareToInstall runs before any files are written (and before the Restart
        // Manager scan). Uninstall path: InitializeUninstall runs before any files are removed.
        Assert.Contains("function PrepareToInstall(var NeedsRestart: Boolean): String;", inno);
        Assert.Contains("function InitializeUninstall(): Boolean;", inno);

        // The killer is defined before both hooks (Pascal requires top-down definition) and called by each
        // (definition header + one call per hook => at least three occurrences of "KillYaguProcesses();").
        int killerDef = inno.IndexOf("procedure KillYaguProcesses();", System.StringComparison.Ordinal);
        int prepare = inno.IndexOf("function PrepareToInstall(", System.StringComparison.Ordinal);
        int uninit = inno.IndexOf("function InitializeUninstall(", System.StringComparison.Ordinal);
        Assert.True(killerDef >= 0 && prepare > killerDef && uninit > killerDef,
            "KillYaguProcesses must be defined before the install/uninstall hooks that call it.");
        int occurrences = System.Text.RegularExpressions.Regex.Matches(inno, @"KillYaguProcesses\(\);").Count;
        Assert.True(occurrences >= 3, $"expected KillYaguProcesses called from both hooks, found {occurrences} occurrence(s).");
    }

    [Fact]
    public void Installer_AbortsWhenSmartAppControlIsEnforcing()
    {
        string root = FindRepoRoot();
        string inno = File.ReadAllText(Path.Combine(root, "installer", "yagu-installer.iss"));

        // SAC mode is read from the canonical CI policy DWORD; only Enforce (state 1) blocks.
        Assert.Contains("function SmartAppControlEnforced(): Boolean;", inno);
        Assert.Contains(@"SYSTEM\CurrentControlSet\Control\CI\Policy", inno);
        Assert.Contains("VerifiedAndReputablePolicyState", inno);
        Assert.Contains("Result := (State = 1);", inno);

        // The check runs in InitializeSetup and cancels setup before any files are copied.
        Assert.Contains("function InitializeSetup(): Boolean;", inno);
        Assert.Contains("if SmartAppControlEnforced() then", inno);
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

        // Publishes self-contained per RID and suppresses the recursive installer hook. The publish
        // invocation is built as a splatted argument array (@publishArgs) rather than an inline command.
        Assert.Contains("$publishArgs = @($projectPath, '-c', 'Release', '-r', $rid", buildInstaller);
        Assert.Contains("& dotnet publish @publishArgs", buildInstaller);
        Assert.Contains("--self-contained", buildInstaller);
        Assert.Contains("-p:BuildInstallerOnPublish=false", buildInstaller);

        // Compiles one installer per architecture and keeps the latest per arch. The optional
        // offline (OCR-bundled) edition appends an "-offline" suffix to the output name (and its
        // retention filter), so both names are built from the shared $ocrSuffix token.
        Assert.Contains("/DYaguArch=$arch", buildInstaller);
        Assert.Contains("$ocrSuffix = if ($IncludeOcr) { '-offline' } else { '' }", buildInstaller);
        Assert.Contains("YaguSetup-$version-$arch$ocrSuffix.exe", buildInstaller);
        Assert.Contains("-Filter \"YaguSetup-*-$arch$ocrSuffix.exe\"", buildInstaller);
    }

    [Fact]
    public void OfflineEdition_BundlesVoidtoolsEverythingSetupAndLicense()
    {
        string root = FindRepoRoot();
        string buildInstaller = File.ReadAllText(Path.Combine(root, "build-installer.ps1"));
        string prereq = File.ReadAllText(Path.Combine(root, "scripts", "everything-prereq.ps1"));
        string inno = File.ReadAllText(Path.Combine(root, "installer", "yagu-installer.iss"));
        string license = File.ReadAllText(Path.Combine(root, "installer", "Everything-License.txt"));

        // build-installer.ps1 loads the helper and stages the bundle only for the offline (-IncludeOcr) edition.
        Assert.Contains("scripts\\everything-prereq.ps1", buildInstaller);
        Assert.Contains("Copy-YaguEverythingPrerequisite -RepoRoot $repoRoot -DestinationRoot $stagingDir", buildInstaller);

        // The helper downloads the voidtools setup, stages it under everything-setup, and copies the notice.
        Assert.Contains("function Copy-YaguEverythingPrerequisite", prereq);
        Assert.Contains("https://www.voidtools.com/", prereq);
        Assert.Contains("everything-setup", prereq);
        Assert.Contains("Everything-License.txt", prereq);

        // The bundled setup version must match the version the app resolves and downloads (no drift).
        Assert.Contains($"$script:EverythingVersion = '{Yagu.Services.EverythingAssetPaths.Version}'", prereq);

        // The recursesubdirs [Files] entry ships <staging>\everything-setup; the ISS documents it.
        Assert.Contains("everything-setup", inno);

        // The redistribution notice carries the voidtools copyright + the MIT-style permission notice.
        Assert.Contains("Copyright (C) 2018 David Carpenter", license);
        Assert.Contains("Permission is hereby granted", license);
    }

    [Fact]
    public void Csproj_CrossCompilesRustCoreAndPackagesPerArchitecture()
    {
        string root = FindRepoRoot();
        string csproj = File.ReadAllText(Path.Combine(root, "src", "Yagu", "Yagu.csproj"));

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
        string csproj = File.ReadAllText(Path.Combine(root, "src", "Yagu", "Yagu.csproj"));

        // A bare `dotnet publish` (no -r) lets the SDK auto-infer the host RID, which it
        // signals via UseCurrentRuntimeIdentifier == 'true'. That case fans out to build
        // all installer variants (x64/x86/arm64 + the x64-offline edition) rather than
        // packaging a single architecture.
        Assert.Contains("<Target Name=\"BuildAllInstallersAfterPublish\"", csproj);
        Assert.Contains("'$(UseCurrentRuntimeIdentifier)' == 'true'", csproj);
        Assert.Contains("build-all-installers.ps1&quot;", csproj);

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

    [Fact]
    public void BuildAllInstallers_SelectsVariantsAndDelegatesToBuildInstaller()
    {
        string root = FindRepoRoot();
        string buildAll = File.ReadAllText(Path.Combine(root, "build-all-installers.ps1"));

        // Selectable variants (one or more, plus 'all') via a validated -Variant list.
        Assert.Contains("[ValidateSet('x64', 'x86', 'arm64', 'x64-offline', 'all')]", buildAll);
        Assert.Contains("[string[]]$Variant = @('all')", buildAll);

        // Only x64-offline bundles OCR (the native PaddleOCR runtime is win-x64 only, so there is no
        // x86-offline / arm64-offline); every variant delegates to build-installer.ps1 instead of duplicating it.
        Assert.Contains("'x64-offline' = @{ Architecture = 'x64'", buildAll);
        Assert.Contains("if ($spec.IncludeOcr) { $params['IncludeOcr'] = $true }", buildAll);
        Assert.Contains("& $buildInstaller @params", buildAll);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Yagu.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }
}