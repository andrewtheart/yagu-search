<#
.SYNOPSIS
  Builds one or more Yagu installer variants by orchestrating build-installer.ps1.

.DESCRIPTION
  Yagu ships four installer variants:

    x64       64-bit (win-x64).   OCR works; models download on first use.
    x86       32-bit (win-x86).   OCR works; models download on first use.
    arm64     ARM64  (win-arm64). OCR works; models download on first use.
    x64-offline  64-bit OFFLINE edition: the OCR runtime + models AND the Tesseract
                 English data are bundled (no first-use download), and Tesseract is
                 the default OCR engine.

  There is intentionally no x86-offline / arm64-offline variant: the bundled OCR
  runtime (native PaddleOCR + OpenCv) is win-x64 only, so OCR can only be bundled for
  x64. On x86/arm64 OCR still works at runtime by downloading its assets.

  Each variant is a full self-contained Native AOT Release build plus its Inno
  Setup installer. Builds run to installer\ (and installer\output\), and the
  per-architecture/edition "keep newest" rule in build-installer.ps1 applies.

.PARAMETER Variant
  Which variant(s) to build. One or more of: x64, x86, arm64, x64-offline, or all
  (the default). Accepts a comma-separated list. Order/duplicates are normalized.

.PARAMETER InnoSetupPath
  Optional path to ISCC.exe. Passed through to build-installer.ps1 (which
  otherwise auto-detects the standard Inno Setup 6 location).

.PARAMETER OcrPayloadCacheDir
  Optional local OCR cache used to source the bundled payload for the x64-offline
  variant. Passed through to build-installer.ps1.

.PARAMETER SkipReadmeUpdate
  Skip rewriting the README "Download Installer" table. By default, after a
  successful build the four table rows are updated so their filename, GitHub raw
  URL, and (~N MB) size match the newest installer of each suffix on disk.

.PARAMETER Commit
  After a fully successful build, stage every change (git add -A, which includes any
  untracked files) and commit them with the message
  "Build installers v<version> (<variants>)". A no-op when the working tree is clean.

.PARAMETER Push
  After a successful build, stage + commit (exactly as -Commit) and then run git push.
  Implies the commit step, since there must be a commit to push. After a successful push,
  a DRAFT GitHub release (tag v<version>) is created via the 'gh' CLI with the freshly built
  installers attached and auto-generated release notes -- unless -SkipRelease is set. It is a
  DRAFT (not published) so you review the notes/assets and publish it manually. Re-running the
  same version refreshes the existing release's assets instead of failing. If 'gh' is missing or
  unauthenticated, the release step warns (the build + push still succeed) and prints the manual
  'gh release create' command.

.PARAMETER SkipRelease
  With -Push, do NOT create/refresh the draft GitHub release after pushing (build + commit + push
  only). No effect without -Push.

.EXAMPLE
  .\build-all-installers.ps1
  Builds all four variants (x64, x86, arm64, x64-offline).

.EXAMPLE
  .\build-all-installers.ps1 -Variant x64,arm64
  Builds only the x64 and arm64 (no-bundled-OCR) installers.

.EXAMPLE
  .\build-all-installers.ps1 -Variant x64-offline
  Builds only the OFFLINE x64 installer (OCR bundled, Tesseract default).

.EXAMPLE
  .\build-all-installers.ps1 -WhatIf
  Prints the build plan (resolved variants + the build-installer.ps1 commands)
  without building anything.

.EXAMPLE
  .\build-all-installers.ps1 -Commit
  Builds all variants, then stages (git add -A) and commits the result.

.EXAMPLE
  .\build-all-installers.ps1 -Variant x64 -Push
  Builds the x64 installer, commits the result, and pushes it to the remote.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
  [ValidateSet('x64', 'x86', 'arm64', 'x64-offline', 'all')]
  [string[]]$Variant = @('all'),
  [string]$InnoSetupPath,
  [string]$OcrPayloadCacheDir,
  [switch]$SkipReadmeUpdate,
  [switch]$KeepVersion,
  [switch]$Commit,
  [switch]$Push,
  [switch]$SkipRelease
)

$ErrorActionPreference = 'Stop'

$repoRoot = $PSScriptRoot
$buildInstaller = Join-Path $repoRoot 'build-installer.ps1'
if (-not (Test-Path -LiteralPath $buildInstaller)) {
  throw "build-installer.ps1 not found next to this script at: $buildInstaller"
}
$installerDir = Join-Path $repoRoot 'installer'

