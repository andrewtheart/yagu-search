namespace Yagu.Tests;

public sealed class InstallerPackagingRegressionTests
{
    [Fact]
    public void WindowsPlatformDependencies_AreCurrentAndAlignedAcrossProjects()
    {
        string root = FindRepoRoot();
        string app = File.ReadAllText(Path.Combine(root, "src", "Yagu", "Yagu.csproj"));
        string semanticWorker = File.ReadAllText(Path.Combine(root, "src", "Yagu.SemanticWorker", "Yagu.SemanticWorker.csproj"));
        string textControl = File.ReadAllText(Path.Combine(root, "src", "vendor", "TextControlBox-WinUI", "TextControlBox", "TextControlBox.csproj"));
        string installer = File.ReadAllText(Path.Combine(root, "installer", "yagu-installer.iss"));
        string globalJson = File.ReadAllText(Path.Combine(root, "global.json"));

        Assert.Contains("<PackageReference Include=\"Microsoft.WindowsAppSDK\" Version=\"2.3.1\" />", app);
        Assert.Contains("<PackageReference Include=\"Microsoft.Windows.SDK.BuildTools\" Version=\"10.0.28000.2526\" />", app);
        Assert.Contains("<PackageReference Include=\"Microsoft.Web.WebView2\" Version=\"1.0.4078.44\"", app);
        Assert.Contains("<PackageReference Include=\"Microsoft.Graphics.Win2D\" Version=\"1.4.0\" />", app);
        Assert.Contains("<PackageReference Include=\"Microsoft.WindowsAppSDK.ML\" Version=\"2.1.74\" />", app);
        Assert.Contains("<PackageReference Include=\"System.Diagnostics.PerformanceCounter\" Version=\"10.0.10\" />", app);
        Assert.Contains("<WindowsSdkPackageVersion>10.0.26100.87</WindowsSdkPackageVersion>", app);

        Assert.Contains("<PackageReference Include=\"Microsoft.WindowsAppSDK.ML\" Version=\"2.1.74\" />", semanticWorker);
        Assert.Contains("<WindowsSdkPackageVersion>10.0.26100.87</WindowsSdkPackageVersion>", semanticWorker);
        Assert.Contains("<PackageReference Include=\"Microsoft.WindowsAppSDK\" Version=\"2.3.1\" />", textControl);
        Assert.Contains("<PackageReference Include=\"Microsoft.Windows.SDK.BuildTools\" Version=\"10.0.28000.2526\" />", textControl);
        Assert.Contains("<PackageReference Include=\"Microsoft.Graphics.Win2D\" Version=\"1.4.0\" />", textControl);
        Assert.Contains("<WindowsSdkPackageVersion>10.0.26100.87</WindowsSdkPackageVersion>", textControl);
        Assert.Contains("Installing Windows App Runtime...", installer);
        Assert.DoesNotContain("Windows App Runtime 1.8", installer);
        Assert.Contains("\"version\": \"10.0.302\"", globalJson);
    }

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
        // The privacy policy ships beside the app too, so the installer's InfoBefore page and the
        // in-app Help can point at <app>\PRIVACY.md.
        Assert.Contains("<Content Include=\"..\\..\\PRIVACY.md\" Link=\"PRIVACY.md\">", csproj);

        // The installer stages the whole publish output, so those files ship as <app>\LICENSE and
        // <app>\THIRD-PARTY-NOTICES.txt.
        Assert.Contains("Copy-Item -Path \"$publishDir\\*\" -Destination $stagingDir -Recurse -Force", buildInstaller);

