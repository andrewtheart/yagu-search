function Get-YaguGitChangedPaths {
  [CmdletBinding()]
  param([Parameter(Mandatory)][string]$RepoRoot)

  $unstaged = @(& git -C $RepoRoot diff --name-only --no-renames)
  if ($LASTEXITCODE -ne 0) { throw "git diff failed (exit $LASTEXITCODE)." }

  $staged = @(& git -C $RepoRoot diff --cached --name-only --no-renames)
  if ($LASTEXITCODE -ne 0) { throw "git diff --cached failed (exit $LASTEXITCODE)." }

  $untracked = @(& git -C $RepoRoot ls-files --others --exclude-standard)
  if ($LASTEXITCODE -ne 0) { throw "git ls-files failed (exit $LASTEXITCODE)." }

  return @($unstaged + $staged + $untracked |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
    Sort-Object -Unique)
}

function Invoke-YaguGitPathBatches {
  [CmdletBinding()]
  param(
    [Parameter(Mandatory)][string]$RepoRoot,
    [Parameter(Mandatory)][string[]]$Paths,
    [Parameter(Mandatory)][scriptblock]$Action
  )

  for ($offset = 0; $offset -lt $Paths.Count; $offset += 100) {
    $last = [Math]::Min($offset + 99, $Paths.Count - 1)
    [string[]]$batch = @($Paths[$offset..$last])
    & $Action $RepoRoot $batch
    if ($LASTEXITCODE -ne 0) { throw "git path operation failed (exit $LASTEXITCODE)." }
  }
}

function Invoke-YaguReviewedStagedCommit {
  [CmdletBinding()]
  param(
    [Parameter(Mandatory)][string]$RepoRoot,
    [Parameter(Mandatory)][string]$Message
  )

  $stagedPaths = @(& git -C $RepoRoot diff --cached --name-only --no-renames)
  if ($LASTEXITCODE -ne 0) { throw "git diff --cached failed (exit $LASTEXITCODE)." }
  if ($stagedPaths.Count -eq 0) { throw 'No staged changes were selected.' }

  & git -C $RepoRoot commit -m $Message
  if ($LASTEXITCODE -ne 0) { throw "git commit failed (exit $LASTEXITCODE)." }

  $committedPaths = @(& git -C $RepoRoot diff-tree --no-commit-id --name-only -r HEAD)
  if ($LASTEXITCODE -ne 0) { throw "git diff-tree failed (exit $LASTEXITCODE)." }
  $unexpected = @($committedPaths | Where-Object { $stagedPaths -notcontains $_ })
  if ($unexpected.Count -gt 0) {
    throw "The commit hook added unexpected path(s); inspect the unpushed commit before continuing: $($unexpected -join ', ')"
  }
}

