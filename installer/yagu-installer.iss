; Yagu Installer — Inno Setup Script
; Requires Inno Setup 6+ (https://jrsoftware.org/isinfo.php)
;
; Before compiling this script, build the app and run build-installer.ps1
; which populates the staging directory referenced below.

#define MyAppName "Yagu"
#define MyAppExeName "Yagu.exe"
#define MyAppPublisher "Yagu"
#define MyAppURL "https://github.com/yagu"
#define DotNet10RuntimeDisplayName ".NET 10.0 Runtime (v10.0.9) - Windows x64 Installer"
#define DotNet10RuntimeInstallerName "dotnet-runtime-10.0.9-win-x64.exe"
#define DotNet10RuntimeDownloadUrl "https://builds.dotnet.microsoft.com/dotnet/Runtime/10.0.9/dotnet-runtime-10.0.9-win-x64.exe"
#define DotNet10RuntimeRegistrySubkey "SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.NETCore.App"

; Version is read from the build-version.txt file produced by the build.
; Override on the ISCC command line with /DMyAppVersion=x.y.z if needed.
#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif

; Source directory containing the built app files (populated by build-installer.ps1)
#ifndef StagingDir
  #define StagingDir "..\installer\staging"
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
OutputBaseFilename=YaguSetup-{#MyAppVersion}
SetupIconFile=..\Yagu\Assets\yagu.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
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
var
  DownloadPage: TDownloadWizardPage;
  DotNetRuntimeNeedsRestart: Boolean;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage(
    'Downloading .NET 10 Runtime',
    'Setup is downloading the required Microsoft .NET 10 Runtime.',
    nil);
  DownloadPage.ShowBaseNameInsteadOfUrl := True;
end;

function IsDotNet10RuntimeInstalled(): Boolean;
var
  RuntimeVersions: TArrayOfString;
  I: Integer;
begin
  Result := False;

  if not RegGetValueNames(HKLM64, '{#DotNet10RuntimeRegistrySubkey}', RuntimeVersions) then
  begin
    if not RegGetValueNames(HKLM32, '{#DotNet10RuntimeRegistrySubkey}', RuntimeVersions) then
      exit;
  end;

  for I := 0 to GetArrayLength(RuntimeVersions) - 1 do
  begin
    if Copy(RuntimeVersions[I], 1, 3) = '10.' then
    begin
      Result := True;
      exit;
    end;
  end;
end;

function DownloadDotNet10RuntimeInstaller(): Boolean;
var
  Error: String;
begin
  Result := False;
  DownloadPage.Clear;
  DownloadPage.Add('{#DotNet10RuntimeDownloadUrl}', '{#DotNet10RuntimeInstallerName}', '');
  DownloadPage.Show;

  try
    try
      DownloadPage.Download;
      Result := True;
    except
      if DownloadPage.AbortedByUser then
        Log('.NET 10 Runtime download aborted by user.')
      else
      begin
        Error := Format('%s: %s', [DownloadPage.LastBaseNameOrUrl, GetExceptionMessage]);
        SuppressibleMsgBox(AddPeriod(Error), mbCriticalError, MB_OK, IDOK);
      end;
    end;
  finally
    DownloadPage.Hide;
  end;
end;

function InstallDotNet10Runtime(): Boolean;
var
  ResultCode: Integer;
  InstallerPath: String;
  Params: String;
begin
  InstallerPath := ExpandConstant('{tmp}\{#DotNet10RuntimeInstallerName}');
  if not FileExists(InstallerPath) then
  begin
    MsgBox('The .NET 10 Runtime installer was not downloaded:' + #13#10 + InstallerPath, mbError, MB_OK);
    Result := False;
    exit;
  end;

  WizardForm.StatusLabel.Caption := 'Installing .NET 10 Runtime...';
  Params := '/install /passive /norestart';
  Result := Exec(InstallerPath, Params, '', SW_SHOW, ewWaitUntilTerminated, ResultCode);
  if not Result then
  begin
    MsgBox('Could not start the .NET 10 Runtime installer.', mbError, MB_OK);
    exit;
  end;

  if (ResultCode <> 0) and (ResultCode <> 3010) then
  begin
    MsgBox('.NET 10 Runtime installation failed with exit code ' + IntToStr(ResultCode) + '.', mbError, MB_OK);
    Result := False;
    exit;
  end;

  DotNetRuntimeNeedsRestart := ResultCode = 3010;
  Result := IsDotNet10RuntimeInstalled();
  if not Result then
  begin
    MsgBox('The .NET 10 Runtime installer completed, but setup still could not detect a .NET 10 x64 runtime.', mbError, MB_OK);
  end;
end;

function EnsureDotNet10RuntimeInstalled(): Boolean;
begin
  if IsDotNet10RuntimeInstalled() then
  begin
    Result := True;
    exit;
  end;

  if (not WizardSilent()) and
     (MsgBox(
       'Yagu requires the .NET 10.0 Runtime for Windows x64.' + #13#10 + #13#10 +
       'Setup did not find a .NET 10 runtime on this computer.' + #13#10 + #13#10 +
       'Download and install {#DotNet10RuntimeDisplayName} now?',
       mbConfirmation,
       MB_YESNO) <> IDYES) then
  begin
    Result := False;
    exit;
  end;

  Result := DownloadDotNet10RuntimeInstaller() and InstallDotNet10Runtime();
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  if CurPageID = wpReady then
    Result := EnsureDotNet10RuntimeInstalled()
  else
    Result := True;
end;

function NeedRestart(): Boolean;
begin
  Result := DotNetRuntimeNeedsRestart;
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

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    if not InstallWindowsAppRuntime() then
      Abort;
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
  end;
end;
