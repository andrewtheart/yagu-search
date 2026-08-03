#requires -Version 5.1
<#
.SYNOPSIS
  Stage-7 real-drive A/B: rebuild a format-v3 index for a folder, then time the SAME content
  search two ways - worker-pruning (IndexUseWorkerQuerySessions ON) vs a plain live scan
  (--no-index) - cold and warm, and verify the result counts are identical.

.DESCRIPTION
  This is the measurement the large-scope plan's Stage-7 sign-off gates on:
  "an accelerated large-scope search is at least as fast as the live scan to first result,
  and materially faster overall when the query is selective."

  Unlike the in-test synthetic harness (LargeScopeSearchSpeedupExperiment), this drives the
  SHIPPING Yagu CLI + settings.json, so it exercises the REAL production build -> v3 -> worker
  -> prune path end to end. It:
    1. Backs up settings.json (restored on exit).
    2. Enables the content index + v3 query structures, and registers the target folder.
    3. Rebuilds the target's index as format-v3 (unless -SkipBuild).
    4. Runs the query with --no-index (pure live scan)      -> BASELINE.
    5. Runs the query with --use-index + IndexUseWorkerQuerySessions=true -> ACCELERATED.
    6. Parses the CLI summary ("Searched N file(s) ... M match(es) ... [T s]" + any "Index:"
       coverage line), verifies match parity, and reports the speedup.

  A rare/zero-match query (the default) is the cleanest isolation: the live scan reads every
  file's content while pruning skips ~all of them, so the delta is the content-read work the
  index avoids. Pass a real selective term with -Query to test a realistic case.

.PARAMETER Target     Folder to index + search. Default C:\src. Use C:\ for the full sign-off (LONG 18+GB build).
.PARAMETER Query      Selective literal. Default is a rare probe token that prunes ~everything.
.PARAMETER Iterations Timed runs per side (default 3). Run 1 is the "cold" number; runs 2..N give the warm median.
.PARAMETER SkipBuild  Reuse the existing index instead of rebuilding (only valid if it is already v3).
.PARAMETER DropCache  Best-effort drop the OS standby cache before each COLD run (needs admin + RAMMap64 on PATH).
.PARAMETER Cli        Path to Yagu.exe (auto-detects the repo Debug then Release build).

.EXAMPLE
  .\scripts\stage7-realdrive-ab.ps1 -Target 'C:\src' -Iterations 3
.EXAMPLE
  # Full whole-drive sign-off (long rebuild):
  .\scripts\stage7-realdrive-ab.ps1 -Target 'C:\' -Query 'Authenticode' -DropCache
