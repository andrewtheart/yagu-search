; Yagu Installer — Inno Setup Script
; Requires Inno Setup 6+ (https://jrsoftware.org/isinfo.php)
;
; Before compiling this script, build the app and run build-installer.ps1
; which populates the staging directory referenced below.

#define MyAppName "Yagu"
#define MyAppExeName "Yagu.exe"
#define MyAppPublisher "Yagu"
#define MyAppURL "https://github.com/yagu"

; Target CPU architecture for this installer. build-installer.ps1 passes
; /DYaguArch=x64|x86|arm64 when compiling each per-architecture installer.
; Yagu ships as a self-contained Native AOT build, so the target machine needs
; no .NET runtime — only the Windows App Runtime (bundled with the installer).
#ifndef YaguArch
  #define YaguArch "x64"
#endif

; Version is read from the build-version.txt file produced by the build.
; Override on the ISCC command line with /DMyAppVersion=x.y.z if needed.
#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif

; Source directory containing the built app files (populated by build-installer.ps1)
#ifndef StagingDir
  #define StagingDir "..\installer\staging"
#endif

; When IncludeOcr is defined to "1", build-installer.ps1 has staged the offline OCR
; payload under {#StagingDir}\ocr-payload (native PaddleOCR runtime + PP-OCR models,
; plus the Tesseract English data, which is the default engine for this edition) AND the
; voidtools Everything setup under {#StagingDir}\everything-setup (bundled so the app can
; install Everything after in-app consent, with no download). Both folders are shipped
; automatically by the recursesubdirs [Files] entry; this define only changes the output
; filename so the offline edition does not collide with the lite edition.
#ifndef IncludeOcr
  #define IncludeOcr "0"
#endif

[Setup]
AppId={{8F4E2B5A-3C7D-4A1E-B9F6-2D8E5A7C3F1B}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=..\installer\output
#if IncludeOcr == "1"
OutputBaseFilename=YaguSetup-{#MyAppVersion}-{#YaguArch}-offline
#else
OutputBaseFilename=YaguSetup-{#MyAppVersion}-{#YaguArch}
#endif
SetupIconFile=..\src\Yagu\Assets\yagu.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
#if YaguArch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#elif YaguArch == "x86"
ArchitecturesAllowed=x86compatible
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "contextmenu"; Description: "Add 'Search with Yagu' to Explorer context menu"; GroupDescription: "Windows Explorer integration:"

[Files]
Source: "{#StagingDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; Store install directory for the app to discover at runtime
Root: HKCU; Subkey: "Software\Yagu"; ValueType: string; ValueName: "InstallDir"; ValueData: "{app}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Yagu"; ValueType: string; ValueName: "DisplayName"; ValueData: "Yagu"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Yagu"; ValueType: string; ValueName: "ExecutablePath"; ValueData: "{app}\{#MyAppExeName}"; Flags: uninsdeletekey

; Explorer context menu — Directory
Root: HKCU; Subkey: "Software\Classes\Directory\shell\Yagu"; ValueType: string; ValueName: ""; ValueData: "Search with Yagu"; Tasks: contextmenu; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Directory\shell\Yagu"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\{#MyAppExeName}"""; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\Directory\shell\Yagu\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Tasks: contextmenu

; Explorer context menu — Directory Background
Root: HKCU; Subkey: "Software\Classes\Directory\Background\shell\Yagu"; ValueType: string; ValueName: ""; ValueData: "Search with Yagu"; Tasks: contextmenu; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Directory\Background\shell\Yagu"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\{#MyAppExeName}"""; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\Directory\Background\shell\Yagu\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%V"""; Tasks: contextmenu

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
{ Yagu ships as a self-contained Native AOT build (one installer per CPU
  architecture: x64, x86, arm64), so the target machine needs no .NET runtime.
  The only prerequisite is the Windows App Runtime, which is bundled under the
  app's Prerequisites folder and installed at the post-install step. }

{ True when Smart App Control (SAC) is turned on AND in *Enforce* mode. SAC in Enforce mode blocks
  binaries that are not signed by a recognized publisher / lacking good cloud reputation from
  running at all. Yagu's per-machine build is unsigned, so installing under SAC Enforce would
  produce an app that Windows blocks the moment it launches (and would also block the bundled
  prerequisites). SAC publishes its mode as the DWORD VerifiedAndReputablePolicyState under
  HKLM\SYSTEM\CurrentControlSet\Control\CI\Policy:
    0 = Off, 1 = Enforce (blocks), 2 = Evaluation (observe only, does not block).
  Only state 1 actually blocks, so Off and Evaluation are allowed to proceed. The CI\Policy key
  lives in the (non-redirected) SYSTEM hive; we still read the 64-bit view explicitly so the x86
  installer reports correctly on 64-bit Windows. }
function SmartAppControlEnforced(): Boolean;
var
  State: Cardinal;
  RootKey: Integer;
begin
  Result := False;
  if IsWin64 then
    RootKey := HKLM64
  else
    RootKey := HKLM;
  if RegQueryDWordValue(RootKey, 'SYSTEM\CurrentControlSet\Control\CI\Policy', 'VerifiedAndReputablePolicyState', State) then
    Result := (State = 1);
end;

{ Runs before the wizard is shown. Abort the install when Smart App Control is enforcing, because
  the unsigned per-machine build cannot run on such a machine. Returning False here cancels setup
  without copying any files. }
function InitializeSetup(): Boolean;
begin
  Result := True;
  if SmartAppControlEnforced() then
  begin
    Result := False;
    if not WizardSilent() then
      MsgBox(
        'Smart App Control is turned on (Enforce mode) on this PC.' + #13#10#13#10 +
        'Smart App Control blocks apps that are not signed by a recognized publisher, which would ' +
        'prevent Yagu from running after it is installed. Setup will now stop.' + #13#10#13#10 +
        'To install Yagu, turn Smart App Control off in Windows Security > App & browser control > ' +
        'Smart App Control settings, then run this installer again.' + #13#10#13#10 +
        'Yagu is not code-signed because SignPath is not currently accepting applications for ' +
        'open-source projects. Signing is planned for a future release, which will let Yagu run ' +
        'with Smart App Control enabled.',
        mbCriticalError, MB_OK);
  end;
end;

function InstallWindowsAppRuntime(): Boolean;
var
  ResultCode: Integer;
  RuntimeScript: String;
  Params: String;
begin
  RuntimeScript := ExpandConstant('{app}\Prerequisites\WindowsAppRuntime\Install-WindowsAppRuntime.ps1');
  if not FileExists(RuntimeScript) then
  begin
    MsgBox('Windows App Runtime prerequisite was not packaged:' + #13#10 + RuntimeScript, mbError, MB_OK);
    Result := False;
    exit;
  end;

  WizardForm.StatusLabel.Caption := 'Installing Windows App Runtime 1.8...';
  Params := '-NoProfile -ExecutionPolicy Bypass -File "' + RuntimeScript + '"';
  Result := Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if Result and (ResultCode <> 0) then
  begin
    MsgBox('Windows App Runtime prerequisite installation failed with exit code ' + IntToStr(ResultCode) + '.', mbError, MB_OK);
    Result := False;
  end;
end;

{ True when the installer was launched with the /VERBOSELOG switch. This forces Yagu to log
  verbosely from its VERY FIRST launch (the in-app log-level setting is unreachable then because
  startup modals block the settings UI). Works with silent installs too, e.g.:
      YaguSetup-x.y.z-x64.exe /VERYSILENT /VERBOSELOG }
function VerboseLoggingRequested(): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), '/VERBOSELOG') = 0 then
    begin
      Result := True;
      Exit;
    end;
end;

{ Publish (or clear) the install-time log-level override that Yagu reads at startup from
  HKCU\Software\Yagu\LogLevelOverride. With /VERBOSELOG the first run logs verbosely to yagu.log;
  a normal install clears any stale override left by a previous verbose install so logging reverts
  to the saved setting. The Software\Yagu key is removed at uninstall (CurUninstallStepChanged). }
procedure ApplyLogLevelOverride();
begin
  if VerboseLoggingRequested() then
    RegWriteStringValue(HKCU, 'Software\Yagu', 'LogLevelOverride', 'Verbose')
  else
    RegDeleteValue(HKCU, 'Software\Yagu', 'LogLevelOverride');
end;

{ True when the Microsoft Edge WebView2 Evergreen Runtime is already installed. It registers its
  version ("pv") under the EdgeUpdate client GUID F3017226-FE2A-4295-8BDF-00C3A9A7E4C5: per-machine
  in WOW6432Node (EdgeUpdate is 32-bit) or the native view, or per-user under HKCU. }
function WebView2RuntimeInstalled(): Boolean;
var
  pv: String;
begin
  Result :=
    (RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', pv) and (pv <> '') and (pv <> '0.0.0.0')) or
    (RegQueryStringValue(HKLM, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', pv) and (pv <> '') and (pv <> '0.0.0.0')) or
    (RegQueryStringValue(HKCU, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', pv) and (pv <> '') and (pv <> '0.0.0.0'));
end;

{ Installs the WebView2 Evergreen Runtime (needed only by the embedded terminal) unless it is already
  present. Prefers the FULL offline Standalone Installer (bundled by the offline edition -- installs
  with no internet), falling back to the online bootstrapper (lite editions). BEST-EFFORT: the terminal
  is optional, so a failure never aborts Yagu's install -- the app shows an in-terminal install prompt. }
procedure InstallWebView2Runtime();
var
  ResultCode: Integer;
  Installer: String;
begin
  if WebView2RuntimeInstalled() then
    exit;

  { Prefer the offline Standalone Installer; fall back to the online bootstrapper. Both accept
    /silent /install. }
  Installer := ExpandConstant('{app}\Prerequisites\WebView2\MicrosoftEdgeWebView2RuntimeInstallerX64.exe');
  if not FileExists(Installer) then
    Installer := ExpandConstant('{app}\Prerequisites\WebView2\MicrosoftEdgeWebView2Setup.exe');
  if not FileExists(Installer) then
    exit;

  WizardForm.StatusLabel.Caption := 'Installing Microsoft Edge WebView2 Runtime (for the embedded terminal)...';
  Exec(Installer, '/silent /install', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    if not InstallWindowsAppRuntime() then
      Abort;
    ApplyLogLevelOverride();
    InstallWebView2Runtime();
  end;
end;

{ Yagu keeps its settings in %APPDATA%\Yagu\settings.json (separate from the installed program
  files in the app install folder). Uninstall does NOT remove per-user app data by default, so on an
  interactive uninstall we ask whether to keep it. The default is to KEEP: a silent/automated
  uninstall never prompts and never deletes settings, and only an explicit "No" removes the file.
  Logs and any other files in the folder are left untouched -- we delete only settings.json, then
  remove the Yagu app-data folder if (and only if) it is now empty. }
procedure MaybeRemoveUserSettings();
var
  SettingsFile: String;
begin
  if UninstallSilent() then
    exit;

  SettingsFile := ExpandConstant('{userappdata}\Yagu\settings.json');
  if not FileExists(SettingsFile) then
    exit;

  if MsgBox(
       'Do you want to keep your Yagu settings and preferences?' + #13#10#13#10 +
       'Choose Yes to keep them (useful if you plan to reinstall Yagu later), or No to permanently ' +
       'delete your settings file:' + #13#10 + SettingsFile,
       mbConfirmation, MB_YESNO) = IDNO then
  begin
    DeleteFile(SettingsFile);
    { Best-effort tidy-up: removes the folder only when empty, so logs/other data are preserved. }
    RemoveDir(ExpandConstant('{userappdata}\Yagu'));
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    // Clean up context menu entries that might remain
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\Directory\shell\Yagu');
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\Directory\Background\shell\Yagu');
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Yagu');

    // Offer to remove the user's settings file (kept by default).
    MaybeRemoveUserSettings();
  end;
end;