function Invoke-YaguFocusedPendingCommits {
  [CmdletBinding()]
  param([Parameter(Mandatory)][string]$RepoRoot)

  if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    throw "Cannot prepare focused commits because 'git' is not available on PATH."
  }

  $restoreNativePreference = $false
  $savedNativePreference = $null
  if (Test-Path variable:PSNativeCommandUseErrorActionPreference) {
    $savedNativePreference = $PSNativeCommandUseErrorActionPreference
    $PSNativeCommandUseErrorActionPreference = $false
    $restoreNativePreference = $true
  }

  [string[]]$originalUntracked = @()
  try {
    $inside = ("$(& git -C $RepoRoot rev-parse --is-inside-work-tree 2>$null)").Trim()
    if ($LASTEXITCODE -ne 0 -or $inside -ne 'true') {
      throw "'$RepoRoot' is not a git working tree."
    }

    $branch = ("$(& git -C $RepoRoot rev-parse --abbrev-ref HEAD)").Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($branch) -or $branch -eq 'HEAD') {
      throw 'Focused commits are not allowed from a detached HEAD.'
    }

    foreach ($operationMarker in @('MERGE_HEAD', 'CHERRY_PICK_HEAD', 'REVERT_HEAD', 'rebase-merge', 'rebase-apply')) {
      $markerPath = ("$(& git -C $RepoRoot rev-parse --git-path $operationMarker)").Trim()
      if ($LASTEXITCODE -ne 0) { throw "Could not inspect Git operation state (exit $LASTEXITCODE)." }
      if (-not [System.IO.Path]::IsPathRooted($markerPath)) {
        $markerPath = Join-Path $RepoRoot $markerPath
      }
      if (Test-Path -LiteralPath $markerPath) {
        throw "Finish the in-progress Git operation ($operationMarker) before publishing."
      }
    }

    $conflicts = @(& git -C $RepoRoot diff --name-only --diff-filter=U)
    if ($LASTEXITCODE -ne 0) { throw "git conflict check failed (exit $LASTEXITCODE)." }
    $conflicts += @(& git -C $RepoRoot diff --cached --name-only --diff-filter=U)
    if ($LASTEXITCODE -ne 0) { throw "git staged conflict check failed (exit $LASTEXITCODE)." }
    $conflicts = @($conflicts | Sort-Object -Unique)
    if ($conflicts.Count -gt 0) {
      throw "Resolve merge conflicts before publishing: $($conflicts -join ', ')"
    }

    $renameStatus = @(& git -C $RepoRoot diff --name-status --find-renames --find-copies)
    if ($LASTEXITCODE -ne 0) { throw "git rename check failed (exit $LASTEXITCODE)." }
    $renameStatus += @(& git -C $RepoRoot diff --cached --name-status --find-renames --find-copies)
    if ($LASTEXITCODE -ne 0) { throw "git staged rename check failed (exit $LASTEXITCODE)." }
    $renames = @($renameStatus | Where-Object { $_ -match '^[RC][0-9]*\s' })
    if ($renames.Count -gt 0) {
      throw 'Renames/copies require manual review before publishing; no automatic hunking was attempted.'
    }

    $pending = @(Get-YaguGitChangedPaths -RepoRoot $RepoRoot)
    if ($pending.Count -eq 0) {
      Write-Host 'No pre-build pending changes to organize.' -ForegroundColor DarkGray
      return
    }

    if ([Console]::IsInputRedirected -or "$env:CI" -match '^(1|true)$') {
      throw 'Pending changes require interactive hunk review before publishing. Commit them manually or rerun from an interactive terminal.'
    }

    Write-Host ''
    Write-Host 'Pending changes must be organized before the installer build.' -ForegroundColor Cyan
    Write-Host 'Select hunks for ONE functional change at a time, quit patch mode, then give that group a focused commit message.' -ForegroundColor Yellow
    Write-Host 'The helper never assigns a source hunk to a commit automatically.' -ForegroundColor Yellow

    & git -C $RepoRoot diff --cached --quiet
    $stagedExit = $LASTEXITCODE
    if ($stagedExit -gt 1) { throw "git diff --cached failed (exit $stagedExit)." }
    if ($stagedExit -eq 1) {
      Write-Host ''
      Write-Host 'Existing staged changes:' -ForegroundColor Cyan
      & git -C $RepoRoot diff --cached --stat
      if ($LASTEXITCODE -ne 0) { throw "git diff --cached --stat failed (exit $LASTEXITCODE)." }

      $choice = (Read-Host 'Commit this already-staged functional group now? [c]ommit/[a]bort').Trim().ToLowerInvariant()
      if ($choice -notin @('c', 'commit')) {
        throw 'Focused commit review aborted; existing staged changes were preserved.'
      }

      $message = (Read-Host 'Focused commit message').Trim()
      if ([string]::IsNullOrWhiteSpace($message)) { throw 'A non-empty focused commit message is required.' }
      Invoke-YaguReviewedStagedCommit -RepoRoot $RepoRoot -Message $message
    }

    $originalUntracked = @(& git -C $RepoRoot ls-files --others --exclude-standard)
    if ($LASTEXITCODE -ne 0) { throw "git ls-files failed (exit $LASTEXITCODE)." }
    if ($originalUntracked.Count -gt 0) {
      Invoke-YaguGitPathBatches -RepoRoot $RepoRoot -Paths $originalUntracked -Action {
        param($root, $batch)
        & git -C $root add --intent-to-add -- @batch
      }
    }

    while (@(Get-YaguGitChangedPaths -RepoRoot $RepoRoot).Count -gt 0) {
      Write-Host ''
      Write-Host 'Interactive hunk selection: choose one functional group, then use q to stop and commit it.' -ForegroundColor Cyan
      & git -C $RepoRoot add --patch
      if ($LASTEXITCODE -ne 0) { throw "git add --patch failed (exit $LASTEXITCODE)." }

      & git -C $RepoRoot diff --cached --quiet
      $stagedExit = $LASTEXITCODE
      if ($stagedExit -gt 1) { throw "git diff --cached failed (exit $stagedExit)." }
      if ($stagedExit -eq 0) {
        $retry = (Read-Host 'No hunks were staged. [r]etry/[a]bort').Trim().ToLowerInvariant()
        if ($retry -notin @('r', 'retry')) { throw 'Focused commit review aborted without changing the working files.' }
        continue
      }

      Write-Host ''
      Write-Host 'Selected functional group:' -ForegroundColor Cyan
      & git -C $RepoRoot diff --cached --stat
      if ($LASTEXITCODE -ne 0) { throw "git diff --cached --stat failed (exit $LASTEXITCODE)." }

      $message = (Read-Host 'Focused commit message').Trim()
      if ([string]::IsNullOrWhiteSpace($message)) { throw 'A non-empty focused commit message is required; selected hunks remain staged.' }
      $confirm = (Read-Host "Commit this group as '$message'? [y/N]").Trim().ToLowerInvariant()
      if ($confirm -notin @('y', 'yes')) { throw 'Focused commit review aborted; selected hunks remain staged.' }

      Invoke-YaguReviewedStagedCommit -RepoRoot $RepoRoot -Message $message
    }

    Write-Host 'All pre-build changes were committed in reviewed functional groups.' -ForegroundColor Green
  }
  finally {
    if ($originalUntracked.Count -gt 0) {
      $stagedPaths = @(& git -C $RepoRoot diff --cached --name-only --no-renames 2>$null)
      if ($LASTEXITCODE -eq 0) {
        $intentOnly = @($originalUntracked | Where-Object { $stagedPaths -notcontains $_ })
        if ($intentOnly.Count -gt 0) {
          Invoke-YaguGitPathBatches -RepoRoot $RepoRoot -Paths $intentOnly -Action {
            param($root, $batch)
            & git -C $root reset --quiet -- @batch
          }
        }
      }
    }

    if ($restoreNativePreference) {
      $PSNativeCommandUseErrorActionPreference = $savedNativePreference
    }
  }
}

