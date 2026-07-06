<#
.SYNOPSIS
  Installs everything required to BUILD and DEVELOP Yagu from source, using OFFICIAL
  vendor installers only. Idempotent: already-present tools are detected and skipped.

.DESCRIPTION
  Prepares a clean Windows machine for Yagu development. Every installer is downloaded
  directly from its official source (no third-party package managers or mirrors):

    - Git for Windows (+ Git LFS)   github.com/git-for-windows  (GitHub Releases API)
    - Rust stable toolchain         static.rust-lang.org (rustup-init) + x64/x86/arm64 targets
    - .NET 10 SDK                   dot.net/v1/dotnet-install.ps1 (Microsoft); also registers
                                    dotnet on PATH (dotnet-install.ps1 does not) and ensures the
                                    official nuget.org v3 feed is a configured package source
    - Visual C++ Build Tools        aka.ms/vs/17/release/vs_BuildTools.exe (Microsoft)
    - Inno Setup 6                  jrsoftware.org  (needed only to build installer EXEs)

  Optional runtime features for exercising the GUI (-IncludeOptional):

    - voidtools Everything          voidtools.com
    - Microsoft Edge WebView2       go.microsoft.com/fwlink (Microsoft)

  The pinned build toolchain matches the repo: .NET SDK 10.0.x (global.json pins
  10.0.107 with rollForward latestFeature) and the Rust targets Yagu builds
  (x86_64-, i686-, and aarch64-pc-windows-msvc are all ensured, regardless of host).
  Per-architecture downloads (Git for Windows, rustup-init, Everything) and the .NET SDK
  are selected to match the host OS architecture, so the script works whether you develop
  on an x64, x86, or arm64 machine.

  Machine-wide installers (Git, .NET SDK, VC++ Build Tools, Inno Setup) require an
  elevated (Administrator) PowerShell. Run non-elevated, the script offers to relaunch
  itself elevated (suppress with -NoElevate; elevated-only steps are then skipped with a
  warning). Rust installs per-user and never needs elevation.

  NOTE: this sets up the command-line build/test/publish toolchain (dotnet build, Native
  AOT link, Rust, installer packaging) - which is all you need to build, test, and debug Yagu
  from VS Code (or any editor) using the dotnet CLI. A full IDE is optional: to open Yagu.sln
  in the Visual Studio IDE instead, install Visual Studio 2026 (18.x) separately - .NET 10
  projects do not load in VS 2022.

.PARAMETER SkipBuildTools
  Do not install the Visual C++ Build Tools even if no MSVC C++ toolchain is detected.
  (Multi-GB download. Skip if you already have Visual Studio with the Desktop C++ workload.)

.PARAMETER IncludeOptional
  Also install the optional runtime features used by the GUI (voidtools Everything and the
  Microsoft Edge WebView2 runtime). Neither is required just to build or run the tests.

.PARAMETER NoElevate
  Do not auto-relaunch elevated. Elevated-only installers are then skipped with a warning.

.EXAMPLE
  .\install-dev-prerequisites.ps1
  Installs every missing build prerequisite (auto-elevates for the machine-wide installers).

.EXAMPLE
  .\install-dev-prerequisites.ps1 -WhatIf
  Reports what WOULD be installed, downloading and changing nothing.

.EXAMPLE
  .\install-dev-prerequisites.ps1 -IncludeOptional
  Also installs voidtools Everything and the WebView2 runtime.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
  [switch]$SkipBuildTools,
  [switch]$IncludeOptional,
  [switch]$NoElevate
)

$ErrorActionPreference = 'Stop'
# Force TLS 1.2 for downloads on older Windows PowerShell 5.1 (pwsh already defaults to it).
try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch { }

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