# Rewrites the four rows of the README "Download Installer" table so each row's
# filename, GitHub raw URL, and (~N MB) size match the newest installer of that
# suffix on disk. Only the link + size token is replaced; the bold label and the
# rest of every row (including its em-dash / middle-dot glyphs) are preserved via
# a capture group, so this script stays ASCII-only. Rows whose suffix has no
# installer on disk are left untouched.
function Update-ReadmeDownloadTable {
  [CmdletBinding()]
  param(
    [Parameter(Mandatory)][string]$ReadmePath,
    [Parameter(Mandatory)][string]$InstallerDir
  )

  if (-not (Test-Path -LiteralPath $ReadmePath)) {
    Write-Warning "README not found at '$ReadmePath' - skipping download-table update."
    return
  }

  $rawBase = 'https://github.com/andrewtheart/yagu-search/raw/main/installer'
  # End-anchored suffixes; 'x64-offline' is checked before 'x64' so the two never
  # collide. Each pattern ends with the exact '-<suffix>.exe', so the 'x64' row
  # can never match the 'x64-offline' installer.
  $suffixes = @('x64-offline', 'x64', 'arm64', 'x86')

  # Read as UTF-8 explicitly. Get-Content -Raw under Windows PowerShell 5.1 decodes
  # a BOM-less UTF-8 file as ANSI, which would corrupt the table's em-dash / middle-dot
  # glyphs on write. File.ReadAllText defaults to UTF-8 (BOM-aware) under 5.1 and pwsh 7.
  $content = [System.IO.File]::ReadAllText($ReadmePath)
  $original = $content
  $updated = New-Object System.Collections.Generic.List[string]

  foreach ($suffix in $suffixes) {
    $suffixEsc = [regex]::Escape($suffix)
    $exe = Get-ChildItem -LiteralPath $InstallerDir -Filter "YaguSetup-*-$suffix.exe" -File -ErrorAction SilentlyContinue |
      Where-Object { $_.Name -match "-$suffixEsc\.exe$" } |
      Sort-Object LastWriteTime | Select-Object -Last 1
    if (-not $exe) {
      Write-Warning "No installer for suffix '$suffix' in '$InstallerDir' - leaving its README row unchanged."
      continue
    }

    $fileName = $exe.Name
    $sizeMb = [math]::Round($exe.Length / 1MB)

    # Group 1 captures the '[**Label** - ' display prefix generically (any chars
    # up to the filename), so the non-ASCII glyphs never appear in this file.
    $pattern = "(\[[^\]]*?)YaguSetup-[0-9.]+-$suffixEsc\.exe\]\(" +
               [regex]::Escape($rawBase) + "/YaguSetup-[0-9.]+-$suffixEsc\.exe\)\s*\(~[\d.]+\s*MB\)"
    $replacement = "`${1}$fileName]($rawBase/$fileName) (~$sizeMb MB)"

    $rx = [regex]$pattern
    if (-not $rx.IsMatch($content)) {
      Write-Warning "Could not find the '$suffix' row in the README download table - it was left unchanged."
      continue
    }

    $new = $rx.Replace($content, $replacement)
    if ($new -ne $content) {
      $content = $new
      $updated.Add("$suffix -> $fileName (~$sizeMb MB)")
    }
  }

  if ($content -ne $original) {
    # Preserve UTF-8 (no BOM) and the existing line endings; works under both
    # Windows PowerShell 5.1 and pwsh 7 (Set-Content -Encoding utf8 differs).
    [System.IO.File]::WriteAllText($ReadmePath, $content, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "README download table updated:" -ForegroundColor Green
    foreach ($u in $updated) { Write-Host "  $u" -ForegroundColor Green }
  }
  else {
    Write-Host "README download table already up to date." -ForegroundColor DarkGray
  }
}

# Canonical variant -> (architecture, bundle-OCR) and the installer filename suffix
# that build-installer.ps1 produces (YaguSetup-<version>-<suffix>.exe).
$variantSpecs = [ordered]@{
  'x64'         = @{ Architecture = 'x64';   IncludeOcr = $false; Suffix = 'x64' }
  'x86'         = @{ Architecture = 'x86';   IncludeOcr = $false; Suffix = 'x86' }
  'arm64'       = @{ Architecture = 'arm64'; IncludeOcr = $false; Suffix = 'arm64' }
  'x64-offline' = @{ Architecture = 'x64';   IncludeOcr = $true;  Suffix = 'x64-offline' }
}

# Resolve requested variants: expand 'all', de-duplicate, keep canonical order.
$requested =
  if ($Variant -contains 'all') { @($variantSpecs.Keys) }
  else { @($variantSpecs.Keys | Where-Object { $Variant -contains $_ }) }

if ($requested.Count -eq 0) {
  throw "No valid variants selected. Choose from: $(@($variantSpecs.Keys) -join ', '), or 'all'."
}

Write-Host "Yagu installer build - variants: $($requested -join ', ')" -ForegroundColor Cyan

# Dry run (-WhatIf): print the plan and exit without building.
if ($WhatIfPreference) {
  Write-Host "WhatIf: the following installer builds would run:" -ForegroundColor Yellow
  foreach ($name in $requested) {
    $spec = $variantSpecs[$name]
    $cmd = "build-installer.ps1 -Architecture $($spec.Architecture)"
    if ($spec.IncludeOcr) { $cmd += ' -IncludeOcr' }
    Write-Host ("  {0,-8} -> {1}" -f $name, $cmd)
  }
  if ($Commit -or $Push) {
    Write-Host ("  post-build: git add -A + commit{0}" -f $(if ($Push) { ' + push' } else { '' })) -ForegroundColor Yellow
    if ($Push -and -not $SkipRelease) {
      Write-Host "  post-push: gh release create v<version> --draft (built installers attached, auto-generated notes)" -ForegroundColor Yellow
    }
  }
  return
}

# A release is ONE version across EVERY variant. build-installer.ps1's `dotnet publish` step
# auto-increments build-version.txt on each call, so without pinning, a 4-variant run gives the
# same release four different version numbers (e.g. x64=2327, x86=2328, arm64=2329, offline=2330).
# Pin one version for the whole run: bump ONCE here (a fresh release number) unless -KeepVersion is
# set, then build every variant with -SkipVersionIncrement so they all share this single version.
$versionFile = Join-Path $repoRoot 'src\Yagu\Properties\build-version.txt'
if (-not $KeepVersion) {
  $appInfoFile = Join-Path $repoRoot 'src\Yagu\Properties\AppInfo.g.cs'
  $incrementScript = Join-Path $repoRoot 'scripts\increment-yagu-version.ps1'
  & $incrementScript -VersionFile $versionFile -OutputFile $appInfoFile
}
$pinnedVersion = (Get-Content -LiteralPath $versionFile -Raw).Trim()
Write-Host ("Pinned release version for all variants: {0}{1}" -f $pinnedVersion, $(if ($KeepVersion) { ' (kept current, not bumped)' } else { ' (bumped once)' })) -ForegroundColor Cyan

$results = New-Object System.Collections.Generic.List[object]
foreach ($name in $requested) {
  $spec = $variantSpecs[$name]

  Write-Host ""
  Write-Host "############################################################" -ForegroundColor Cyan
  Write-Host "# Building variant: $name (Architecture=$($spec.Architecture), IncludeOcr=$($spec.IncludeOcr))" -ForegroundColor Cyan
  Write-Host "############################################################" -ForegroundColor Cyan

  $params = @{ Architecture = $spec.Architecture }
  if ($spec.IncludeOcr) { $params['IncludeOcr'] = $true }
  # Every variant in this run shares the single pinned version (see the version-pin block above).
  $params['SkipVersionIncrement'] = $true
  if (-not [string]::IsNullOrWhiteSpace($InnoSetupPath)) { $params['InnoSetupPath'] = $InnoSetupPath }
  if (-not [string]::IsNullOrWhiteSpace($OcrPayloadCacheDir)) { $params['OcrPayloadCacheDir'] = $OcrPayloadCacheDir }

  $success = $true
  $errorMessage = $null
  try {
    # build-installer.ps1 has $ErrorActionPreference='Stop' and throws on any
    # failure, so a non-throwing return means the variant built successfully.
    & $buildInstaller @params
  }
  catch {
    $success = $false
    $errorMessage = $_.Exception.Message
    Write-Warning "Variant '$name' FAILED: $errorMessage"
  }

  $results.Add([pscustomobject]@{ Variant = $name; Suffix = $spec.Suffix; Success = $success; Error = $errorMessage })
}

# Summary: match each built variant to its installer in the repo installer\ folder.
Write-Host ""
Write-Host "==================== Build summary ====================" -ForegroundColor Cyan
foreach ($r in $results) {
  if ($r.Success) {
    $exe = Get-ChildItem -LiteralPath $installerDir -Filter "YaguSetup-*-$($r.Suffix).exe" -File -ErrorAction SilentlyContinue |
      Where-Object { $_.Name -match "-$([regex]::Escape($r.Suffix))\.exe$" } |
      Sort-Object LastWriteTime | Select-Object -Last 1
    if ($exe) {
      Write-Host ("  [OK]   {0,-8} -> {1} ({2} MB)" -f $r.Variant, $exe.Name, [math]::Round($exe.Length / 1MB, 1)) -ForegroundColor Green
    } else {
      Write-Host ("  [OK?]  {0,-8} -> built, but no matching installer found in installer\" -f $r.Variant) -ForegroundColor Yellow
    }
  } else {
    Write-Host ("  [FAIL] {0,-8} -> {1}" -f $r.Variant, $r.Error) -ForegroundColor Red
  }
}
Write-Host "=======================================================" -ForegroundColor Cyan

# Point the README download table at the newest installers on disk (unless opted out).
if ($SkipReadmeUpdate) {
  Write-Host "Skipping README download-table update (-SkipReadmeUpdate)." -ForegroundColor DarkGray
}
else {
  try {
    Update-ReadmeDownloadTable -ReadmePath (Join-Path $repoRoot 'README.md') -InstallerDir $installerDir
  }
  catch {
    Write-Warning "README download-table update failed: $($_.Exception.Message)"
  }
}

$failed = @($results | Where-Object { -not $_.Success })
if ($failed.Count -gt 0) {
  throw "$($failed.Count) of $($results.Count) variant(s) failed: $(@($failed.Variant) -join ', ')."
}

Write-Host "All $($results.Count) variant(s) built successfully." -ForegroundColor Green

# Optionally stage + commit (and push) everything the build produced or changed (the version bump,
# generated AppInfo, the installers, the README table, etc.). -Push implies the commit step, since
# there must be a commit to push. This is only reached after a fully successful build (a failed
# build throws above), so we never commit a half-built release.
if ($Commit -or $Push) {
  if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    throw "-Commit/-Push was requested but 'git' is not available on PATH."
  }

  # git signals success/failure via exit code, not exceptions. Turn off native-command
  # auto-throwing (PowerShell 7.3+) for this block so the "is anything staged?" probe
  # (git diff --cached --quiet exits 1 BY DESIGN when there are staged changes) is not treated as an
  # error; we inspect $LASTEXITCODE ourselves and throw only on genuine failures.
  $restoreNativePref = $false
  $savedNativePref = $null
  if (Test-Path variable:PSNativeCommandUseErrorActionPreference) {
    $savedNativePref = $PSNativeCommandUseErrorActionPreference
    $PSNativeCommandUseErrorActionPreference = $false
    $restoreNativePref = $true
  }
  try {
    $inside = (& git -C $repoRoot rev-parse --is-inside-work-tree 2>$null)
    if ($LASTEXITCODE -ne 0 -or "$inside".Trim() -ne 'true') {
      throw "-Commit/-Push was requested but '$repoRoot' is not a git working tree."
    }

    Write-Host ""
    Write-Host "Staging all changes (git add -A)..." -ForegroundColor Cyan
    & git -C $repoRoot add -A
    if ($LASTEXITCODE -ne 0) { throw "git add -A failed (exit $LASTEXITCODE)." }

    & git -C $repoRoot diff --cached --quiet
    $stagedExit = $LASTEXITCODE
    if ($stagedExit -gt 1) { throw "git diff --cached failed (exit $stagedExit)." }

    if ($stagedExit -eq 1) {
      $commitMessage = "Build installers v$pinnedVersion ($($requested -join ', '))"
      Write-Host "Committing: $commitMessage" -ForegroundColor Cyan
      & git -C $repoRoot commit -m $commitMessage
      if ($LASTEXITCODE -ne 0) { throw "git commit failed (exit $LASTEXITCODE)." }
      Write-Host "Committed." -ForegroundColor Green
    }
    else {
      Write-Host "Nothing to commit - working tree already clean." -ForegroundColor DarkGray
    }

    if ($Push) {
      Write-Host "Pushing (git push)..." -ForegroundColor Cyan
      & git -C $repoRoot push
      if ($LASTEXITCODE -ne 0) { throw "git push failed (exit $LASTEXITCODE)." }
      Write-Host "Pushed." -ForegroundColor Green

      # --- Publish a DRAFT GitHub release for this version with the built installers attached. ---
      # Only after a successful push, so HEAD is on the remote and the release tag can point at it.
      # DRAFT by design: a human reviews the auto-generated notes + assets and publishes manually.
      # A missing/unauthenticated 'gh' or a failed release only WARNS -- the build + push already
      # succeeded, so the whole run must not be reported as a failure for a release-step hiccup.
      if (-not $SkipRelease) {
        $gh = Get-Command gh -ErrorAction SilentlyContinue
        if (-not $gh) {
          Write-Warning ("GitHub release skipped: 'gh' CLI not found on PATH. Install https://cli.github.com/, run 'gh auth login', then: " +
            "gh release create v$pinnedVersion installer\YaguSetup-$pinnedVersion-*.exe --draft --generate-notes")
        }
        else {
          $releaseAssets = @(Get-ChildItem -LiteralPath $installerDir -Filter "YaguSetup-$pinnedVersion-*.exe" -File -ErrorAction SilentlyContinue)
          if ($releaseAssets.Count -eq 0) {
            Write-Warning "GitHub release skipped: no installer\YaguSetup-$pinnedVersion-*.exe found to attach."
          }
          else {
            $tag = "v$pinnedVersion"
            # Derive owner/repo from origin so gh targets the right repo regardless of cwd
            # (handles both https://github.com/owner/repo.git and git@github.com:owner/repo.git).
            $originUrl = (& git -C $repoRoot remote get-url origin 2>$null)
            $repoArgs = @()
            $repoSlug = $null
            if ("$originUrl" -match 'github\.com[:/](?<owner>[^/]+)/(?<repo>[^/]+?)(?:\.git)?/?$') {
              $repoSlug = "$($Matches.owner)/$($Matches.repo)"
              $repoArgs = @('--repo', $repoSlug)
            }
            $headSha = ("$(& git -C $repoRoot rev-parse HEAD 2>$null)").Trim()

            # Idempotent: if the release/tag already exists (re-run of the same version), refresh its
            # assets with --clobber instead of failing on 'release create'.
            & $gh.Source release view $tag @repoArgs *> $null
            if ($LASTEXITCODE -eq 0) {
              Write-Host "GitHub release $tag already exists - refreshing installer assets..." -ForegroundColor Cyan
              & $gh.Source release upload $tag @($releaseAssets.FullName) --clobber @repoArgs
              if ($LASTEXITCODE -ne 0) {
                Write-Warning "GitHub release asset upload failed (exit $LASTEXITCODE). Build + push still succeeded; attach manually: gh release upload $tag installer\YaguSetup-$pinnedVersion-*.exe --clobber"
              }
              else { Write-Host "Refreshed assets on release $tag." -ForegroundColor Green }
            }
            else {
              Write-Host "Creating DRAFT GitHub release $tag with $($releaseAssets.Count) installer(s) attached..." -ForegroundColor Cyan
              $createArgs = @('release', 'create', $tag,
                '--draft',
                '--title', "Yagu $pinnedVersion",
                '--generate-notes',
                '--target', $headSha) + @($releaseAssets.FullName) + $repoArgs
              & $gh.Source @createArgs
              if ($LASTEXITCODE -ne 0) {
                Write-Warning ("GitHub release creation failed (exit $LASTEXITCODE). Build + push still succeeded. Ensure 'gh auth login' is done, then run: " +
                  "gh release create $tag installer\YaguSetup-$pinnedVersion-*.exe --draft --generate-notes")
              }
              else {
                $releasesUrl = if ($repoSlug) { "https://github.com/$repoSlug/releases" } else { "the GitHub Releases page" }
                Write-Host "Draft release $tag created. Review the notes/assets and publish it at: $releasesUrl" -ForegroundColor Green
              }
            }
          }
        }
      }
    }
  }
  finally {
    if ($restoreNativePref) { $PSNativeCommandUseErrorActionPreference = $savedNativePref }
  }
}
