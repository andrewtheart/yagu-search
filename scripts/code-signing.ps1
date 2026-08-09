<#
.SYNOPSIS
  Optional Authenticode code-signing helpers for the Yagu installer build.

.DESCRIPTION
  Dot-sourced by build-installer.ps1. Signing is entirely OPT-IN: without a
  certificate thumbprint the build behaves exactly as before and produces
  unsigned binaries.

  Yagu's own runtime trust checks bind the app to its publisher, so a partially
  signed build is WORSE than an unsigned one:
    * AuthenticodeVerifier.IsWorkerTrustedForHost refuses to launch a worker whose
      publisher does not match the (signed) host, so Yagu.exe must never be signed
      without also signing Yagu.OcrWorker.exe / Yagu.SemanticWorker.exe /
      Yagu.IndexWorker.exe.
    * AuthenticodeVerifier.IsInstallerTrustedForHostPublisher refuses a downloaded
      update installer unless BOTH the running build and the installer are signed by
      the same publisher, so the setup EXE must be signed too.
  Get-YaguSignableStagedFile therefore returns the whole set, and the build signs it
  in ONE signtool invocation (a hardware token typically prompts for its PIN once per
  invocation, not once per file).

  ASCII-only so it parses identically under Windows PowerShell 5.1 and pwsh.
#>

# Windows SDK signtool.exe. Prefers an explicit path, then PATH, then the newest
# Windows Kits 10 build whose bin\<version>\<arch> folder has signtool.exe.
function Resolve-YaguSignTool {
  [CmdletBinding()]
  param(
    [string]$RequestedPath
  )

  if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
    if (-not (Test-Path -LiteralPath $RequestedPath -PathType Leaf)) {
      throw "signtool.exe not found at the requested path: $RequestedPath"
    }
    return (Resolve-Path -LiteralPath $RequestedPath).Path
  }

  $onPath = Get-Command -Name 'signtool.exe' -CommandType Application -ErrorAction SilentlyContinue |
    Select-Object -First 1
  if ($onPath) { return $onPath.Source }

  $archPreference = switch ($env:PROCESSOR_ARCHITECTURE) {
    'ARM64' { @('arm64', 'x64', 'x86') }
    'x86'   { @('x86', 'x64') }
    default { @('x64', 'x86', 'arm64') }
  }

  $binRoots = @(
    "${env:ProgramFiles(x86)}\Windows Kits\10\bin",
    "$env:ProgramFiles\Windows Kits\10\bin"
  ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and (Test-Path -LiteralPath $_) }

  $candidates = New-Object System.Collections.Generic.List[object]
  foreach ($root in $binRoots) {
    foreach ($versionDir in (Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue)) {
      $parsedVersion = $null
      if (-not [Version]::TryParse($versionDir.Name, [ref]$parsedVersion)) { continue }
      foreach ($arch in $archPreference) {
        $candidate = Join-Path $versionDir.FullName "$arch\signtool.exe"
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
          $candidates.Add([pscustomobject]@{
              Path       = $candidate
              Version    = $parsedVersion
              ArchRank   = [array]::IndexOf($archPreference, $arch)
            })
          break
        }
      }
    }
  }

  $best = $candidates | Sort-Object -Property @{ Expression = 'Version'; Descending = $true }, ArchRank | Select-Object -First 1
  if (-not $best) {
    throw "Could not find signtool.exe. Install the Windows SDK 'Windows SDK Signing Tools' component or pass -SignToolPath."
  }
  return $best.Path
}

# Normalizes a certificate thumbprint (accepts the spaced/uppercase form the Windows
# certificate dialog copies) and verifies the certificate is present with a private key.
function Resolve-YaguSigningCertificate {
  [CmdletBinding()]
  param(
    [Parameter(Mandatory)][string]$Thumbprint
  )

  $normalized = ($Thumbprint -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
  if ($normalized.Length -ne 40) {
    throw "Invalid code-signing thumbprint '$Thumbprint'. Expected a 40-character SHA-1 thumbprint."
  }

  $stores = @('Cert:\CurrentUser\My', 'Cert:\LocalMachine\My')
  foreach ($store in $stores) {
    $cert = Get-ChildItem -Path $store -ErrorAction SilentlyContinue |
      Where-Object { $_.Thumbprint -eq $normalized } |
      Select-Object -First 1
    if ($cert) {
      if ($cert.NotAfter -lt (Get-Date)) {
        throw "Code-signing certificate $normalized expired on $($cert.NotAfter.ToString('yyyy-MM-dd'))."
      }
      return [pscustomobject]@{
        Thumbprint = $normalized
        Subject    = $cert.Subject
        NotAfter   = $cert.NotAfter
        Store      = $store
      }
    }
  }

  throw "Code-signing certificate $normalized was not found in Cert:\CurrentUser\My or Cert:\LocalMachine\My. Insert the token / import the certificate first."
}

# Every Yagu-authored binary in the staging tree. Third-party payloads (Windows App
# Runtime, WebView2, Everything, PaddleOCR natives, pdftotext) keep their own vendor
# signatures and are deliberately NOT re-signed.
function Get-YaguSignableStagedFile {
  [CmdletBinding()]
  param(
    [Parameter(Mandatory)][string]$StagingDir
  )

  if (-not (Test-Path -LiteralPath $StagingDir)) {
    throw "Staging directory not found: $StagingDir"
  }

  return @(
    Get-ChildItem -LiteralPath $StagingDir -File -Recurse |
      Where-Object { $_.Extension -in @('.exe', '.dll') } |
      Where-Object { $_.Name -like 'Yagu*' -or $_.Name -eq 'yagu_core.dll' } |
      Sort-Object -Property FullName |
      Select-Object -ExpandProperty FullName
  )
}

# Signs every supplied file in ONE signtool call (SHA-256 digest + RFC 3161 timestamp),
# then verifies the result. Throws on any failure so the build cannot ship a half-signed tree.
function Invoke-YaguAuthenticodeSign {
  [CmdletBinding()]
  param(
    [Parameter(Mandatory)][string[]]$Path,
    [Parameter(Mandatory)][string]$CertThumbprint,
    [Parameter(Mandatory)][string]$TimestampUrl,
    [Parameter(Mandatory)][string]$SignToolPath,
    [string]$Description = 'Yagu'
  )

  $files = @($Path | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
  if ($files.Count -eq 0) {
    throw 'Invoke-YaguAuthenticodeSign was called with no files to sign.'
  }
  foreach ($file in $files) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
      throw "Cannot sign missing file: $file"
    }
  }

  Write-Host "Signing $($files.Count) file(s) with certificate $CertThumbprint..."
  $signArgs = @(
    'sign',
    '/sha1', $CertThumbprint,
    '/fd', 'SHA256',
    '/tr', $TimestampUrl,
    '/td', 'SHA256',
    '/d', $Description
  ) + $files

  & $SignToolPath @signArgs
  if ($LASTEXITCODE -ne 0) {
    throw "signtool sign failed with exit code $LASTEXITCODE."
  }

  # /pa = Authenticode policy, /all = every signature present. A signed-but-unverifiable
  # binary would fail Yagu's own WinVerifyTrust gate at runtime, so fail the build here.
  $verifyArgs = @('verify', '/pa', '/all', '/q') + $files
  & $SignToolPath @verifyArgs
  if ($LASTEXITCODE -ne 0) {
    throw "signtool verify failed with exit code $LASTEXITCODE. The produced binaries are not trusted."
  }
  Write-Host "Signature verification passed for $($files.Count) file(s)."
}