function Test-IsAdministrator {
  $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
  return ([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)
}

# Refresh the CURRENT session's PATH from the registry (Machine + User) plus Cargo's bin,
# so a tool installed earlier in this run is found by later detection/verification steps.
function Update-SessionPath {
  $machine = [Environment]::GetEnvironmentVariable('Path', 'Machine')
  $user = [Environment]::GetEnvironmentVariable('Path', 'User')
  $cargo = Join-Path $env:USERPROFILE '.cargo\bin'
  $env:Path = (@($machine, $user, $cargo) | Where-Object { $_ }) -join ';'
}

# Persist a directory onto the Machine or User PATH (registry) if it isn't already there.
# Returns $true when it added the entry, $false when it was already present.
function Add-ToPersistentPath {
  param(
    [Parameter(Mandatory)][string]$Directory,
    [ValidateSet('Machine', 'User')][string]$Scope = 'User'
  )
  $current = [Environment]::GetEnvironmentVariable('Path', $Scope)
  $parts = @($current -split ';' | Where-Object { $_ })
  if ($parts | Where-Object { $_.TrimEnd('\') -ieq $Directory.TrimEnd('\') }) { return $false }
  [Environment]::SetEnvironmentVariable('Path', (@($parts + $Directory) -join ';'), $Scope)
  return $true
}

function Save-OfficialFile {
  param(
    [Parameter(Mandatory)][string]$Uri,
    [Parameter(Mandatory)][string]$OutFile
  )
  Write-Host "  downloading: $Uri" -ForegroundColor DarkGray
  Invoke-WebRequest -Uri $Uri -OutFile $OutFile -UseBasicParsing -Headers @{ 'User-Agent' = 'yagu-dev-setup' }
}

function Test-CommandExists {
  param([Parameter(Mandatory)][string]$Name)
  return [bool](Get-Command $Name -ErrorAction SilentlyContinue)
}

# Detect the host OS architecture so per-arch installers/toolchains match the machine the
# script runs on. Returns 'x64', 'x86', or 'arm64' (defaults to 'x64' if detection fails).
function Get-HostArch {
  try {
    switch ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()) {
      'X64' { return 'x64' }
      'X86' { return 'x86' }
      'Arm64' { return 'arm64' }
    }
  }
  catch { }
  # Fallback via environment: PROCESSOR_ARCHITEW6432 reflects the native arch under WOW64.
  $a = $env:PROCESSOR_ARCHITEW6432; if (-not $a) { $a = $env:PROCESSOR_ARCHITECTURE }
  switch ($a) {
    'ARM64' { return 'arm64' }
    'x86' { return 'x86' }
    default { return 'x64' }
  }
}

# ---------------------------------------------------------------------------
# Detection
# ---------------------------------------------------------------------------

function Test-Dotnet10Sdk {
  if (-not (Test-CommandExists 'dotnet')) { return $false }
  $sdks = & dotnet --list-sdks 2>$null
  return [bool]($sdks | Where-Object { $_ -match '^10\.0\.\d+' })
}

# Where dotnet.exe actually lives, even when it isn't on PATH yet (dotnet-install.ps1 does
# not register PATH). Prefers the machine-wide install over a per-user one.
function Get-DotnetInstallDir {
  foreach ($d in @((Join-Path $env:ProgramFiles 'dotnet'), (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'))) {
    if (Test-Path -LiteralPath (Join-Path $d 'dotnet.exe')) { return $d }
  }
  return $null
}

function Get-VsWherePath {
  $p = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
  if (Test-Path -LiteralPath $p) { return $p }
  return $null
}

function Test-MsvcCppTools {
  $vswhere = Get-VsWherePath
  if (-not $vswhere) { return $false }
  $installPath = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath 2>$null
  return [bool]$installPath
}

function Get-InnoSetupPath {
  $candidates = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
  )
  return ($candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1)
}

function Test-WebView2Runtime {
  # Evergreen WebView2 runtime registers its version under the EdgeUpdate client GUID.
  $guid = '{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
  foreach ($root in @('HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients',
                      'HKLM:\SOFTWARE\Microsoft\EdgeUpdate\Clients',
                      'HKCU:\SOFTWARE\Microsoft\EdgeUpdate\Clients')) {
    $key = Join-Path $root $guid
    $pv = (Get-ItemProperty -LiteralPath $key -Name pv -ErrorAction SilentlyContinue).pv
    if ($pv -and $pv -ne '0.0.0.0') { return $true }
  }
  return $false
}

# ---------------------------------------------------------------------------
# Installers (each idempotent; each gated by ShouldProcess so -WhatIf is a dry run)
# ---------------------------------------------------------------------------

function Install-Git {
  Update-SessionPath
  if (Test-CommandExists 'git') {
    Write-Host "[skip] Git already installed: $((git --version) 2>$null)" -ForegroundColor Green
    return
  }
  if (-not $script:IsAdmin) {
    Write-Warning "[skip] Git for Windows needs Administrator to install machine-wide. Re-run this script elevated."
    return
  }
  if (-not $PSCmdlet.ShouldProcess('Git for Windows (+ Git LFS)', 'Install latest release from github.com/git-for-windows')) { return }

  Write-Host "Installing Git for Windows..." -ForegroundColor Cyan
  $release = Invoke-RestMethod -Uri 'https://api.github.com/repos/git-for-windows/git/releases/latest' -Headers @{ 'User-Agent' = 'yagu-dev-setup' }
  # Pick the installer matching the host architecture. Git for Windows ships full x64 and
  # arm64 .exe installers; there is no 32-bit .exe installer, so x86 hosts fall back to the
  # x64 installer.
  $arch = Get-HostArch
  $assetPattern = if ($arch -eq 'arm64') { '^Git-.*-arm64\.exe$' } else { '^Git-.*-64-bit\.exe$' }
  $asset = $release.assets | Where-Object { $_.name -match $assetPattern } | Select-Object -First 1
  if (-not $asset -and $arch -eq 'arm64') {
    # No arm64 installer in this release; fall back to the x64 installer (runs under emulation).
    $asset = $release.assets | Where-Object { $_.name -match '^Git-.*-64-bit\.exe$' } | Select-Object -First 1
  }
  if (-not $asset) { throw "Could not find a Git for Windows installer ($arch) in the latest release." }
  $exe = Join-Path $env:TEMP $asset.name
  Save-OfficialFile -Uri $asset.browser_download_url -OutFile $exe
  Write-Host "  running silent install..." -ForegroundColor DarkGray
  $proc = Start-Process -FilePath $exe -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NOCANCEL', '/SP-' -Wait -PassThru
  if ($proc.ExitCode -ne 0) { throw "Git for Windows installer exited with code $($proc.ExitCode)." }
  Update-SessionPath
  Write-Host "[ok] Git installed." -ForegroundColor Green
}

function Initialize-GitLfs {
  Update-SessionPath
  if (-not (Test-CommandExists 'git')) { return }
  # Git for Windows bundles Git LFS; register it for the current user so LFS-tracked
  # installer EXEs check out as real binaries instead of pointer files.
  if (-not $PSCmdlet.ShouldProcess('Git LFS', 'git lfs install')) { return }
  try {
    git lfs install | Out-Null
    Write-Host "[ok] Git LFS initialised ($((git lfs version) 2>$null))." -ForegroundColor Green
  }
  catch { Write-Warning "git lfs install failed (Git for Windows normally bundles Git LFS): $($_.Exception.Message)" }
}

function Install-Rust {
  Update-SessionPath
  if ((Test-CommandExists 'cargo') -and (Test-CommandExists 'rustc')) {
    Write-Host "[skip] Rust already installed: $((rustc --version) 2>$null)" -ForegroundColor Green
  }
  elseif ($PSCmdlet.ShouldProcess('Rust stable toolchain (rustup, msvc host)', 'Install via rustup-init from static.rust-lang.org')) {
    Write-Host "Installing Rust (rustup, stable, msvc)..." -ForegroundColor Cyan
    $init = Join-Path $env:TEMP 'rustup-init.exe'
    # Fetch the rustup-init that matches the host architecture so the default toolchain host
    # is native (x64/x86/arm64) rather than always x86_64.
    $rustupHost = switch (Get-HostArch) {
      'x86' { 'i686-pc-windows-msvc' }
      'arm64' { 'aarch64-pc-windows-msvc' }
      default { 'x86_64-pc-windows-msvc' }
    }
    Save-OfficialFile -Uri "https://static.rust-lang.org/rustup/dist/$rustupHost/rustup-init.exe" -OutFile $init
    $proc = Start-Process -FilePath $init -ArgumentList '-y', '--default-toolchain', 'stable', '--profile', 'default' -Wait -PassThru
    if ($proc.ExitCode -ne 0) { throw "rustup-init exited with code $($proc.ExitCode)." }
    Update-SessionPath
    Write-Host "[ok] Rust installed." -ForegroundColor Green
  }

  # Ensure all three targets Yagu builds are present, regardless of host architecture. The
  # host's own target is already installed by rustup, so re-adding it is a harmless no-op.
  if (Test-CommandExists 'rustup') {
    foreach ($target in @('x86_64-pc-windows-msvc', 'i686-pc-windows-msvc', 'aarch64-pc-windows-msvc')) {
      if ($PSCmdlet.ShouldProcess("Rust target $target", 'rustup target add')) {
        try {
          rustup target add $target | Out-Null
          Write-Host "[ok] Rust target: $target" -ForegroundColor Green
        }
        catch { Write-Warning "rustup target add $target failed: $($_.Exception.Message)" }
      }
    }
  }
}

function Install-DotnetSdk {
  if (Test-Dotnet10Sdk) {
    Write-Host "[skip] .NET 10 SDK already installed." -ForegroundColor Green
  }
  elseif ($PSCmdlet.ShouldProcess('.NET 10 SDK (channel 10.0)', 'Install via dot.net/v1/dotnet-install.ps1')) {
    Write-Host "Installing .NET 10 SDK..." -ForegroundColor Cyan
    $installScript = Join-Path $env:TEMP 'dotnet-install.ps1'
    Save-OfficialFile -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installScript
    if ($script:IsAdmin) {
      & $installScript -Channel '10.0' -InstallDir (Join-Path $env:ProgramFiles 'dotnet')
    }
    else {
      Write-Warning "Not elevated: installing the .NET 10 SDK per-user. Visual Studio may not see a user-scoped SDK; re-run elevated for a machine-wide install."
      & $installScript -Channel '10.0'
    }
    Write-Host "[ok] .NET 10 SDK installed." -ForegroundColor Green
  }

  # dotnet-install.ps1 (unlike the standalone .exe/.msi installer) does NOT add dotnet to
  # PATH, so a freshly provisioned machine has no `dotnet` command in new terminals and
  # NuGet restore can't run. Persist the install directory (machine-wide when elevated,
  # otherwise per-user) so later steps and future shells resolve it.
  $dotnetDir = Get-DotnetInstallDir
  if ($dotnetDir) {
    $scope = if ($script:IsAdmin -and ($dotnetDir.TrimEnd('\') -ieq (Join-Path $env:ProgramFiles 'dotnet').TrimEnd('\'))) { 'Machine' } else { 'User' }
    if ($PSCmdlet.ShouldProcess("$scope PATH += $dotnetDir", 'Register dotnet on PATH')) {
      if (Add-ToPersistentPath -Directory $dotnetDir -Scope $scope) {
        Write-Host "[ok] Registered $dotnetDir on the $scope PATH." -ForegroundColor Green
      }
    }
  }
  Update-SessionPath
}

function Initialize-NuGetSource {
  Update-SessionPath
  if (-not (Test-CommandExists 'dotnet')) {
    Write-Warning "[skip] Cannot configure the NuGet source: dotnet is not on PATH yet."
    return
  }
  # A user-level NuGet.Config with an empty <packageSources> element (as some machine
  # provisioning leaves behind) SUPPRESSES the built-in nuget.org default, so `dotnet
  # restore` fails with NU1100 for every package. Ensure the official nuget.org v3 feed
  # is registered.
  $sources = & dotnet nuget list source 2>$null
  if ($sources -match 'api\.nuget\.org/v3/index\.json') {
    Write-Host "[skip] nuget.org package source already configured." -ForegroundColor Green
    return
  }
  if (-not $PSCmdlet.ShouldProcess('nuget.org package source', 'dotnet nuget add source https://api.nuget.org/v3/index.json')) { return }
  try {
    & dotnet nuget add source 'https://api.nuget.org/v3/index.json' --name 'nuget.org' | Out-Null
    Write-Host "[ok] Registered nuget.org package source." -ForegroundColor Green
  }
  catch { Write-Warning "Failed to add the nuget.org package source: $($_.Exception.Message)" }
}

function Install-BuildTools {
  if ($SkipBuildTools) {
    Write-Host "[skip] Visual C++ Build Tools (-SkipBuildTools)." -ForegroundColor DarkGray
    return
  }
  if (Test-MsvcCppTools) {
    Write-Host "[skip] Visual C++ (Desktop C++) toolchain already present." -ForegroundColor Green
    return
  }
  if (-not $script:IsAdmin) {
    Write-Warning "[skip] Visual C++ Build Tools need Administrator. Re-run elevated, or install the 'Desktop development with C++' workload via the Visual Studio Installer."
    return
  }
  if (-not $PSCmdlet.ShouldProcess('Visual C++ Build Tools (Desktop C++ workload + Windows 11 SDK + ARM64 tools)', 'Install from aka.ms/vs/17/release/vs_BuildTools.exe')) { return }

  Write-Host "Installing Visual C++ Build Tools (multi-GB download; this can take a while)..." -ForegroundColor Cyan
  $bootstrapper = Join-Path $env:TEMP 'vs_BuildTools.exe'
  Save-OfficialFile -Uri 'https://aka.ms/vs/17/release/vs_BuildTools.exe' -OutFile $bootstrapper
  $btArgs = @(
    '--quiet', '--wait', '--norestart', '--nocache',
    '--add', 'Microsoft.VisualStudio.Workload.VCTools',
    '--add', 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64',
    '--add', 'Microsoft.VisualStudio.Component.VC.Tools.ARM64',
    '--add', 'Microsoft.VisualStudio.Component.Windows11SDK.22621',
    '--includeRecommended'
  )
  $proc = Start-Process -FilePath $bootstrapper -ArgumentList $btArgs -Wait -PassThru
  # 0 = success, 3010 = success but a reboot is required.
  if ($proc.ExitCode -notin @(0, 3010)) { throw "Visual C++ Build Tools installer exited with code $($proc.ExitCode)." }
  Write-Host "[ok] Visual C++ Build Tools installed$(if ($proc.ExitCode -eq 3010) { ' (reboot required)' })." -ForegroundColor Green
}

function Install-InnoSetup {
  if (Get-InnoSetupPath) {
    Write-Host "[skip] Inno Setup 6 already installed." -ForegroundColor Green
    return
  }
  if (-not $script:IsAdmin) {
    Write-Warning "[skip] Inno Setup needs Administrator. Re-run elevated. (Only required to build the installer EXEs with build-installer.ps1.)"
    return
  }
  if (-not $PSCmdlet.ShouldProcess('Inno Setup 6', 'Install latest from jrsoftware.org')) { return }

  Write-Host "Installing Inno Setup 6..." -ForegroundColor Cyan
  $exe = Join-Path $env:TEMP 'innosetup-latest.exe'
  Save-OfficialFile -Uri 'https://jrsoftware.org/download.php/is.exe' -OutFile $exe
  $proc = Start-Process -FilePath $exe -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-' -Wait -PassThru
  if ($proc.ExitCode -ne 0) { throw "Inno Setup installer exited with code $($proc.ExitCode)." }
  Write-Host "[ok] Inno Setup 6 installed." -ForegroundColor Green
}

function Install-Everything {
  if (Test-CommandExists 'Everything') {
    Write-Host "[skip] voidtools Everything already on PATH." -ForegroundColor Green
    return
  }
  if (Get-Process 'Everything' -ErrorAction SilentlyContinue) {
    Write-Host "[skip] voidtools Everything appears to be running." -ForegroundColor Green
    return
  }
  if (-not $script:IsAdmin) {
    Write-Warning "[skip] voidtools Everything needs Administrator (installs a service). Re-run elevated with -IncludeOptional."
    return
  }
  if (-not $PSCmdlet.ShouldProcess('voidtools Everything (optional)', 'Install from voidtools.com')) { return }

  Write-Host "Installing voidtools Everything (optional runtime feature)..." -ForegroundColor Cyan
  $exe = Join-Path $env:TEMP 'Everything-Setup.exe'
  # Pick the installer matching the host arch. voidtools ships x86 and x64 setup EXEs for
  # 1.4.1.1032; there is no 1.4 arm64 installer, so arm64 hosts fall back to x64 (emulated).
  $everythingArch = if ((Get-HostArch) -eq 'x86') { 'x86' } else { 'x64' }
  try {
    Save-OfficialFile -Uri "https://www.voidtools.com/Everything-1.4.1.1032.$everythingArch-Setup.exe" -OutFile $exe
    $proc = Start-Process -FilePath $exe -ArgumentList '/S' -Wait -PassThru   # Everything's NSIS installer: /S = silent
    if ($proc.ExitCode -ne 0) { Write-Warning "Everything installer exited with code $($proc.ExitCode)." }
    else { Write-Host "[ok] voidtools Everything installed." -ForegroundColor Green }
  }
  catch { Write-Warning "voidtools Everything install failed (optional): $($_.Exception.Message)" }
}

function Install-WebView2 {
  if (Test-WebView2Runtime) {
    Write-Host "[skip] Microsoft Edge WebView2 runtime already installed." -ForegroundColor Green
    return
  }
  if (-not $PSCmdlet.ShouldProcess('Microsoft Edge WebView2 runtime (optional)', 'Install evergreen bootstrapper from go.microsoft.com')) { return }

  Write-Host "Installing Microsoft Edge WebView2 runtime (optional; embedded terminal + help)..." -ForegroundColor Cyan
  $exe = Join-Path $env:TEMP 'MicrosoftEdgeWebView2Setup.exe'
  try {
    Save-OfficialFile -Uri 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' -OutFile $exe
    $proc = Start-Process -FilePath $exe -ArgumentList '/silent', '/install' -Wait -PassThru
    if ($proc.ExitCode -ne 0) { Write-Warning "WebView2 bootstrapper exited with code $($proc.ExitCode)." }
    else { Write-Host "[ok] WebView2 runtime installed." -ForegroundColor Green }
  }
  catch { Write-Warning "WebView2 runtime install failed (optional): $($_.Exception.Message)" }
}

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------

function Write-FinalSummary {
  Update-SessionPath
  Write-Host ""
  Write-Host "==================== Yagu dev prerequisites ====================" -ForegroundColor Cyan

  function Show-ToolStatus([string]$Name, [scriptblock]$Probe) {
    try {
      $value = & $Probe
      if ($value) { Write-Host ("  [OK]   {0,-18} {1}" -f $Name, $value) -ForegroundColor Green }
      else { Write-Host ("  [MISS] {0,-18} not detected" -f $Name) -ForegroundColor Yellow }
    }
    catch { Write-Host ("  [MISS] {0,-18} not detected" -f $Name) -ForegroundColor Yellow }
  }

  Show-ToolStatus 'Git'          { if (Test-CommandExists 'git') { (git --version) } }
  Show-ToolStatus 'Git LFS'      { if (Test-CommandExists 'git') { (git lfs version) } }
  Show-ToolStatus 'Rust (rustc)' { if (Test-CommandExists 'rustc') { (rustc --version) } }
  Show-ToolStatus 'Cargo'        { if (Test-CommandExists 'cargo') { (cargo --version) } }
  Show-ToolStatus '.NET SDK 10'  { if (Test-Dotnet10Sdk) { ((& dotnet --list-sdks | Where-Object { $_ -match '^10\.0\.' } | Select-Object -Last 1)) } }
  Show-ToolStatus 'nuget.org src' { if ((Test-CommandExists 'dotnet') -and ((& dotnet nuget list source 2>$null) -match 'api\.nuget\.org/v3/index\.json')) { 'configured' } }
  Show-ToolStatus 'MSVC C++'     { if (Test-MsvcCppTools) { 'Desktop C++ toolchain present' } }
  Show-ToolStatus 'Inno Setup 6' { Get-InnoSetupPath }
  if ($IncludeOptional) {
    Show-ToolStatus 'Everything'   { if ((Test-CommandExists 'Everything') -or (Get-Process 'Everything' -ErrorAction SilentlyContinue)) { 'installed' } }
    Show-ToolStatus 'WebView2'     { if (Test-WebView2Runtime) { 'runtime present' } }
  }

  Write-Host "===============================================================" -ForegroundColor Cyan
  Write-Host "You can build, test, and debug Yagu from VS Code (or any editor) with the dotnet CLI toolchain above - no full IDE required." -ForegroundColor Yellow
  Write-Host "To open Yagu.sln in the Visual Studio IDE instead, install Visual Studio 2026 (18.x): https://visualstudio.microsoft.com/" -ForegroundColor Yellow
  Write-Host "  (.NET 10 projects do NOT load in Visual Studio 2022; the Build Tools above are only for CLI builds.)" -ForegroundColor DarkGray
  Write-Host "Open a NEW terminal so PATH updates take effect, then build:" -ForegroundColor Cyan
  Write-Host "  dotnet build Yagu.sln -c Release" -ForegroundColor Gray
}

# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

$script:IsAdmin = Test-IsAdministrator

# Auto-elevate for the machine-wide installers (skipped for -WhatIf, which is a read-only
# dry run, and for -NoElevate). The elevated instance re-runs detection so nothing double-installs.
if (-not $script:IsAdmin -and -not $NoElevate -and -not $WhatIfPreference) {
  Write-Host "Several installers require Administrator rights. Relaunching this script elevated..." -ForegroundColor Yellow
  $psExe = (Get-Process -Id $PID).Path
  $relaunchArgs = @('-NoExit', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath)
  if ($SkipBuildTools) { $relaunchArgs += '-SkipBuildTools' }
  if ($IncludeOptional) { $relaunchArgs += '-IncludeOptional' }
  try {
    Start-Process -FilePath $psExe -Verb RunAs -ArgumentList $relaunchArgs | Out-Null
    Write-Host "An elevated PowerShell window has opened to continue the setup." -ForegroundColor Cyan
  }
  catch {
    Write-Warning "Elevation was cancelled or failed. Re-run this script from an elevated PowerShell, or pass -NoElevate to run only the per-user (Rust) steps. ($($_.Exception.Message))"
  }
  return
}

Write-Host "Yagu developer prerequisite setup" -ForegroundColor Cyan
Write-Host ("Elevated: {0}   Optional features: {1}" -f $script:IsAdmin, [bool]$IncludeOptional)
Write-Host ""

Install-Git
Initialize-GitLfs
Install-Rust
Install-DotnetSdk
Initialize-NuGetSource
Install-BuildTools
Install-InnoSetup
if ($IncludeOptional) {
  Install-Everything
  Install-WebView2
}

Write-FinalSummary
