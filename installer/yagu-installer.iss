; Yagu Installer — Inno Setup Script
; Requires Inno Setup 6+ (https://jrsoftware.org/isinfo.php)
;
; Before compiling this script, build the app and run build-installer.ps1
; which populates the staging directory referenced below.

#define MyAppName "Yagu"
#define MyAppExeName "Yagu.exe"
#define MyAppPublisher "Yagu"
#define MyAppURL "https://github.com/yagu"
#define DotNet10RuntimeDownloadUrl "https://dotnet.microsoft.com/download/dotnet/10.0"
#define DotNet10RuntimeDirectUrl "https://aka.ms/dotnet/10.0/dotnet-runtime-win-x64.exe"
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

function ShellExecUrl(const Url: String): Boolean;
var
  ErrorCode: Integer;
begin
  Result := ShellExec('open', Url, '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
end;

function DownloadAndInstallDotNetRuntime(): Boolean;
var
  TempDir, TempFile, DoneFlag: String;
  ResultCode: Integer;
  DownloadCmd: String;
  ProgressForm: TSetupForm;
  ProgressLabel: TNewStaticText;
  ProgressBar: TNewProgressBar;
  WaitCount: Integer;
  FileSize: Int64;
  Pct: Integer;
  ExpectedSize: Int64;
begin
  Result := False;
  TempDir := GetTempDir;
  TempFile := TempDir + 'dotnet-runtime-10.0-win-x64.exe';
  DoneFlag := TempFile + '.done';
  ExpectedSize := 30500000; { ~30 MB .NET 10 runtime installer }

  { Delete stale files from previous attempts }
  if FileExists(TempFile) then
    DeleteFile(TempFile);
  if FileExists(DoneFlag) then
    DeleteFile(DoneFlag);

  { Create a progress dialog }
  ProgressForm := CreateCustomForm(ScaleX(420), ScaleY(110), False, False);
  ProgressForm.Caption := 'Downloading .NET 10.0 Runtime';
  ProgressForm.Position := poScreenCenter;

  ProgressLabel := TNewStaticText.Create(ProgressForm);
  ProgressLabel.Parent := ProgressForm;
  ProgressLabel.Left := ScaleX(20);
  ProgressLabel.Top := ScaleY(20);
  ProgressLabel.Caption := 'Downloading .NET 10.0 Runtime... 0%';
  ProgressLabel.AutoSize := True;

  ProgressBar := TNewProgressBar.Create(ProgressForm);
  ProgressBar.Parent := ProgressForm;
  ProgressBar.Left := ScaleX(20);
  ProgressBar.Top := ScaleY(50);
  ProgressBar.Width := ScaleX(380);
  ProgressBar.Height := ScaleY(20);
  ProgressBar.Min := 0;
  ProgressBar.Max := 100;
  ProgressBar.Position := 0;

  ProgressForm.Show();
  ProgressForm.Refresh();

  { Start download in background using streaming .NET WebClient so the file grows on disk }
  DownloadCmd := '/c start /min "" powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command "' +
    '[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12; ' +
    '$wc = New-Object System.Net.WebClient; ' +
    '$wc.DownloadFile(''{#DotNet10RuntimeDirectUrl}'', ''' + TempFile + '''); ' +
    'New-Item -Path ''' + DoneFlag + ''' -ItemType File -Force | Out-Null"';

  if not Exec(ExpandConstant('{cmd}'), DownloadCmd, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    ProgressForm.Close();
    ProgressForm.Free();
    MsgBox('Failed to launch download process.', mbError, MB_OK);
    exit;
  end;

  { Poll for completion, updating progress bar based on file size }
  WaitCount := 0;
  while (not FileExists(DoneFlag)) and (WaitCount < 360) do
  begin
    Sleep(500);
    if FileExists(TempFile) then
    begin
      if FileSize64(TempFile, FileSize) and (FileSize > 0) then
      begin
        Pct := FileSize * 100 div ExpectedSize;
        if Pct > 99 then
          Pct := 99;
        ProgressBar.Position := Pct;
        ProgressLabel.Caption := 'Downloading .NET 10.0 Runtime... ' + IntToStr(Pct) + '%';
      end;
    end;
    ProgressForm.Refresh();
    WaitCount := WaitCount + 1;
  end;

  ProgressBar.Position := 100;
  ProgressLabel.Caption := 'Download complete.';
  ProgressForm.Refresh();

  ProgressForm.Close();
  ProgressForm.Free();

  if not FileExists(TempFile) then
  begin
    MsgBox('Download failed or timed out.' + #13#10 +
      'Please download and install the .NET 10 Runtime manually.', mbError, MB_OK);
    exit;
  end;

  { Clean up done flag }
  DeleteFile(DoneFlag);

  { Run the .NET installer with UI so the user can see progress }
  if not Exec(TempFile, '/install /norestart', '', SW_SHOWNORMAL, ewWaitUntilTerminated, ResultCode) then
  begin
    MsgBox('Failed to launch the .NET Runtime installer.', mbError, MB_OK);
    exit;
  end;

  if ResultCode = 0 then
    Result := True
  else if ResultCode = 3010 then
    Result := True  { Success but reboot required }
  else
    MsgBox('.NET Runtime installer exited with code ' + IntToStr(ResultCode) + '.', mbError, MB_OK);

  { Clean up downloaded file }
  DeleteFile(TempFile);
end;

function InitializeSetup(): Boolean;
var
  Choice: Integer;
  ButtonLabels: TArrayOfString;
begin
  Result := True;
  if not IsDotNet10RuntimeInstalled() then
  begin
    SetArrayLength(ButtonLabels, 3);
    ButtonLabels[0] := 'Download && Install Now';
    ButtonLabels[1] := 'Open Download Page';
    ButtonLabels[2] := 'Cancel';

    Choice := TaskDialogMsgBox(
      'Missing .NET 10.0 Runtime',
      'Yagu requires the .NET 10.0 Runtime for Windows x64.' + #13#10 + #13#10 +
      'The installer did not find a .NET 10 runtime on this computer.' + #13#10 + #13#10 +
      'Choose an option below to install it, or cancel to exit.',
      mbCriticalError,
      MB_YESNOCANCEL, ButtonLabels, 0);

    case Choice of
      IDYES:
        begin
          DownloadAndInstallDotNetRuntime();
          { Re-check after install attempt }
          if IsDotNet10RuntimeInstalled() then
            Result := True
          else
          begin
            MsgBox('.NET 10 Runtime installation did not complete successfully.' + #13#10 +
              'Please install it manually and run this installer again.', mbError, MB_OK);
            Result := False;
          end;
        end;
      IDNO:
        begin
          ShellExecUrl('{#DotNet10RuntimeDownloadUrl}');
          MsgBox('After installing the .NET 10 Runtime, run this installer again.', mbInformation, MB_OK);
          Result := False;
        end;
    else
      Result := False;
    end;
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