function Invoke-YaguInstallerReleaseCommit {
  [CmdletBinding()]
  param(
    [Parameter(Mandatory)][string]$RepoRoot,
    [Parameter(Mandatory)][string[]]$AllowedPaths,
    [Parameter(Mandatory)][string]$Message
  )

  $restoreNativePreference = $false
  $savedNativePreference = $null
  if (Test-Path variable:PSNativeCommandUseErrorActionPreference) {
    $savedNativePreference = $PSNativeCommandUseErrorActionPreference
    $PSNativeCommandUseErrorActionPreference = $false
    $restoreNativePreference = $true
  }

  try {
    $allowed = @{}
    foreach ($path in $AllowedPaths) {
      $allowed[$path.Replace('\', '/')] = $true
    }

    $changed = @(Get-YaguGitChangedPaths -RepoRoot $RepoRoot)
    $unexpected = @($changed | Where-Object { -not $allowed.ContainsKey($_.Replace('\', '/')) })
    if ($unexpected.Count -gt 0) {
      throw "Unexpected post-build change(s) will not be committed or pushed: $($unexpected -join ', ')"
    }

    $releaseChanges = @($changed | Where-Object { $allowed.ContainsKey($_.Replace('\', '/')) })
    if ($releaseChanges.Count -eq 0) {
      Write-Host 'No release-generated changes to commit.' -ForegroundColor DarkGray
      return
    }

    Write-Host "Staging release-generated paths only: $($releaseChanges -join ', ')" -ForegroundColor Cyan
    & git -C $RepoRoot add -A -- @releaseChanges
    if ($LASTEXITCODE -ne 0) { throw "Path-scoped git add failed (exit $LASTEXITCODE)." }

    $stagedPaths = @(& git -C $RepoRoot diff --cached --name-only --no-renames)
    if ($LASTEXITCODE -ne 0) { throw "git diff --cached failed (exit $LASTEXITCODE)." }
    $unexpectedStaged = @($stagedPaths | Where-Object { -not $allowed.ContainsKey($_.Replace('\', '/')) })
    if ($unexpectedStaged.Count -gt 0) {
      throw "Unexpected staged path(s) will not be committed: $($unexpectedStaged -join ', ')"
    }

    Write-Host "Committing: $Message" -ForegroundColor Cyan
    & git -C $RepoRoot commit --only -m $Message -- @releaseChanges
    if ($LASTEXITCODE -ne 0) { throw "git commit failed (exit $LASTEXITCODE)." }

    $committedPaths = @(& git -C $RepoRoot diff-tree --no-commit-id --name-only -r HEAD)
    if ($LASTEXITCODE -ne 0) { throw "git diff-tree failed (exit $LASTEXITCODE)." }
    $unexpectedCommitted = @($committedPaths | Where-Object { -not $allowed.ContainsKey($_.Replace('\', '/')) })
    if ($unexpectedCommitted.Count -gt 0) {
      throw "The release commit contains unexpected path(s); inspect the unpushed commit before continuing: $($unexpectedCommitted -join ', ')"
    }

    $remaining = @(Get-YaguGitChangedPaths -RepoRoot $RepoRoot)
    if ($remaining.Count -gt 0) {
      throw "Changes remain after the release commit; push was stopped: $($remaining -join ', ')"
    }

    Write-Host 'Committed only the expected release-generated changes.' -ForegroundColor Green
  }
  finally {
    if ($restoreNativePreference) {
      $PSNativeCommandUseErrorActionPreference = $savedNativePreference
    }
  }
}