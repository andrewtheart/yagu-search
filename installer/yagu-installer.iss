; Yagu Installer — Inno Setup Script
; Requires Inno Setup 6+ (https://jrsoftware.org/isinfo.php)
;
; Before compiling this script, build the app and run build-installer.ps1
; which populates the staging directory referenced below.

#define MyAppName "Yagu"
#define MyAppExeName "Yagu.exe"
#define MyAppPublisher "Yagu"
#define MyAppURL "https://github.com/yagu"
#define DotNet10WingetPackageId "Microsoft.DotNet.SDK.10"
#define DotNet10WingetCommandDisplayName "winget install Microsoft.DotNet.SDK.10"
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
  DotNetProgressPage: TOutputProgressWizardPage;
  DotNetRuntimeNeedsRestart: Boolean;

procedure InitializeWizard;
begin
  DotNetProgressPage := CreateOutputProgressPage(
    'Installing .NET 10',
    'Setup is using winget to install Microsoft .NET 10.');
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

function QuotePowerShellString(Value: String): String;
var
  Escaped: String;
begin
  Escaped := Value;
  StringChangeEx(Escaped, '''', '''''', True);
  Result := '''' + Escaped + '''';
end;

function LoadTrimmedStringFromFile(FileName: String; var Value: String): Boolean;
var
  RawValue: AnsiString;
begin
  Result := LoadStringFromFile(FileName, RawValue);
  if Result then
  begin
    Value := RawValue;
    Value := Trim(Value);
    StringChangeEx(Value, #13, ' ', True);
    StringChangeEx(Value, #10, ' ', True);
    if Length(Value) > 160 then
      Value := Copy(Value, Length(Value) - 159, 160);
  end;
end;

function WriteDotNetWingetRunnerScript(ScriptPath, StatusPath, ExitPath, LogPath: String): Boolean;
var
  Script: String;
begin
  Script :=
    '$ErrorActionPreference = ''Continue''' + #13#10 +
    '$statusFile = ' + QuotePowerShellString(StatusPath) + #13#10 +
    '$exitFile = ' + QuotePowerShellString(ExitPath) + #13#10 +
    '$logFile = ' + QuotePowerShellString(LogPath) + #13#10 +
    'function Write-Status([string]$message) {' + #13#10 +
    '  if ([string]::IsNullOrWhiteSpace($message)) { return }' + #13#10 +
    '  $clean = [regex]::Replace($message, "$([char]27)\[[0-9;?]*[ -/]*[@-~]", "")' + #13#10 +
    '  $clean = ($clean -replace "[`r`n]+", " " -replace "\s{2,}", " ").Trim()' + #13#10 +
    '  if ($clean.Length -eq 0) { return }' + #13#10 +
    '  Add-Content -LiteralPath $logFile -Value $clean -Encoding UTF8' + #13#10 +
    '  Set-Content -LiteralPath $statusFile -Value $clean -Encoding UTF8' + #13#10 +
    '}' + #13#10 +
    '$exitCode = 1' + #13#10 +
    'try {' + #13#10 +
    '  Write-Status "Checking for winget..."' + #13#10 +
    '  $wingetCommand = Get-Command winget.exe -ErrorAction SilentlyContinue' + #13#10 +
    '  if ($null -eq $wingetCommand) { $wingetCommand = Get-Command winget -ErrorAction SilentlyContinue }' + #13#10 +
    '  if ($null -eq $wingetCommand) {' + #13#10 +
    '    Write-Status "winget was not found. Install App Installer from Microsoft Store, then run setup again."' + #13#10 +
    '    $exitCode = 9009' + #13#10 +
    '  } else {' + #13#10 +
    '    $env:WINGET_DISABLE_INTERACTIVITY = "1"' + #13#10 +
    '    Write-Status "Running {#DotNet10WingetCommandDisplayName}..."' + #13#10 +
    '    & $wingetCommand.Source install {#DotNet10WingetPackageId} --silent --accept-package-agreements --accept-source-agreements --disable-interactivity 2>&1 | ForEach-Object {' + #13#10 +
    '      Write-Status ($_ | Out-String)' + #13#10 +
    '    }' + #13#10 +
    '    if ($null -ne $global:LASTEXITCODE) { $exitCode = [int]$global:LASTEXITCODE } else { $exitCode = 0 }' + #13#10 +
    '    Write-Status ("winget finished with exit code {0}." -f $exitCode)' + #13#10 +
    '  }' + #13#10 +
    '} catch {' + #13#10 +
    '  Write-Status ("winget install failed: {0}" -f $_.Exception.Message)' + #13#10 +
    '  $exitCode = 1' + #13#10 +
    '} finally {' + #13#10 +
    '  Set-Content -LiteralPath $exitFile -Value $exitCode -Encoding UTF8' + #13#10 +
    '}' + #13#10 +
    'exit $exitCode' + #13#10;

  Result := SaveStringToFile(ScriptPath, Script, False);
end;

procedure UpdateDotNetWingetProgress(StatusPath: String; var ProgressValue: Integer);
var
  Status: String;
begin
  if not LoadTrimmedStringFromFile(StatusPath, Status) or (Status = '') then
    Status := 'Waiting for winget output...';

  DotNetProgressPage.SetText(Status, 'This can take several minutes. Please leave setup open.');

  ProgressValue := ProgressValue + 2;
  if ProgressValue > 95 then
    ProgressValue := 10;

  DotNetProgressPage.SetProgress(ProgressValue, 100);
  WizardForm.Refresh;
end;

function ReadWingetExitCode(ExitPath: String; var ExitCode: Integer): Boolean;
var
  ExitText: String;
begin
  Result := LoadTrimmedStringFromFile(ExitPath, ExitText);
  if Result then
    ExitCode := StrToIntDef(ExitText, -1);
end;

function InstallDotNet10RuntimeWithWinget(): Boolean;
var
  ResultCode: Integer;
  ExitCode: Integer;
  ScriptPath: String;
  StatusPath: String;
  ExitPath: String;
  LogPath: String;
  Params: String;
  ProgressValue: Integer;
  WaitIterations: Integer;
begin
  Result := False;
  ScriptPath := ExpandConstant('{tmp}\yagu-install-dotnet10-winget.ps1');
  StatusPath := ExpandConstant('{tmp}\yagu-install-dotnet10-winget.status.txt');
  ExitPath := ExpandConstant('{tmp}\yagu-install-dotnet10-winget.exit.txt');
  LogPath := ExpandConstant('{tmp}\yagu-install-dotnet10-winget.log.txt');

  DeleteFile(StatusPath);
  DeleteFile(ExitPath);
  DeleteFile(LogPath);

  if not WriteDotNetWingetRunnerScript(ScriptPath, StatusPath, ExitPath, LogPath) then
  begin
    MsgBox('Could not create the .NET 10 winget installer helper script.', mbError, MB_OK);
    exit;
  end;

  ProgressValue := 5;
  DotNetProgressPage.SetProgress(ProgressValue, 100);
  DotNetProgressPage.SetText('Starting {#DotNet10WingetCommandDisplayName}...', 'This can take several minutes. Please leave setup open.');
  DotNetProgressPage.Show;

  try
    WizardForm.StatusLabel.Caption := 'Installing .NET 10 with winget...';
    Params := '-NoProfile -ExecutionPolicy Bypass -File "' + ScriptPath + '"';
    if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Params, '', SW_HIDE, ewNoWait, ResultCode) then
    begin
      MsgBox('Could not start winget to install .NET 10.', mbError, MB_OK);
      exit;
    end;

    WaitIterations := 0;
    while not FileExists(ExitPath) and (WaitIterations < 14400) do
    begin
      UpdateDotNetWingetProgress(StatusPath, ProgressValue);
      Sleep(250);
      WaitIterations := WaitIterations + 1;
    end;

    if not FileExists(ExitPath) then
    begin
      MsgBox('winget did not finish within one hour. Setup cannot continue.' + #13#10 + #13#10 + 'Log: ' + LogPath, mbError, MB_OK);
      exit;
    end;

    UpdateDotNetWingetProgress(StatusPath, ProgressValue);
    DotNetProgressPage.SetProgress(100, 100);

    if not ReadWingetExitCode(ExitPath, ExitCode) then
    begin
      MsgBox('winget finished, but setup could not read its exit code.' + #13#10 + 'Log: ' + LogPath, mbError, MB_OK);
      exit;
    end;
  finally
    DotNetProgressPage.Hide;
  end;

  if (ExitCode <> 0) and (ExitCode <> 3010) then
  begin
    MsgBox('winget failed to install .NET 10. Exit code: ' + IntToStr(ExitCode) + '.' + #13#10 + #13#10 + 'Log: ' + LogPath, mbError, MB_OK);
    exit;
  end;

  DotNetRuntimeNeedsRestart := ExitCode = 3010;
  Result := IsDotNet10RuntimeInstalled();
  if not Result then
  begin
    MsgBox('winget completed, but setup still could not detect a .NET 10 x64 runtime.', mbError, MB_OK);
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
      'Install it now by running {#DotNet10WingetCommandDisplayName}?',
       mbConfirmation,
       MB_YESNO) <> IDYES) then
  begin
    Result := False;
    exit;
  end;

  Result := InstallDotNet10RuntimeWithWinget();
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