#>
param(
    [string]$Target = 'C:\src',
    [string]$Query = 'Xyzzy_Stage7Probe_NoSuchToken',
    [int]$Iterations = 3,
    [switch]$SkipBuild,
    [switch]$DropCache,
    [string]$Cli
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

# ---- Resolve Yagu.exe -------------------------------------------------------
if (-not $Cli) {
    $tfm = 'net10.0-windows10.0.19041.0'
    foreach ($cfg in 'Debug', 'Release') {
        $candidate = Join-Path $repoRoot "src\Yagu\bin\$cfg\$tfm\Yagu.exe"
        if (Test-Path $candidate) { $Cli = $candidate; break }
    }
}
if (-not $Cli -or -not (Test-Path $Cli)) { throw "Yagu.exe not found. Build the app or pass -Cli <path>." }
if (-not (Test-Path $Target)) { throw "Target folder not found: $Target" }
$Target = (Resolve-Path $Target).Path.TrimEnd('\') + '\'
Write-Host "Yagu.exe : $Cli"
Write-Host "Target   : $Target"
Write-Host "Query    : '$Query'"
Write-Host ""

# ---- Avoid settings.json contention with a running GUI ----------------------
Get-Process Yagu -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -like '*\Yagu\bin\*' } |
    ForEach-Object { Write-Host "Stopping running Yagu (PID $($_.Id)) to free settings.json"; Stop-Process -Id $_.Id -Force }
Start-Sleep -Milliseconds 400

$settingsPath = Join-Path $env:APPDATA 'Yagu\settings.json'
if (-not (Test-Path $settingsPath)) { throw "settings.json not found at $settingsPath (launch Yagu once first)." }
$backup = "$settingsPath.stage7-ab-backup"
Copy-Item $settingsPath $backup -Force
Write-Host "Backed up settings -> $backup"

function Set-YaguSettings([hashtable]$values) {
    $j = Get-Content $settingsPath -Raw | ConvertFrom-Json
    foreach ($key in $values.Keys) {
        if ($j.PSObject.Properties.Name -contains $key) { $j.$key = $values[$key] }
        else { $j | Add-Member -NotePropertyName $key -NotePropertyValue $values[$key] -Force }
    }
    ($j | ConvertTo-Json -Depth 25) | Set-Content -Path $settingsPath -Encoding UTF8
}

function Add-IndexedRoot([string]$root) {
    $j = Get-Content $settingsPath -Raw | ConvertFrom-Json
    $roots = @()
    if ($j.PSObject.Properties.Name -contains 'IndexedRoots' -and $j.IndexedRoots) { $roots = @($j.IndexedRoots) }
    if ($roots -notcontains $root) { $roots += $root }
    $j.IndexedRoots = @($roots | Select-Object -Unique)
    ($j | ConvertTo-Json -Depth 25) | Set-Content -Path $settingsPath -Encoding UTF8
}

function Invoke-DropCache {
    if (-not $DropCache) { return $false }
    $ram = Get-Command 'RAMMap64.exe' -ErrorAction SilentlyContinue
    if (-not $ram) { Write-Warning "DropCache requested but RAMMap64.exe not on PATH - measuring WARM."; return $false }
    try { & $ram.Source -Et | Out-Null; & $ram.Source -E0 | Out-Null; Start-Sleep -Seconds 2; return $true }
    catch { Write-Warning "DropCache failed ($_) - measuring WARM."; return $false }
}

# Runs one CLI content search, returns a parsed result object.
function Invoke-YaguSearch([string]$indexFlag) {
    $tmp = [System.IO.Path]::GetTempFileName()
    $args = @('--cli', '--directory', $Target, $Query, '--search-mode', 'content', '--case-sensitive', '--no-exact-match', $indexFlag)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $prevEap = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try { & $Cli @args *> $tmp } finally { $ErrorActionPreference = $prevEap }
    $sw.Stop()
    $out = Get-Content $tmp -Raw
    Remove-Item $tmp -ErrorAction SilentlyContinue
    if ($out) { $out = $out -replace "\x1B\[[0-9;]*m", '' }  # strip ANSI color codes before parsing

    $scanned = $null; $matches = $null; $filesWith = $null; $sec = $null; $indexLine = ''
    $m = [regex]::Match($out, 'Searched\s+([\d,]+)\s+file\(s\)')
    if ($m.Success) { $scanned = [long]($m.Groups[1].Value -replace ',', '') }
    $m = [regex]::Match($out, '([\d,]+)\s+match\(es\)\s+in\s+([\d,]+)\s+file\(s\)')
    if ($m.Success) { $matches = [long]($m.Groups[1].Value -replace ',', ''); $filesWith = [long]($m.Groups[2].Value -replace ',', '') }
    $m = [regex]::Match($out, '\[([\d.]+)s\]')
    if ($m.Success) { $sec = [double]$m.Groups[1].Value }
    # Match only Yagu's completion coverage line. A result file can itself contain text such as
    # "Index: accelerated" (especially when searching logs/transcripts); the old broad regex selected that
    # entire result line and polluted the report. PowerShell may prefix native stderr with "Yagu.exe :".
    $m = [regex]::Match($out, '(?im)^\s*(?:Yagu\.exe\s*:\s*)?Content index:\s*.*$')
    if ($m.Success) { $indexLine = $m.Value.Trim() }

    [pscustomobject]@{
        Scanned      = $scanned
        Matches      = $matches
        FilesMatched = $filesWith
        SearchSec    = $sec           # CLI-reported search time (excludes process launch)
        WallSec      = [math]::Round($sw.Elapsed.TotalSeconds, 2)
        IndexLine    = $indexLine
    }
}

function Median([double[]]$v) {
    if ($v.Count -eq 0) { return 0 }
    $s = $v | Sort-Object
    $mid = [int]([math]::Floor($s.Count / 2))
    if ($s.Count % 2 -eq 1) { return $s[$mid] } else { return ($s[$mid - 1] + $s[$mid]) / 2 }
}

try {
    # ---- 1. Configure + register the target -------------------------------
    Set-YaguSettings @{
        EnableContentIndex           = $true
        UseContentIndexByDefault     = $true
        IndexProduceV3QueryStructures = $true
        IndexUseNativeWorker         = $true
        IndexUseWorkerQuerySessions  = $false   # off during build + baseline
    }
    Add-IndexedRoot $Target

    # ---- 2. Rebuild the index as format-v3 --------------------------------
    if (-not $SkipBuild) {
        Write-Host "Rebuilding format-v3 index for $Target (this can take a while for large folders)..."
        $bsw = [System.Diagnostics.Stopwatch]::StartNew()
        $prevEap = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
        try { & $Cli --cli --rebuild-index $Target } finally { $ErrorActionPreference = $prevEap }
        $bsw.Stop()
        Write-Host ("Index rebuild finished in {0:N1}s" -f $bsw.Elapsed.TotalSeconds)
    }
    else { Write-Host "Skipping build (reusing existing index)." }
    Write-Host ""

    # ---- 3. BASELINE: pure live scan (--no-index) -------------------------
    Write-Host "== BASELINE (live scan, --no-index) =="
    $liveTimes = @(); $liveWall = @(); $baseline = $null
    for ($i = 1; $i -le $Iterations; $i++) {
        if ($i -eq 1) { [void](Invoke-DropCache) }
        $r = Invoke-YaguSearch '--no-index'
        $baseline = $r
        $liveTimes += $r.SearchSec; $liveWall += $r.WallSec
        Write-Host ("  run {0}: scanned={1}, matches={2}, search={3}s, wall={4}s" -f $i, $r.Scanned, $r.Matches, $r.SearchSec, $r.WallSec)
    }

    # ---- 4. ACCELERATED: worker pruning (--use-index + flag on) -----------
    Set-YaguSettings @{ IndexUseWorkerQuerySessions = $true }
    Write-Host ""
    Write-Host "== ACCELERATED (worker pruning, --use-index, IndexUseWorkerQuerySessions=true) =="
    $pruneTimes = @(); $pruneWall = @(); $accel = $null; $engagedLine = ''
    for ($i = 1; $i -le $Iterations; $i++) {
        if ($i -eq 1) { [void](Invoke-DropCache) }
        $r = Invoke-YaguSearch '--use-index'
        $accel = $r
        if ($r.IndexLine) { $engagedLine = $r.IndexLine }
        $pruneTimes += $r.SearchSec; $pruneWall += $r.WallSec
        Write-Host ("  run {0}: scanned={1}, matches={2}, search={3}s, wall={4}s  {5}" -f $i, $r.Scanned, $r.Matches, $r.SearchSec, $r.WallSec, $r.IndexLine)
    }

    # ---- 5. Verdict -------------------------------------------------------
    $liveMed = [math]::Round((Median $liveTimes), 2)
    $pruneCold = if ($pruneTimes.Count -gt 0) { $pruneTimes[0] } else { 0 }
    $pruneWarm = if ($pruneTimes.Count -gt 1) { [math]::Round((Median ($pruneTimes[1..($pruneTimes.Count - 1)])), 2) } else { $pruneCold }
    $engaged = [bool]$engagedLine -or ($accel -and $baseline -and $accel.Scanned -ne $null -and $baseline.Scanned -ne $null -and $accel.Scanned -lt $baseline.Scanned)
    $parity = ($accel -and $baseline -and $accel.Matches -eq $baseline.Matches)

    Write-Host ""
    Write-Host "---------------- RESULT ----------------"
    Write-Host ("Pruning engaged:           {0}  {1}" -f $engaged, $engagedLine)
    Write-Host ("Match parity (identical):  {0}  (live={1}, prune={2})" -f $parity, $baseline.Matches, $accel.Matches)
    Write-Host ("Files scanned (live):      {0:N0}" -f $baseline.Scanned)
    Write-Host ("Files scanned (pruned):    {0:N0}" -f $accel.Scanned)
    Write-Host ("Live-scan median search:   {0}s" -f $liveMed)
    Write-Host ("Prune cold (1st) search:   {0}s" -f $pruneCold)
    Write-Host ("Prune warm median search:  {0}s" -f $pruneWarm)
    if ($pruneWarm -gt 0) { Write-Host ("Speedup (live / prune-warm): {0}x" -f ([math]::Round($liveMed / $pruneWarm, 2))) }
    if ($pruneCold -gt 0) { Write-Host ("Speedup (live / prune-cold): {0}x" -f ([math]::Round($liveMed / $pruneCold, 2))) }
    Write-Host "----------------------------------------"
    if (-not $engaged) { Write-Warning "Pruning did NOT engage - the search live-scanned. Check the index is v3 and the scope fits (see the 'Index:' line / yagu.log)." }
    if (-not $parity) { Write-Warning "MATCH COUNTS DIFFER - correctness regression; do NOT trust the timing." }
}
finally {
    Copy-Item $backup $settingsPath -Force
    Remove-Item $backup -ErrorAction SilentlyContinue
    Write-Host ""
    Write-Host "Restored settings.json from backup."
}
