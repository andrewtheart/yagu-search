; Yagu Installer — Inno Setup Script
; Requires Inno Setup 6+ (https://jrsoftware.org/isinfo.php)
;
; Before compiling this script, build the app and run build-installer.ps1
; which populates the staging directory referenced below.

#define MyAppName "Yagu"
#define MyAppExeName "Yagu.exe"
#define MyAppPublisher "Yagu"
#define MyAppURL "https://github.com/yagu"
; Repository root, relative to this script (which lives in installer\).
#define RepoRoot ".."

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

; The system-wide PATH lives under this (non-redirected) HKLM key. Used by both the [Registry]
; append (the "Add Yagu to the system PATH" task) and the [Code] add/remove helpers.
#define EnvironmentKey "SYSTEM\CurrentControlSet\Control\Session Manager\Environment"

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
; Always install per-machine (elevated). Yagu is an unsigned, per-machine desktop app whose layout
; lives under {commonpf}\Yagu (C:\Program Files\Yagu), so an UPDATE must be able to overwrite that
; location. The old non-elevated model (run as the current user by default, with a "for all users /
; just me" mode dialog) put the elevation decision on the user; re-running it over an existing
; per-machine install then silently failed to update Program Files (it either targeted a separate
; per-user copy or copied nothing). Requiring admin makes Setup auto-prompt for elevation (UAC) every
; time, so a real user never has to know to "run as administrator" -- the update just lands in the
; existing C:\Program Files\Yagu install.
PrivilegesRequired=admin
CloseApplications=yes
RestartApplications=no
; The optional "Add Yagu to the system PATH" task modifies the system Path environment variable, so
; broadcast WM_SETTINGCHANGE after install/uninstall to let already-open apps pick up the change.
ChangesEnvironment=yes
; Show the standard GPLv3 agreement page first, followed immediately by a custom page containing the
; consolidated third-party notices. Yagu also shows its privacy policy on an information page during
; setup (InfoBeforeFile, before component/task selection) so the user is told how their data is
; handled before installing.
LicenseFile={#RepoRoot}\LICENSE
InfoBeforeFile={#RepoRoot}\PRIVACY.md

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "contextmenu"; Description: "Add 'Search with Yagu' to Explorer context menu"; GroupDescription: "Windows Explorer integration:"
Name: "addtopath"; Description: "Add Yagu to the system PATH (run 'yagu' from any terminal)"; GroupDescription: "Command-line access:"

[Files]
Source: "{#RepoRoot}\THIRD-PARTY-NOTICES.txt"; Flags: dontcopy noencryption
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

; Optional: append the install folder to the system PATH so "yagu" works from any terminal. Gated on
; the addtopath task and NeedsAddPath() so a re-run/update never adds a duplicate entry. The matching
; removal on uninstall is handled in [Code] (an appended value cannot be reversed declaratively).
Root: HKLM; Subkey: "{#EnvironmentKey}"; ValueType: expandsz; ValueName: "Path"; ValueData: "{olddata};{app}"; Tasks: addtopath; Check: NeedsAddPath('{app}')

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

var
  PreservedCustomIndexRoot: String;
  ThirdPartyNoticesPage: TWizardPage;
  ThirdPartyNoticesViewer: TRichEditViewer;
  ExistingDataPage: TWizardPage;
  ExistingSummaryViewer: TRichEditViewer;
  ExistingContinueCheckBox: TNewCheckBox;
  ExistingSettingsCheckBox: TNewCheckBox;
  ExistingIndexesCheckBox: TNewCheckBox;
  ExistingInstallLocations: String;
  ExistingSettingsFound: Boolean;
  ExistingIndexesFound: Boolean;
  ExistingIndexScopeCount: Integer;
  ExistingIndexLocations: String;
  ExistingCustomIndexRoot: String;

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

  WizardForm.StatusLabel.Caption := 'Installing Windows app runtime (if not installed)...';
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

{ ===== System PATH integration (the "Add Yagu to the system PATH" task) ===== }

{ Check used by the [Registry] Path append: returns True only when the install folder is NOT already
  a whole, delimited entry in the system Path, so re-running the installer (or installing an update)
  never adds a duplicate. Param is the install folder; ExpandConstant is a no-op when Inno has
  already expanded it. }
function NeedsAddPath(Param: String): Boolean;
var
  OrigPath: String;
  AppDir: String;
begin
  if not RegQueryStringValue(HKLM, '{#EnvironmentKey}', 'Path', OrigPath) then
  begin
    Result := True;
    Exit;
  end;
  AppDir := ExpandConstant(Param);
  { Wrap both sides in ';' so a whole entry is matched (case-insensitively), never a substring. }
  Result := Pos(';' + Uppercase(AppDir) + ';', ';' + Uppercase(OrigPath) + ';') = 0;
end;

{ Uninstall counterpart to the Path append: removes the install folder from the system Path (matched
  as a whole, trimmed, case-insensitive entry) if present. Every other entry -- including empties --
  is preserved. Best-effort: a failure to read/write Path never blocks uninstall. }
procedure RemoveAppFromSystemPath();
var
  OrigPath: String;
  NewPath: String;
  AppDir: String;
  Rest: String;
  Token: String;
  SemiPos: Integer;
begin
  if not RegQueryStringValue(HKLM, '{#EnvironmentKey}', 'Path', OrigPath) then
    Exit;
  AppDir := ExpandConstant('{app}');
  NewPath := '';
  Rest := OrigPath;
  while Rest <> '' do
  begin
    SemiPos := Pos(';', Rest);
    if SemiPos > 0 then
    begin
      Token := Copy(Rest, 1, SemiPos - 1);
      Rest := Copy(Rest, SemiPos + 1, Length(Rest));
    end
    else
    begin
      Token := Rest;
      Rest := '';
    end;
    if CompareText(Trim(Token), AppDir) <> 0 then
    begin
      if NewPath <> '' then
        NewPath := NewPath + ';';
      NewPath := NewPath + Token;
    end;
  end;
  if CompareText(NewPath, OrigPath) <> 0 then
    RegWriteExpandStringValue(HKLM, '{#EnvironmentKey}', 'Path', NewPath);
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

{ Force-terminate any running Yagu process -- the main app plus its out-of-process workers -- so their
  executables/DLLs are not locked while setup copies (install) or deletes (uninstall) files. Runs on
  BOTH install (PrepareToInstall, before any files are written) and uninstall (InitializeUninstall).
  Best-effort: taskkill returns a non-zero code when no matching process is running, which is expected
  and ignored. "/T" also terminates child processes (the workers); the workers are killed explicitly
  too in case one was orphaned from its parent. }
procedure KillYaguProcesses();
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /T /IM Yagu.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM Yagu.SemanticWorker.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM Yagu.OcrWorker.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function IsDecimalProcessId(const Value: String): Boolean;
var
  I: Integer;
begin
  Result := (Length(Value) >= 1) and (Length(Value) <= 10);
  if not Result then
    exit;

  for I := 1 to Length(Value) do
  begin
    if (Value[I] < '0') or (Value[I] > '9') then
    begin
      Result := False;
      exit;
    end;
  end;
end;

procedure WaitForLaunchingYagu();
var
  ResultCode: Integer;
  WaitPid: String;
begin
  WaitPid := ExpandConstant('{param:YAGUWAITPID|}');
  if not IsDecimalProcessId(WaitPid) then
    exit;

  { The in-app updater passes its PID after starting this elevated setup process. Give Yagu's
    graceful search/index shutdown time to finish before the existing forced-close fallback runs. }
  Exec(
    ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -Command "Wait-Process -Id ' + WaitPid + ' -Timeout 15 -ErrorAction SilentlyContinue"',
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode);
end;

function IsHexCharacter(Value: Char): Boolean;
begin
  Result := ((Value >= '0') and (Value <= '9')) or
            ((Value >= 'a') and (Value <= 'f')) or
            ((Value >= 'A') and (Value <= 'F'));
end;

function IsIndexScopeDirectoryName(const Value: String): Boolean;
var
  I: Integer;
begin
  Result := Length(Value) = 32;
  if not Result then
    exit;

  for I := 1 to Length(Value) do
  begin
    if not IsHexCharacter(Value[I]) then
    begin
      Result := False;
      exit;
    end;
  end;
end;

function IsRecognizedIndexScope(const ScopeDirectory: String): Boolean;
begin
  Result := IsIndexScopeDirectoryName(ExtractFileName(ScopeDirectory)) and
            (FileExists(ScopeDirectory + '\current.a') or
             FileExists(ScopeDirectory + '\current.b'));
end;

function CountRecognizedIndexScopes(const IndexRoot: String): Integer;
var
  FindRec: TFindRec;
  Candidate: String;
begin
  Result := 0;
  if not DirExists(IndexRoot) then
    exit;

  if FindFirst(IndexRoot + '\*', FindRec) then
  begin
    try
      repeat
        if ((FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0) and
           (FindRec.Name <> '.') and (FindRec.Name <> '..') then
        begin
          Candidate := IndexRoot + '\' + FindRec.Name;
          if IsRecognizedIndexScope(Candidate) then
            Result := Result + 1;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

function HasRecognizedIndexData(const IndexRoot: String): Boolean;
begin
  Result := CountRecognizedIndexScopes(IndexRoot) > 0;
end;

function HexDigitValue(Value: Char): Integer;
begin
  if (Value >= '0') and (Value <= '9') then
    Result := Ord(Value) - Ord('0')
  else if (Value >= 'a') and (Value <= 'f') then
    Result := Ord(Value) - Ord('a') + 10
  else if (Value >= 'A') and (Value <= 'F') then
    Result := Ord(Value) - Ord('A') + 10
  else
    Result := -1;
end;

function TryReadJsonStringProperty(const Json, PropertyName: String; var Value: String): Boolean;
var
  Marker: String;
  Position, JsonLength, I, Digit, CodePoint: Integer;
  Character, Escape: Char;
begin
  Result := False;
  Value := '';
  Marker := '"' + PropertyName + '"';
  Position := Pos(Marker, Json);
  if Position = 0 then
    exit;

  Position := Position + Length(Marker);
  JsonLength := Length(Json);
  while (Position <= JsonLength) and
        ((Json[Position] = ' ') or (Json[Position] = #9) or
         (Json[Position] = #10) or (Json[Position] = #13)) do
    Position := Position + 1;
  if (Position > JsonLength) or (Json[Position] <> ':') then
    exit;

  Position := Position + 1;
  while (Position <= JsonLength) and
        ((Json[Position] = ' ') or (Json[Position] = #9) or
         (Json[Position] = #10) or (Json[Position] = #13)) do
    Position := Position + 1;
  if (Position > JsonLength) or (Json[Position] <> '"') then
    exit;

  Position := Position + 1;
  while Position <= JsonLength do
  begin
    Character := Json[Position];
    Position := Position + 1;
    if Character = '"' then
    begin
      Result := True;
      exit;
    end;

    if Character <> '\' then
    begin
      Value := Value + Character;
      continue;
    end;

    if Position > JsonLength then
      exit;
    Escape := Json[Position];
    Position := Position + 1;
    if (Escape = '"') or (Escape = '\') or (Escape = '/') then
      Value := Value + Escape
    else if Escape = 'b' then
      Value := Value + #8
    else if Escape = 'f' then
      Value := Value + #12
    else if Escape = 'n' then
      Value := Value + #10
    else if Escape = 'r' then
      Value := Value + #13
    else if Escape = 't' then
      Value := Value + #9
    else if Escape = 'u' then
    begin
      if Position + 3 > JsonLength then
        exit;
      CodePoint := 0;
      for I := 0 to 3 do
      begin
        Digit := HexDigitValue(Json[Position + I]);
        if Digit < 0 then
          exit;
        CodePoint := (CodePoint * 16) + Digit;
      end;
      Value := Value + Chr(CodePoint);
      Position := Position + 4;
    end
    else
      exit;
  end;
end;

function TryGetConfiguredIndexRoot(var IndexRoot: String): Boolean;
var
  SettingsJson: AnsiString;
  ParsedRoot: String;
begin
  Result := False;
  IndexRoot := '';
  if LoadStringFromFile(ExpandConstant('{userappdata}\Yagu\settings.json'), SettingsJson) and
     TryReadJsonStringProperty(String(SettingsJson), 'IndexStorageDirectory', ParsedRoot) and
     (Trim(ParsedRoot) <> '') then
  begin
    IndexRoot := RemoveBackslashUnlessRoot(Trim(ParsedRoot));
    Result := True;
    exit;
  end;

  { If settings were explicitly deleted while a custom index was kept, the uninstaller leaves only
    this locator. Setup and the next app launch can then find the preserved data without retaining
    any other preference. }
  if RegQueryStringValue(HKCU, 'Software\Yagu', 'PreservedIndexStorageDirectory', ParsedRoot) and
     (Trim(ParsedRoot) <> '') then
  begin
    IndexRoot := RemoveBackslashUnlessRoot(Trim(ParsedRoot));
    Result := True;
  end;
end;

procedure AddDetectedInstallLocation(const Candidate: String);
var
  Normalized, SearchText: String;
begin
  if Trim(Candidate) = '' then
    exit;

  Normalized := RemoveBackslashUnlessRoot(Trim(Candidate));
  if not FileExists(Normalized + '\{#MyAppExeName}') then
    exit;

  SearchText := #13#10 + Lowercase(ExistingInstallLocations) + #13#10;
  if Pos(#13#10 + Lowercase(Normalized) + #13#10, SearchText) = 0 then
  begin
    if ExistingInstallLocations <> '' then
      ExistingInstallLocations := ExistingInstallLocations + #13#10;
    ExistingInstallLocations := ExistingInstallLocations + Normalized;
  end;
end;

procedure DetectRegisteredInstall(RootKey: Integer);
var
  InstallLocation: String;
begin
  if RegQueryStringValue(
       RootKey,
       'Software\Microsoft\Windows\CurrentVersion\Uninstall\{8F4E2B5A-3C7D-4A1E-B9F6-2D8E5A7C3F1B}_is1',
       'InstallLocation',
       InstallLocation) then
    AddDetectedInstallLocation(InstallLocation);
end;

procedure DetectExistingYaguState();
var
  DefaultRoot: String;
  DefaultIndexCount, CustomIndexCount: Integer;
begin
  ExistingInstallLocations := '';
  ExistingSettingsFound := FileExists(ExpandConstant('{userappdata}\Yagu\settings.json'));
  ExistingIndexScopeCount := 0;
  ExistingIndexLocations := '';
  ExistingCustomIndexRoot := '';

  DetectRegisteredInstall(HKCU32);
  DetectRegisteredInstall(HKLM32);
  if IsWin64 then
  begin
    DetectRegisteredInstall(HKCU64);
    DetectRegisteredInstall(HKLM64);
  end;

  { Registry registration is authoritative, with fixed-location probes as recovery for a damaged or
    manually copied installation. Both architecture locations are checked on 64-bit Windows. }
  AddDetectedInstallLocation(ExpandConstant('{commonpf}\{#MyAppName}'));
  if IsWin64 then
  begin
    AddDetectedInstallLocation(ExpandConstant('{commonpf32}\{#MyAppName}'));
    AddDetectedInstallLocation(ExpandConstant('{commonpf64}\{#MyAppName}'));
  end;

  DefaultRoot := ExpandConstant('{localappdata}\Yagu\content-index');
  DefaultIndexCount := CountRecognizedIndexScopes(DefaultRoot);
  if DefaultIndexCount > 0 then
  begin
    ExistingIndexScopeCount := DefaultIndexCount;
    ExistingIndexLocations := IntToStr(DefaultIndexCount) + ' at ' + DefaultRoot;
  end;

  if TryGetConfiguredIndexRoot(ExistingCustomIndexRoot) then
  begin
    ExistingCustomIndexRoot := RemoveBackslashUnlessRoot(ExistingCustomIndexRoot);
    if CompareText(ExistingCustomIndexRoot, DefaultRoot) = 0 then
      ExistingCustomIndexRoot := ''
    else
    begin
      CustomIndexCount := CountRecognizedIndexScopes(ExistingCustomIndexRoot);
      if CustomIndexCount > 0 then
      begin
        ExistingIndexScopeCount := ExistingIndexScopeCount + CustomIndexCount;
        if ExistingIndexLocations <> '' then
          ExistingIndexLocations := ExistingIndexLocations + #13#10;
        ExistingIndexLocations := ExistingIndexLocations +
          IntToStr(CustomIndexCount) + ' at ' + ExistingCustomIndexRoot;
      end
      else
        ExistingCustomIndexRoot := '';
    end;
  end;
  ExistingIndexesFound := ExistingIndexScopeCount > 0;
end;

function EscapeRtfText(const Value: String): String;
var
  I: Integer;
  Character: Char;
begin
  Result := '';
  for I := 1 to Length(Value) do
  begin
    Character := Value[I];
    if (Character = '\') or (Character = '{') or (Character = '}') then
      Result := Result + '\' + Character
    else if Character = #13 then
      Result := Result + '\line '
    else if (Character = #10) and ((I = 1) or (Value[I - 1] <> #13)) then
      Result := Result + '\line '
    else if Character <> #10 then
      Result := Result + Character;
  end;
end;

function CreateExistingDataCheckBox(const Caption: String; Top: Integer): TNewCheckBox;
begin
  Result := TNewCheckBox.Create(ExistingDataPage);
  Result.Parent := ExistingDataPage.Surface;
  Result.Left := 0;
  Result.Top := Top;
  Result.Width := ExistingDataPage.SurfaceWidth;
  Result.Height := ScaleY(17);
  Result.Caption := Caption;
  Result.Checked := True;
end;

procedure InitializeWizard();
var
  Rtf: String;
  NoticesLines: TArrayOfString;
  I: Integer;
  OptionCount, OptionTop: Integer;
begin
  ExtractTemporaryFile('THIRD-PARTY-NOTICES.txt');
  if not LoadStringsFromFile(
    ExpandConstant('{tmp}\THIRD-PARTY-NOTICES.txt'), NoticesLines) then
    RaiseException('Setup could not load the bundled third-party notices.');

  ThirdPartyNoticesPage := CreateCustomPage(
    wpLicense,
    'Third-party notices',
    'Review the licenses and attribution notices for components included with Yagu');
  ThirdPartyNoticesViewer := TRichEditViewer.Create(ThirdPartyNoticesPage);
  ThirdPartyNoticesViewer.Parent := ThirdPartyNoticesPage.Surface;
  ThirdPartyNoticesViewer.Left := 0;
  ThirdPartyNoticesViewer.Top := 0;
  ThirdPartyNoticesViewer.Width := ThirdPartyNoticesPage.SurfaceWidth;
  ThirdPartyNoticesViewer.Height := ThirdPartyNoticesPage.SurfaceHeight;
  ThirdPartyNoticesViewer.BevelKind := bkFlat;
  ThirdPartyNoticesViewer.BorderStyle := bsNone;
  ThirdPartyNoticesViewer.ReadOnly := True;
  ThirdPartyNoticesViewer.ScrollBars := ssVertical;
  ThirdPartyNoticesViewer.UseRichEdit := True;
  for I := 0 to GetArrayLength(NoticesLines) - 1 do
    ThirdPartyNoticesViewer.Lines.Add(NoticesLines[I]);

  DetectExistingYaguState();
  if (ExistingInstallLocations = '') and not ExistingSettingsFound and not ExistingIndexesFound then
    exit;

  Rtf :=
    '{\rtf1\ansi\ansicpg1252\deff0' +
    '{\fonttbl{\f0\fswiss\fcharset0 Segoe UI;}}' +
    '{\colortbl ;\red0\green70\blue140;\red170\green30\blue30;\red0\green100\blue55;}' +
    '\viewkind4\uc1\pard\f0\fs18' +
    '\b Setup found existing Yagu program files or saved data.\b0\par\par ';
  if ExistingInstallLocations <> '' then
    Rtf := Rtf + '\b Detected installation(s)\b0\par ' +
      EscapeRtfText(ExistingInstallLocations) + '\par\par ';
  if ExistingSettingsFound then
    Rtf := Rtf + '\b Existing settings\b0\par ' +
      EscapeRtfText(ExpandConstant('{userappdata}\Yagu\settings.json')) + '\par\par ';
  if ExistingIndexesFound then
    Rtf := Rtf + '\b Recognized content indexes: ' + IntToStr(ExistingIndexScopeCount) + '\b0\par ' +
      EscapeRtfText(ExistingIndexLocations) + '\par\par ';
  Rtf := Rtf +
    '\b\cf3 Keep is the recommended default.\cf0\b0 Existing settings are loaded by the new version; ' +
    'new settings receive defaults and supported compatibility migrations are applied.\par ';
  if ExistingSettingsFound or ExistingIndexesFound then
    Rtf := Rtf +
      '\b\cf2 Warning: clearing a Keep option permanently deletes that data.\cf0\b0 ';
  if ExistingIndexesFound then
    Rtf := Rtf + 'Rebuilding content indexes can take a long time.';
  Rtf := Rtf + '\par}';

  ExistingDataPage := CreateCustomPage(
    wpSelectTasks,
    'Existing Yagu installation or data found',
    'Choose what Setup should preserve');

  OptionCount := 1;
  if ExistingSettingsFound then
    OptionCount := OptionCount + 1;
  if ExistingIndexesFound then
    OptionCount := OptionCount + 1;
  OptionTop := ExistingDataPage.SurfaceHeight - (OptionCount * ScaleY(22));

  ExistingSummaryViewer := TRichEditViewer.Create(ExistingDataPage);
  ExistingSummaryViewer.Parent := ExistingDataPage.Surface;
  ExistingSummaryViewer.Left := 0;
  ExistingSummaryViewer.Top := 0;
  ExistingSummaryViewer.Width := ExistingDataPage.SurfaceWidth;
  ExistingSummaryViewer.Height := OptionTop - ScaleY(8);
  ExistingSummaryViewer.BevelKind := bkFlat;
  ExistingSummaryViewer.BorderStyle := bsNone;
  ExistingSummaryViewer.ReadOnly := True;
  ExistingSummaryViewer.ScrollBars := ssVertical;
  ExistingSummaryViewer.UseRichEdit := True;
  ExistingSummaryViewer.RTFText := Rtf;

  ExistingContinueCheckBox := CreateExistingDataCheckBox(
    'Continue with this installation or update', OptionTop);
  OptionTop := OptionTop + ScaleY(22);
  if ExistingSettingsFound then
  begin
    ExistingSettingsCheckBox := CreateExistingDataCheckBox(
      'Keep settings and apply supported migrations (recommended)', OptionTop);
    OptionTop := OptionTop + ScaleY(22);
  end;
  if ExistingIndexesFound then
    ExistingIndexesCheckBox := CreateExistingDataCheckBox(
      'Keep content indexes; avoids rebuilding (recommended)', OptionTop);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (ExistingDataPage <> nil) and (CurPageID = ExistingDataPage.ID) and
     (ExistingContinueCheckBox <> nil) and not ExistingContinueCheckBox.Checked then
  begin
    MsgBox(
      'Select "Continue with this installation or update" to proceed, or click Cancel to leave the existing installation unchanged.',
      mbInformation,
      MB_OK);
    Result := False;
  end;
end;

procedure DeleteRecognizedIndexData(const IndexRoot: String; DeleteDedicatedRoot: Boolean);
var
  FindRec: TFindRec;
  Candidate: String;
begin
  if not DirExists(IndexRoot) then
    exit;

  if DeleteDedicatedRoot then
  begin
    DelTree(IndexRoot, True, True, True);
    exit;
  end;

  { A custom storage path may contain unrelated user data. Delete only positively identified Yagu
    scope directories and Yagu's root-level mutation artifacts; never recursively delete the root. }
  if FindFirst(IndexRoot + '\*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Name <> '.') and (FindRec.Name <> '..') then
        begin
          Candidate := IndexRoot + '\' + FindRec.Name;
          if ((FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0) then
          begin
            if IsRecognizedIndexScope(Candidate) or
               (Pos('.build-', Lowercase(FindRec.Name)) = 1) or
               (Pos('.pdf-backup-', Lowercase(FindRec.Name)) = 1) then
              DelTree(Candidate, True, True, True);
          end;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
  DeleteFile(IndexRoot + '\.writer.lock');
end;

procedure MaybeRemoveContentIndexes(DuringUninstall: Boolean);
var
  DefaultRoot, CustomRoot, Locations: String;
  HasDefault, HasCustom: Boolean;
  Choice: Integer;
begin
  if (DuringUninstall and UninstallSilent()) or
     ((not DuringUninstall) and WizardSilent()) then
    exit;

  DefaultRoot := ExpandConstant('{localappdata}\Yagu\content-index');
  HasDefault := HasRecognizedIndexData(DefaultRoot);
  HasCustom := TryGetConfiguredIndexRoot(CustomRoot) and
               (CompareText(CustomRoot, DefaultRoot) <> 0) and
               HasRecognizedIndexData(CustomRoot);
  if not HasDefault and not HasCustom then
    exit;

  Locations := '';
  if HasDefault then
    Locations := DefaultRoot;
  if HasCustom then
  begin
    if Locations <> '' then
      Locations := Locations + #13#10;
    Locations := Locations + CustomRoot;
  end;

  if DuringUninstall then
    Choice := MsgBox(
         'Do you want to keep your existing Yagu content indexes?' + #13#10#13#10 +
         'Choose Yes to keep them (recommended if you plan to use or reinstall Yagu), or No to ' +
         'permanently delete the index data. Building it again can take a long time.' + #13#10#13#10 +
         'Index location(s):' + #13#10 + Locations,
         mbConfirmation, MB_YESNO)
  else if (ExistingIndexesCheckBox = nil) or ExistingIndexesCheckBox.Checked then
    Choice := IDYES
  else
    Choice := IDNO;

  if Choice = IDNO then
  begin
    if HasDefault then
      DeleteRecognizedIndexData(DefaultRoot, True);
    if HasCustom then
      DeleteRecognizedIndexData(CustomRoot, False);
    PreservedCustomIndexRoot := '';
    RegDeleteValue(HKCU, 'Software\Yagu', 'PreservedIndexStorageDirectory');
  end;
  if (Choice = IDYES) and DuringUninstall and HasCustom then
    PreservedCustomIndexRoot := CustomRoot;
end;

procedure MaybeRemoveUserSettingsDuringInstall();
var
  SettingsFile: String;
begin
  if WizardSilent() or (ExistingSettingsCheckBox = nil) or ExistingSettingsCheckBox.Checked then
    exit;

  { Keep a minimal locator when settings are deleted but a custom index was explicitly preserved. }
    if (ExistingCustomIndexRoot <> '') and (ExistingIndexesCheckBox <> nil) and
      ExistingIndexesCheckBox.Checked then
    RegWriteStringValue(
      HKCU,
      'Software\Yagu',
      'PreservedIndexStorageDirectory',
      ExistingCustomIndexRoot);

  SettingsFile := ExpandConstant('{userappdata}\Yagu\settings.json');
  DeleteFile(SettingsFile);
  RemoveDir(ExpandConstant('{userappdata}\Yagu'));
end;

{ Called after the user clicks Install but before any files are written (also runs for silent
  installs). Close any running Yagu -- e.g. installing an update over a running copy -- so its files
  are never locked. Runs before the CloseApplications/Restart Manager scan, so no "please close"
  prompt appears. Returning '' proceeds with the install. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  WaitForLaunchingYagu();
  KillYaguProcesses();
  { The visible existing-data wizard page captured independent settings/index choices. Apply them
    only now, after the user clicked Install; silent installs preserve all data. Indexes are handled
    first because their custom location may still be stored in settings. }
  MaybeRemoveContentIndexes(False);
  MaybeRemoveUserSettingsDuringInstall();
  Result := '';
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

{ Runs at the very start of uninstall, before any files are removed. Force-close any running Yagu so
  its executable/DLLs are not locked while the uninstaller deletes them. Returning True proceeds. }
function InitializeUninstall(): Boolean;
begin
  KillYaguProcesses();
  MaybeRemoveContentIndexes(True);
  Result := True;
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
  begin
    if PreservedCustomIndexRoot <> '' then
      RegWriteStringValue(HKCU, 'Software\Yagu', 'PreservedIndexStorageDirectory', PreservedCustomIndexRoot);
    exit;
  end;

  if MsgBox(
       'Do you want to keep your Yagu settings and preferences?' + #13#10#13#10 +
       'Choose Yes to keep them (useful if you plan to reinstall Yagu later), or No to permanently ' +
       'delete your settings file:' + #13#10 + SettingsFile,
       mbConfirmation, MB_YESNO) = IDNO then
  begin
    DeleteFile(SettingsFile);
    { Best-effort tidy-up: removes the folder only when empty, so logs/other data are preserved. }
    RemoveDir(ExpandConstant('{userappdata}\Yagu'));
    if PreservedCustomIndexRoot <> '' then
      RegWriteStringValue(HKCU, 'Software\Yagu', 'PreservedIndexStorageDirectory', PreservedCustomIndexRoot);
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

    // Remove the install folder from the system PATH if the "Add to PATH" task had added it.
    RemoveAppFromSystemPath();

    // Offer to remove the user's settings file (kept by default).
    MaybeRemoveUserSettings();
  end;
end;