        // Sanity: the notices file exists and carries the unRAR redistribution statement required by
        // the bundled 7-Zip 7z.dll.
        string notices = File.ReadAllText(Path.Combine(root, "THIRD-PARTY-NOTICES.txt"));
        Assert.Contains("develop a RAR (WinRAR) compatible archiver", notices);
    }

    [Fact]
    public void Installer_MatchesMultiTermLicenseAndPostInstallNoticesFlow()
    {
        string root = FindRepoRoot();
        string inno = File.ReadAllText(Path.Combine(root, "installer", "yagu-installer.iss"));
        string help = File.ReadAllText(Path.Combine(root, "HELP.md"));

        // This is the same standard Inno flow used by D:\multiTerm\installer\MultiTerm.iss:
        // GPL agreement during setup, then consolidated notices on the post-install information page.
        // Yagu adds a privacy-policy information page DURING setup (InfoBeforeFile), shown after the
        // license and before component/task selection, so the user learns how their data is handled
        // before installing.
        Assert.Contains("#define RepoRoot \"..\"", inno);
        Assert.Contains(@"LicenseFile={#RepoRoot}\LICENSE", inno);
        Assert.Contains(@"InfoBeforeFile={#RepoRoot}\PRIVACY.md", inno);
        Assert.Contains(@"InfoAfterFile={#RepoRoot}\THIRD-PARTY-NOTICES.txt", inno);
        Assert.True(
            inno.IndexOf("LicenseFile=", StringComparison.Ordinal) < inno.IndexOf("InfoAfterFile=", StringComparison.Ordinal),
            "The license agreement must precede the post-install notices, matching MultiTerm.");
        // The privacy page is shown during setup: after the license, before the post-install notices.
        Assert.True(
            inno.IndexOf("LicenseFile=", StringComparison.Ordinal) < inno.IndexOf("InfoBeforeFile=", StringComparison.Ordinal)
                && inno.IndexOf("InfoBeforeFile=", StringComparison.Ordinal) < inno.IndexOf("InfoAfterFile=", StringComparison.Ordinal),
            "The privacy policy (InfoBeforeFile) must appear during setup, after the license and before the post-install notices.");
        Assert.DoesNotContain("ACCEPTLICENSE", inno);
        Assert.DoesNotContain("ACCEPTLICENSE", help);
        Assert.Contains("/VERYSILENT /VERBOSELOG", inno);
        Assert.Contains("/VERYSILENT /VERBOSELOG", help);
    }

    [Fact]
    public void PrivacyPolicy_ExistsAndIsSurfacedDuringSetupAndInAppHelp()
    {
        string root = FindRepoRoot();
        string privacy = File.ReadAllText(Path.Combine(root, "PRIVACY.md"));
        string help = File.ReadAllText(Path.Combine(root, "HELP.md"));
        string inno = File.ReadAllText(Path.Combine(root, "installer", "yagu-installer.iss"));

        // The canonical policy exists at the repo root and states the core on-device guarantee.
        Assert.Contains("# Yagu — Privacy Policy", privacy);
        Assert.Contains("never leave your PC", privacy);

        // It is shown during installation (InfoBeforeFile) ...
        Assert.Contains(@"InfoBeforeFile={#RepoRoot}\PRIVACY.md", inno);

        // ... and the in-app Help (rendered from HELP.md) carries a Privacy Policy section that points
        // at the shipped PRIVACY.md so the user can read it from Help inside the app.
        Assert.Contains("## Privacy Policy", help);
        Assert.Contains("PRIVACY.md", help);
    }

    [Fact]
    public void IndexWorker_IsPublishedArchMatched_AndShipsInEveryInstallerVariant()
    {
        string root = FindRepoRoot();
        string csproj = File.ReadAllText(Path.Combine(root, "src", "Yagu", "Yagu.csproj"));
        string buildInstaller = File.ReadAllText(Path.Combine(root, "build-installer.ps1"));
        string inno = File.ReadAllText(Path.Combine(root, "installer", "yagu-installer.iss"));

        // The content-index worker is PUBLISHED self-contained (carries its own .NET runtime — Yagu.exe is
        // self-contained Native AOT and provides no shared runtime). UNLIKE the OCR / semantic workers it is
        // arch-matched (its native yagu_core.dll is arch-specific), so its RID tracks the app's RuntimeIdentifier.
        Assert.Contains("dotnet publish &quot;$(IndexWorkerProject)&quot; -c $(Configuration) -r $(IndexWorkerRid) --self-contained true", csproj);
        Assert.Contains("<IndexWorkerRid Condition=\"'$(IndexWorkerRid)' == '' And '$(RuntimeIdentifier)' != ''\">$(RuntimeIdentifier)</IndexWorkerRid>", csproj);

        // Copied into the self-contained publish dir (AfterTargets=Publish) so build-installer.ps1's recursive
        // stage carries it into <staging>\index-worker for EVERY architecture / edition.
        Assert.Contains("$(PublishDir)index-worker\\", csproj);
        Assert.Contains("AfterTargets=\"Publish\"", csproj);
        Assert.Contains("Copy-Item -Path \"$publishDir\\*\" -Destination $stagingDir -Recurse -Force", buildInstaller);

        // The recursesubdirs [Files] entry ships every staged subfolder (incl. index-worker) as <app>\...,
        // so all four installer variants (x64/x86/arm64/offline) contain a runnable worker with no ISS change.
        Assert.Contains("Source: \"{#StagingDir}\\*\"; DestDir: \"{app}\"; Flags: ignoreversion recursesubdirs createallsubdirs", inno);
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
    public void InstallerAndUninstaller_PromptToKeepOrDeleteContentIndexesSafely()
    {
        string root = FindRepoRoot();
        string inno = File.ReadAllText(Path.Combine(root, "installer", "yagu-installer.iss"));

        // Setup visibly detects both 32-bit and 64-bit registrations plus saved settings/index data,
        // then presents independent, default-on preservation choices before installation starts.
        Assert.Contains("procedure DetectExistingYaguState();", inno);
        Assert.Contains("DetectRegisteredInstall(HKCU32);", inno);
        Assert.Contains("DetectRegisteredInstall(HKCU64);", inno);
        Assert.Contains("DetectRegisteredInstall(HKLM32);", inno);
        Assert.Contains("DetectRegisteredInstall(HKLM64);", inno);
        Assert.Contains("procedure InitializeWizard();", inno);
        Assert.Contains("Existing Yagu installation or data found", inno);
        Assert.Contains("function CountRecognizedIndexScopes(const IndexRoot: String): Integer;", inno);
        Assert.Contains("\\b Recognized content indexes: ", inno);
        Assert.Contains("IntToStr(ExistingIndexScopeCount)", inno);
        Assert.Contains("Warning: clearing a Keep option permanently deletes that data.", inno);

        // The summary uses native rich text for meaningful emphasis. Preservation choices are fixed
        // below it rather than placed in TInputOptionWizardPage's scrollable options strip.
        Assert.Contains("ExistingDataPage := CreateCustomPage(", inno);
        Assert.Contains("ExistingSummaryViewer := TRichEditViewer.Create(ExistingDataPage);", inno);
        Assert.Contains("ExistingSummaryViewer.UseRichEdit := True;", inno);
        Assert.Contains("ExistingSummaryViewer.RTFText := Rtf;", inno);
        Assert.Contains("\\b\\cf3 Keep is the recommended default.", inno);
        Assert.Contains("\\b\\cf2 Warning: clearing a Keep option permanently deletes that data.", inno);
        Assert.Contains("OptionTop := ExistingDataPage.SurfaceHeight - (OptionCount * ScaleY(22));", inno);
        Assert.Contains("Keep settings and apply supported migrations (recommended)", inno);
        Assert.Contains("Keep content indexes; avoids rebuilding (recommended)", inno);
        Assert.Contains("Result := TNewCheckBox.Create(ExistingDataPage);", inno);
        Assert.Contains("Result.Checked := True;", inno);
        Assert.DoesNotContain("CreateInputOptionPage(", inno);

        Assert.Contains("procedure MaybeRemoveContentIndexes(DuringUninstall: Boolean);", inno);
        Assert.Contains("Do you want to keep your existing Yagu content indexes?", inno);
        Assert.Contains("Building it again can take a long time.", inno);
        Assert.Contains("mbConfirmation, MB_YESNO) = IDNO then", inno);
        Assert.Contains("(DuringUninstall and UninstallSilent())", inno);
        Assert.Contains("((not DuringUninstall) and WizardSilent())", inno);

        // The default per-user location is dedicated to Yagu. A custom root may contain unrelated files,
        // so setup identifies scope directories and never recursively deletes that custom root itself.
        Assert.Contains(@"ExpandConstant('{localappdata}\Yagu\content-index')", inno);
        Assert.Contains("TryReadJsonStringProperty(String(SettingsJson), 'IndexStorageDirectory', ParsedRoot)", inno);
        Assert.Contains("'PreservedIndexStorageDirectory'", inno);
        Assert.Contains("IsRecognizedIndexScope(Candidate)", inno);
        Assert.Contains("DeleteRecognizedIndexData(DefaultRoot, True);", inno);
        Assert.Contains("DeleteRecognizedIndexData(CustomRoot, False);", inno);
        Assert.Contains("if DeleteDedicatedRoot then", inno);
        Assert.Contains("RegWriteStringValue(HKCU, 'Software\\Yagu', 'PreservedIndexStorageDirectory', PreservedCustomIndexRoot);", inno);

        // Interactive uninstall asks before removing files. Install applies the visible page choices only
        // after Install is clicked; silent setup preserves settings and indexes without prompting.
        Assert.Contains("MaybeRemoveContentIndexes(True);", inno);
        Assert.Contains("MaybeRemoveContentIndexes(False);", inno);
        Assert.Contains("procedure MaybeRemoveUserSettingsDuringInstall();", inno);
        Assert.Contains("MaybeRemoveUserSettingsDuringInstall();", inno);
        Assert.DoesNotContain("if not FileExists(ExpandConstant('{app}\\{#MyAppExeName}')) then", inno);
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
    public void Installer_RequiresAdminSoUpdatesReplaceThePerMachineInstall()
    {
        string root = FindRepoRoot();
        string inno = File.ReadAllText(Path.Combine(root, "installer", "yagu-installer.iss"));

        // Yagu installs per-machine under {commonpf}\Yagu (C:\Program Files\Yagu), so an UPDATE must be
        // able to overwrite that location. Requiring admin makes Setup auto-elevate (UAC) on every run,
        // so a re-install/update always lands in the existing per-machine install without the user having
        // to know to "run as administrator". The old `lowest` + `PrivilegesRequiredOverridesAllowed=dialog`
        // model ran the installer NON-elevated by default and silently failed to update Program Files.
        Assert.Contains("PrivilegesRequired=admin", inno);
        Assert.Contains(@"DefaultDirName={autopf}\{#MyAppName}", inno);
        Assert.DoesNotContain("PrivilegesRequired=lowest", inno);
        Assert.DoesNotContain("PrivilegesRequiredOverridesAllowed", inno);
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

    [Fact]
    public void InstallerPush_DelegatesToCanonicalReviewedReleaseWorkflow()
    {
        string root = FindRepoRoot();
        string buildAll = File.ReadAllText(Path.Combine(root, "build-all-installers.ps1"));
        string buildInstaller = File.ReadAllText(Path.Combine(root, "build-installer.ps1"));
        string gitHelper = File.ReadAllText(Path.Combine(root, "scripts", "installer-git-commits.ps1"));

        Assert.Contains("Invoke-YaguFocusedPendingCommits -RepoRoot $repoRoot -CopilotExecutable $copilotCli", buildAll);
        Assert.Contains("Invoke-YaguInstallerReleaseCommit -RepoRoot $repoRoot", buildAll);
        Assert.DoesNotContain("& git -C $repoRoot add -A", buildAll);

        Assert.True(
            buildAll.IndexOf("Invoke-YaguFocusedPendingCommits", StringComparison.Ordinal)
                < buildAll.IndexOf("$versionFile =", StringComparison.Ordinal),
            "Pending changes must be organized before the release version is incremented.");
        Assert.Contains("$canonicalReleaseScript = Join-Path $repoRoot 'build-all-installers.ps1'", buildInstaller);
        Assert.Contains("& $canonicalReleaseScript @releaseParams", buildInstaller);
        Assert.Contains("if ($SkipBuild) { $releaseParams['SkipBuild'] = $true }", buildInstaller);
        Assert.Contains("if ($SkipVersionIncrement) { $releaseParams['KeepVersion'] = $true }", buildInstaller);
        Assert.Contains("if ($SkipRelease) { $releaseParams['SkipRelease'] = $true }", buildInstaller);
        Assert.Contains("if (-not [string]::IsNullOrWhiteSpace($CopilotPath)) { $releaseParams['CopilotPath'] = $CopilotPath }", buildInstaller);
        Assert.Contains("@('x64-offline')", buildInstaller);
        Assert.True(
            buildInstaller.IndexOf("if ($Push)", StringComparison.Ordinal)
                < buildInstaller.IndexOf("$prereqHelper =", StringComparison.Ordinal),
            "Single-installer publishing must delegate before loading build prerequisites or mutating files.");
        Assert.DoesNotContain("Invoke-YaguFocusedPendingCommits", buildInstaller);
        Assert.DoesNotContain("Invoke-YaguInstallerReleaseCommit", buildInstaller);
        Assert.DoesNotContain("gh release", buildInstaller);
        Assert.DoesNotContain("--generate-notes", buildInstaller);

        Assert.Contains("function ConvertFrom-YaguCopilotCommitPlan", gitHelper);
        Assert.Contains("function Assert-YaguAtomicCommitPlan", gitHelper);
        Assert.Contains("function New-YaguCopilotAtomicCommitPlan", gitHelper);
        Assert.Contains("function Test-YaguAtomicCommitStaging", gitHelper);
        Assert.Contains("function Invoke-YaguAtomicCommitPlan", gitHelper);
        Assert.Contains("UNTRACKED FILE PREVIEW (READ-ONLY FILE READS, BOUNDED)", gitHelper);
        Assert.Contains("-C $RepoRoot -p $prompt --silent --no-color", gitHelper);
        Assert.Contains("--no-custom-instructions --no-ask-user --disable-builtin-mcps --allow-all-tools", gitHelper);
        Assert.Contains("--deny-tool shell --deny-tool write", gitHelper);
        Assert.DoesNotContain("--prompt-file", gitHelper);
        Assert.DoesNotContain(" --no-ask ", gitHelper);
        Assert.Contains("Apply this whole-file commit plan? [yes/abort]", gitHelper);
        Assert.Contains("if ($choice -cne 'yes')", gitHelper);
        Assert.Contains("$env:GIT_INDEX_FILE = $tempIndex", gitHelper);
        Assert.Contains("<# DEFERRED: Interactive hunk workflow retained for possible future restoration.", gitHelper);
        Assert.DoesNotContain("Interactive hunk selection:", gitHelper);
        Assert.Contains("diff --name-only --diff-filter=U", gitHelper);
        Assert.Contains("diff --name-status --find-renames", gitHelper);
        Assert.Contains("Focused commits are not allowed from a detached HEAD", gitHelper);
        Assert.Contains("'MERGE_HEAD', 'CHERRY_PICK_HEAD', 'REVERT_HEAD', 'rebase-merge', 'rebase-apply'", gitHelper);
        Assert.Contains("[Console]::IsInputRedirected", gitHelper);
        Assert.Contains("Unexpected post-build change(s) will not be committed or pushed", gitHelper);
        Assert.Contains("& git --no-pager -C $RepoRoot add -A -- @releaseChanges", gitHelper);
        Assert.Contains("commit --only -m $Message -- @releaseChanges", gitHelper);
        Assert.Contains("'README.md'", buildAll);

        // WhatIf exits before release-tool preflight so dry-runs never require gh/copilot.
        Assert.True(
            buildAll.IndexOf("if ($WhatIfPreference)", StringComparison.Ordinal)
                < buildAll.IndexOf("if ($Push -and -not $SkipRelease)", StringComparison.Ordinal),
            "WhatIf must return before GitHub/Copilot preflight checks.");

        // Push release preflight now requires both gh and copilot up front.
        Assert.Contains("[string]$CopilotPath", buildAll);
        Assert.Contains("Resolve-CopilotCliPath", buildAll);
        Assert.Contains("Assert-CopilotCliAvailable", buildAll);
        Assert.Contains("& $CopilotCli --version *> $null", buildAll);

        // Release notes are prepared before git push so failures block mutation.
        Assert.True(
            buildAll.IndexOf("$preparedReleaseNotes = Add-ReleaseCompareLink", StringComparison.Ordinal)
                < buildAll.IndexOf("Write-Host \"Pushing (git push)...\"", StringComparison.Ordinal),
            "Prepared release notes must be finalized before git push.");
    }

    [Fact]
    public void InstallerRelease_AlwaysAddsFullChangelogCompareLink()
    {
        string root = FindRepoRoot();
        string buildAll = File.ReadAllText(Path.Combine(root, "build-all-installers.ps1"));

        Assert.Contains("function Add-ReleaseCompareLink", buildAll);
        Assert.Contains("## Full changelog", buildAll);
        Assert.Contains("[Compare $range]($compareUrl)", buildAll);
        Assert.Contains("https://github.com/$RepositorySlug/compare/$range", buildAll);
        Assert.Contains("function Get-PreviousGitHubReleaseTag", buildAll);
        Assert.Contains("Where-Object { -not $_.isDraft -and $_.tagName -ne $CurrentTag }", buildAll);

        // Release notes are Copilot-generated from bounded local context, normalized, then deterministically augmented.
        Assert.Contains("function Get-ReleaseChangeContext", buildAll);
        Assert.Contains("git --no-pager -C $RepoRoot log --no-merges '--pretty=format:%h|%s|%b' $range", buildAll);
        Assert.Contains("'diff', '--no-color', '--minimal', $range, '--'", buildAll);
        Assert.Contains("src/Yagu/HELP.html", buildAll);
        Assert.Contains("src/Yagu/Properties/AppInfo.g.cs", buildAll);
        Assert.Contains("src/Yagu/Properties/build-version.txt", buildAll);
        Assert.Contains("function Normalize-CopilotReleaseNotes", buildAll);
        Assert.Contains("Copilot release notes must start with '## What's changed'.", buildAll);
        Assert.Contains("function New-CopilotReleaseNotes", buildAll);
        Assert.Contains("--deny-tool', 'shell'", buildAll);
        Assert.Contains("--deny-tool', 'write'", buildAll);
        Assert.Contains("--no-custom-instructions", buildAll);
        Assert.Contains("--no-ask-user", buildAll);
        Assert.Contains("'-p', $prompt", buildAll);
        Assert.Contains("yagu-release-context-", buildAll);
        Assert.Contains("& $CopilotCli @args > $stdoutPath 2> $stderrPath", buildAll);
        Assert.Contains("ReadAllText($stdoutPath).Trim()", buildAll);
        Assert.Contains("Remove-Item -LiteralPath $contextPath, $stdoutPath, $stderrPath", buildAll);
        Assert.DoesNotContain("--prompt-file", buildAll);
        Assert.Contains("function Add-DeterministicReleaseSections", buildAll);
        Assert.Contains("## Assets", buildAll);
        Assert.Contains("## Validation", buildAll);
        Assert.Contains("## Installation", buildAll);
        Assert.Contains("SHA-256", buildAll);
        Assert.Contains("freshness-checked and size-checked", buildAll);

        // Existing releases are refreshed with prepared notes; new releases are created with the same prepared notes.
        Assert.Contains("--notes-file", buildAll);
        Assert.Contains("if ($releaseExists)", buildAll);
        Assert.Contains("Get-RemoteReleaseTagTarget -RepoRoot $repoRoot -Tag $tag", buildAll);
        Assert.Contains("release upload $tag @($releaseAssets.FullName) --clobber", buildAll);
        Assert.Contains("Assert-GitHubReleaseMatchesBuild", buildAll);
        Assert.Contains("release notes are missing required heading", buildAll);

        // GitHub generated-notes API must not be used.
        Assert.DoesNotContain("releases/generate-notes", buildAll);
        Assert.DoesNotContain("Get-GitHubGeneratedReleaseNotes", buildAll);

        // Full changelog is appended exactly once by Add-ReleaseCompareLink.
        Assert.Contains("if ($notesWithLink.Contains($compareUrl)) { return $notesWithLink }", buildAll);
    }

    [Fact]
    public void InstallerReleaseScripts_UseExplicitGitNoPagerEverywhere()
    {
        string root = FindRepoRoot();
        string buildAll = File.ReadAllText(Path.Combine(root, "build-all-installers.ps1"));
        string helper = File.ReadAllText(Path.Combine(root, "scripts", "installer-git-commits.ps1"));

        string stripDeferred = @"<#[\s\S]*?#>";
        string buildAllActive = System.Text.RegularExpressions.Regex.Replace(buildAll, stripDeferred, string.Empty);
        string helperActive = System.Text.RegularExpressions.Regex.Replace(helper, stripDeferred, string.Empty);

        Assert.DoesNotMatch(@"(?m)(?<![#\w-])&?\s*git\s+-C\s+", buildAllActive);
        Assert.DoesNotMatch(@"(?m)(?<![#\w-])&?\s*git\s+-C\s+", helperActive);
        Assert.DoesNotMatch(@"(?m)&\s*git\s+@(?![^\r\n]*--no-pager)", buildAllActive);
        Assert.DoesNotMatch(@"(?m)&\s*git\s+@(?![^\r\n]*--no-pager)", helperActive);

        Assert.Contains("git --no-pager -C $RepoRoot diff --stat", buildAll);
        Assert.Contains("git --no-pager -C $repoRoot push", buildAll);
        Assert.Contains("git --no-pager -C $RepoRoot log", buildAll);
        Assert.Contains("git --no-pager -C $RepoRoot commit", helper);
        Assert.Contains("git --no-pager -C $repoRoot push", buildAll);
    }

    [Fact]
    public void Installer_OffersOptionalAddToSystemPathTask()
    {
        string root = FindRepoRoot();
        string inno = File.ReadAllText(Path.Combine(root, "installer", "yagu-installer.iss"));

        // An opt-in (unchecked) task on the "Select Additional Tasks" page asks the user whether to add
        // the install folder to the system PATH.
        Assert.Contains(@"Name: ""addtopath""; Description: ""Add Yagu to the system PATH (run 'yagu' from any terminal)""; GroupDescription: ""Command-line access:""; Flags: unchecked", inno);

        // Editing the system Path env var requires broadcasting WM_SETTINGCHANGE so open apps see it.
        Assert.Contains("ChangesEnvironment=yes", inno);

        // The append is gated on the task AND a duplicate guard (NeedsAddPath), writing the system
        // (HKLM) Path as REG_EXPAND_SZ.
        Assert.Contains(@"Root: HKLM; Subkey: ""{#EnvironmentKey}""; ValueType: expandsz; ValueName: ""Path""; ValueData: ""{olddata};{app}""; Tasks: addtopath; Check: NeedsAddPath('{app}')", inno);
        Assert.Contains("function NeedsAddPath(Param: String): Boolean;", inno);

        // Uninstall removes the entry it added (an appended registry value cannot be reversed declaratively).
        Assert.Contains("procedure RemoveAppFromSystemPath();", inno);
        Assert.Contains("RegWriteExpandStringValue(HKLM, '{#EnvironmentKey}', 'Path', NewPath);", inno);
        Assert.Contains("RemoveAppFromSystemPath();", inno);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Yagu.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }
}