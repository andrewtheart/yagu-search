<#
.SYNOPSIS
    Installs Yagu's repo-tracked git hooks into .git/hooks without disturbing
    the existing Git LFS hooks.

.DESCRIPTION
    Copies every hook from scripts/git-hooks/ into .git/hooks/. This deliberately
    avoids `core.hooksPath`, because that would bypass the Git LFS hooks
    (pre-push, post-checkout, post-commit, post-merge) and break LFS.

    Run this once after cloning, and again whenever a hook in scripts/git-hooks/
    changes.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$repoRoot  = (git rev-parse --show-toplevel).Trim()
$srcDir    = Join-Path $repoRoot 'scripts/git-hooks'
$gitDir    = (git rev-parse --git-path hooks).Trim()
$destDir   = if ([System.IO.Path]::IsPathRooted($gitDir)) { $gitDir } else { Join-Path $repoRoot $gitDir }

if (-not (Test-Path $srcDir)) { throw "Hook source dir not found: $srcDir" }
if (-not (Test-Path $destDir)) { New-Item -ItemType Directory -Path $destDir -Force | Out-Null }

Get-ChildItem -File $srcDir | ForEach-Object {
    $dest = Join-Path $destDir $_.Name
    Copy-Item -LiteralPath $_.FullName -Destination $dest -Force
    # Normalise to LF and ensure executable bit is irrelevant on Windows (sh reads it fine).
    $content = [System.IO.File]::ReadAllText($dest) -replace "`r`n", "`n"
    [System.IO.File]::WriteAllText($dest, $content)
    Write-Host "Installed hook: $($_.Name) -> $dest"
}

Write-Host "Done. Git LFS hooks were left untouched."
