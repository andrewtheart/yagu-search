# Runs the 30-case randomized content-index UI campaign described in
# PLANS/INDEX_ACCELERATION_RANDOMIZED_UI_TEST_PLAN.md.
#
# Windows PowerShell 5.1 compatible. Keep this file ASCII-only.

param(
    [ValidateRange(30, 30)]
    [int]$Count = 30,
    [ValidateRange(1, 30)]
    [int]$StartCaseId = 1,
    [Nullable[int]]$Seed = $null,
    [string]$ManifestPath = "",
    [string]$ExePath = "",
    [string]$OutputDirectory = "",
    [ValidateRange(1, 1440)]
    [int]$AllDrivesTimeoutMinutes = 45,
    [ValidateRange(1, 1440)]
    [int]$DriveTimeoutMinutes = 30,
    [ValidateRange(1, 1440)]
    [int]$FolderTimeoutMinutes = 15,
    [switch]$PrepareOnly
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptDir
if ([string]::IsNullOrWhiteSpace($ExePath)) {
    $ExePath = Join-Path $repoRoot 'src\Yagu\bin\Debug\net10.0-windows10.0.19041.0\Yagu.exe'
}
$ExePath = [IO.Path]::GetFullPath($ExePath)
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot ("TestResults\IndexUiRandomized\" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)

$settingsPath = Join-Path $env:APPDATA 'Yagu\settings.json'
$logPath = Join-Path $env:APPDATA 'Yagu\yagu.log'
$logsDir = Join-Path $OutputDirectory 'logs'
$uiaDir = Join-Path $OutputDirectory 'uia'
$failuresDir = Join-Path $OutputDirectory 'failures'
$resultsPath = Join-Path $OutputDirectory 'results.jsonl'
$casesPath = Join-Path $OutputDirectory 'cases.json'
$preflightPath = Join-Path $OutputDirectory 'preflight.json'
$summaryPath = Join-Path $OutputDirectory 'summary.md'

$utf8NoBom = New-Object Text.UTF8Encoding($false)
$settingsOriginalBytes = $null
$settingsRestored = $false
$campaignProcess = $null
$campaignProcessStartTime = $null
$mainWindow = $null
$campaignStartUtc = [DateTime]::UtcNow
$campaignEndUtc = $null
$campaignError = $null
$results = New-Object Collections.ArrayList
$defects = New-Object Collections.ArrayList
$startupChoices = New-Object Collections.ArrayList

function Write-JsonFile {
    param([string]$Path, $Value, [int]$Depth = 30)
    $json = $Value | ConvertTo-Json -Depth $Depth
    [IO.File]::WriteAllText($Path, $json, $utf8NoBom)
}

function Append-JsonLine {
    param([string]$Path, $Value)
    $json = $Value | ConvertTo-Json -Depth 30 -Compress
    [IO.File]::AppendAllText($Path, $json + [Environment]::NewLine, $utf8NoBom)
}

function Write-CampaignProgress {
    param([string]$Message)
    Write-Host ("[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss'), $Message)
}

function Get-CryptoSeed {
    $bytes = New-Object byte[] 4
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    $value = [BitConverter]::ToInt32($bytes, 0)
    if ($value -eq [int]::MinValue) { return 0 }
    return [Math]::Abs($value)
}

if ($null -eq $Seed) { $Seed = Get-CryptoSeed }
$seedValue = [int]$Seed
$random = New-Object Random($seedValue)

function Shuffle-Array {
    param([object[]]$Items)
    $copy = @($Items)
    for ($i = $copy.Count - 1; $i -gt 0; $i--) {
        $j = $random.Next($i + 1)
        $tmp = $copy[$i]
        $copy[$i] = $copy[$j]
        $copy[$j] = $tmp
    }
    return $copy
}

function Pick-One {
    param([object[]]$Items)
    if ($null -eq $Items -or $Items.Count -eq 0) { return $null }
    return $Items[$random.Next($Items.Count)]
}

function Get-SettingValue {
    param($Settings, [string]$Name, $Default = $null)
    $p = $Settings.PSObject.Properties[$Name]
    if ($null -eq $p) { return $Default }
    return $p.Value
}

function Set-SettingValue {
    param($Settings, [string]$Name, $Value)
    $p = $Settings.PSObject.Properties[$Name]
    if ($null -eq $p) { $Settings | Add-Member -NotePropertyName $Name -NotePropertyValue $Value }
    else { $p.Value = $Value }
}

function Normalize-PathText {
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { return '' }
    $p = $Path.Replace('/', '\').Trim()
    try { $p = [IO.Path]::GetFullPath($p) } catch { }
    $root = [IO.Path]::GetPathRoot($p)
    if (-not [string]::Equals($p, $root, [StringComparison]::OrdinalIgnoreCase)) {
        $p = $p.TrimEnd('\')
    }
    return $p
}

function Test-PathCoveredBy {
    param([string]$Path, [string]$Root)
    $p = Normalize-PathText $Path
    $r = Normalize-PathText $Root
    if ([string]::Equals($p, $r, [StringComparison]::OrdinalIgnoreCase)) { return $true }
    if (-not $r.EndsWith('\')) { $r += '\' }
    return $p.StartsWith($r, [StringComparison]::OrdinalIgnoreCase)
}

function Find-CoveringRoot {
    param([string]$Path, [object[]]$Roots)
    $matches = @($Roots | Where-Object { Test-PathCoveredBy $Path ([string]$_) } | Sort-Object { ([string]$_).Length } -Descending)
    if ($matches.Count -eq 0) { return $null }
    return [string]$matches[0]
}

function Get-DirectorySizeBytes {
    param([string]$Path)
    $sum = [long]0
    if (-not (Test-Path -LiteralPath $Path)) { return $sum }
    try {
        Get-ChildItem -LiteralPath $Path -File -Recurse -Force -ErrorAction SilentlyContinue | ForEach-Object { $sum += [long]$_.Length }
    } catch { }
    return $sum
}

function Test-LikelyCloudDrive {
    param([IO.DriveInfo]$Drive)
    $text = ''
    try { $text = (($Drive.VolumeLabel + ' ' + $Drive.DriveFormat).ToLowerInvariant()) } catch { }
    foreach ($marker in @('google drive','googledrive','onedrive','dropbox','icloud','box drive','pcloud')) {
        if ($text.Contains($marker)) { return $true }
    }
    return $false
}

function Invoke-IndexStatus {
    param([string]$Root)
    $lines = @(& $ExePath --cli --index-status $Root 2>&1 | ForEach-Object { $_.ToString() })
    $text = $lines -join [Environment]::NewLine
    $healthy = $text -match '(?m)^Content index for '
    $coveredBy = $null
    $m = [regex]::Match($text, '(?m)^\s*covered by:\s+(.+?)\s*$')
    if ($m.Success) { $coveredBy = Normalize-PathText $m.Groups[1].Value }
    $documents = 0L
    $segments = 0
    $built = $null
    $m = [regex]::Match($text, '(?m)^\s*documents:\s+([\d,]+)')
    if ($m.Success) { $documents = [long]($m.Groups[1].Value.Replace(',', '')) }
    $m = [regex]::Match($text, '(?m)^\s*segments:\s+(\d+)')
    if ($m.Success) { $segments = [int]$m.Groups[1].Value }
    $m = [regex]::Match($text, '(?m)^\s*built \(UTC\):\s+(.+?)\s*$')
    if ($m.Success) { $built = $m.Groups[1].Value }
    return [pscustomobject][ordered]@{
        root = Normalize-PathText $Root
        healthy = $healthy
        coveredBy = $coveredBy
        documents = $documents
        segments = $segments
        builtUtc = $built
        output = $text
        exitCode = $LASTEXITCODE
    }
}

function Get-CampaignYaguProcesses {
    return @(Get-CimInstance Win32_Process -Filter "Name='Yagu.exe'" -ErrorAction SilentlyContinue)
}

function Test-ExactProcessIdentity {
    param([int]$ProcessId, [datetime]$StartTime)
    try {
        $p = Get-Process -Id $ProcessId -ErrorAction Stop
        return [string]::Equals($p.Path, $ExePath, [StringComparison]::OrdinalIgnoreCase) -and $p.StartTime -eq $StartTime
    } catch { return $false }
}

function Get-MaintenanceWorkers {
    return @(Get-CimInstance Win32_Process -Filter "Name='Yagu.IndexWorker.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -match '(?i)--maintenance' })
}

function Read-LogFromOffset {
    param([long]$Offset)
    if (-not (Test-Path -LiteralPath $logPath)) { return '' }
    $fs = New-Object IO.FileStream($logPath, [IO.FileMode]::Open, [IO.FileAccess]::Read,
        ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    try {
        if ($fs.Length -lt $Offset) { throw "yagu.log rotated or shrank during the case (offset=$Offset length=$($fs.Length))." }
        [void]$fs.Seek($Offset, [IO.SeekOrigin]::Begin)
        $remaining = [int]($fs.Length - $Offset)
        if ($remaining -le 0) { return '' }
        $bytes = New-Object byte[] $remaining
        $read = 0
        while ($read -lt $bytes.Length) {
            $n = $fs.Read($bytes, $read, $bytes.Length - $read)
            if ($n -le 0) { break }
            $read += $n
        }
        return [Text.Encoding]::UTF8.GetString($bytes, 0, $read)
    } finally { $fs.Dispose() }
}

function Get-LogLength {
    if (-not (Test-Path -LiteralPath $logPath)) { return 0L }
    $fs = New-Object IO.FileStream($logPath, [IO.FileMode]::Open, [IO.FileAccess]::Read,
        ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    try { return $fs.Length } finally { $fs.Dispose() }
}

function Get-WerEvents {
    param([datetime]$StartUtc)
    $localStart = $StartUtc.ToLocalTime()
    try {
        return @(Get-WinEvent -FilterHashtable @{ LogName='Application'; StartTime=$localStart } -ErrorAction SilentlyContinue |
            Where-Object {
                $_.ProviderName -in @('Application Error','Windows Error Reporting','.NET Runtime') -and
                $_.Message -match 'Yagu|Yagu.IndexWorker|Microsoft.UI.Xaml|0xc0000005|0xc000027b'
            } | Select-Object TimeCreated,ProviderName,Id,LevelDisplayName,Message)
    } catch { return @() }
}

function Get-TextFileCandidates {
    param([string]$BasePath, [int]$MaxFiles = 250, [int]$MaxDirectories = 250)
    $extensions = @('.cs','.txt','.md','.json','.xml','.log','.ps1','.rs','.toml','.yml','.yaml','.html','.css','.js','.ts','.py','.config')
    $files = New-Object Collections.ArrayList
    $queue = New-Object Collections.Queue
    if (-not (Test-Path -LiteralPath $BasePath -PathType Container)) { return @() }
    $queue.Enqueue($BasePath)
    $visited = 0
    while ($queue.Count -gt 0 -and $files.Count -lt $MaxFiles -and $visited -lt $MaxDirectories) {
        $dir = [string]$queue.Dequeue()
        $visited++
        if ($dir -match '(?i)\\(System Volume Information|\$RECYCLE\.BIN)(\\|$)') { continue }
        if (Test-PathCoveredBy $dir $OutputDirectory) { continue }
        try {
            foreach ($file in Get-ChildItem -LiteralPath $dir -File -Force -ErrorAction SilentlyContinue) {
                if ($files.Count -ge $MaxFiles) { break }
                if ($extensions -notcontains $file.Extension.ToLowerInvariant()) { continue }
                if ($file.Length -le 0 -or $file.Length -gt 2MB) { continue }
                [void]$files.Add($file.FullName)
            }
            foreach ($child in Get-ChildItem -LiteralPath $dir -Directory -Force -ErrorAction SilentlyContinue) {
                if ($queue.Count + $visited -ge $MaxDirectories) { break }
                if ($child.FullName -match '(?i)\\(System Volume Information|\$RECYCLE\.BIN)(\\|$)') { continue }
                $queue.Enqueue($child.FullName)
            }
        } catch { }
    }
    return @($files)
}

function Read-TextFixture {
    param([string]$Path)
    try {
        $bytes = [IO.File]::ReadAllBytes($Path)
        if ($bytes.Length -eq 0 -or $bytes.Length -gt 2MB) { return $null }
        $sniff = [Math]::Min(8192, $bytes.Length)
        for ($i = 0; $i -lt $sniff; $i++) { if ($bytes[$i] -eq 0) { return $null } }
        $strict = New-Object Text.UTF8Encoding($false, $true)
        try { $text = $strict.GetString($bytes) }
        catch {
            if ($bytes.Length -ge 2 -and (($bytes[0] -eq 255 -and $bytes[1] -eq 254) -or ($bytes[0] -eq 254 -and $bytes[1] -eq 255))) {
                $text = [Text.Encoding]::Unicode.GetString($bytes)
            } else { return $null }
        }
        if ([string]::IsNullOrWhiteSpace($text)) { return $null }
        return [pscustomobject]@{ path=$Path; text=$text; lines=@([regex]::Split($text, '\r?\n')) }
    } catch { return $null }
}

function Get-HarvestBase {
    param([string]$Scope)
    $candidates = New-Object Collections.ArrayList
    if ([string]::IsNullOrWhiteSpace($Scope)) {
        foreach ($p in @(
            (Join-Path $repoRoot 'src\Yagu\Services\Index'),
            (Join-Path $repoRoot 'tests\Yagu.Tests\Index'),
            'D:\diskActivityMonitor', 'D:\installationSite')) {
            if (Test-Path -LiteralPath $p) { [void]$candidates.Add($p) }
        }
    } else {
        $normalized = Normalize-PathText $Scope
        $drive = [IO.Path]::GetPathRoot($normalized)
        if ([string]::Equals($normalized, $drive, [StringComparison]::OrdinalIgnoreCase)) {
            $mapped = switch ($drive.ToUpperInvariant()) {
                'C:\' { Join-Path $repoRoot 'src\Yagu\Services\Index' }
                'D:\' { 'D:\diskActivityMonitor' }
                'E:\' { 'E:\Temp' }
                'F:\' { 'F:\Temp' }
                'G:\' { 'G:\My Drive\Colab Notebooks' }
                default { $normalized }
            }
            if (Test-Path -LiteralPath $mapped) { [void]$candidates.Add($mapped) }
        }
        if (Test-Path -LiteralPath $normalized) { [void]$candidates.Add($normalized) }
    }
    if ($candidates.Count -eq 0) { return $null }
    return [string](Pick-One @($candidates))
}

function Get-FixtureForScope {
    param([string]$Scope, [switch]$NeedMultiline, [switch]$NeedMixedCase)
    $base = Get-HarvestBase $Scope
    if ($null -eq $base) { return $null }
    $files = Shuffle-Array @(Get-TextFileCandidates $base)
    foreach ($path in $files) {
        $fixture = Read-TextFixture $path
        if ($null -eq $fixture) { continue }
        $tokens = @([regex]::Matches($fixture.text, '\b[A-Za-z_][A-Za-z0-9_]{2,31}\b') | ForEach-Object { $_.Value } |
            Where-Object { $_ -notmatch '^(true|false|null|public|private|internal|return|using|namespace|class|function|param|string|object)$' } |
            Select-Object -Unique)
        if ($NeedMixedCase) {
            $tokens = @($tokens | Where-Object { $_ -cmatch '[a-z]' -and $_ -cmatch '[A-Z]' })
        }
        $pair = $null
        if ($NeedMultiline) {
            for ($i = 0; $i -lt $fixture.lines.Count - 1; $i++) {
                $leftMatches = [regex]::Matches($fixture.lines[$i], '[A-Za-z_][A-Za-z0-9_]{2,20}')
                $rightMatches = [regex]::Matches($fixture.lines[$i + 1], '[A-Za-z_][A-Za-z0-9_]{2,20}')
                if ($leftMatches.Count -gt 0 -and $rightMatches.Count -gt 0) {
                    $pair = [pscustomobject]@{
                        left = $leftMatches[$leftMatches.Count - 1].Value
                        right = $rightMatches[0].Value
                        line1 = $i + 1
                        line2 = $i + 2
                    }
                    break
                }
            }
            if ($null -eq $pair) { continue }
        }
        if ($tokens.Count -eq 0 -and -not $NeedMultiline) { continue }
        return [pscustomobject]@{ fixture=$fixture; tokens=$tokens; pair=$pair }
    }
    return $null
}

function New-NoHitToken {
    param([int]$CaseId, [string]$Suffix = '')
    $bytes = New-Object byte[] 4
    $random.NextBytes($bytes)
    return ('YAGU_NO_HIT_{0}_{1}_{2}{3}' -f $seedValue, $CaseId, ([BitConverter]::ToString($bytes).Replace('-','')), $Suffix)
}

function New-SourceFixtureRecord {
    param($FixtureInfo, [int]$Line1 = 0, [int]$Line2 = 0)
    if ($null -eq $FixtureInfo) { return $null }
    return [pscustomobject][ordered]@{
        file = $FixtureInfo.fixture.path
        line1 = $Line1
        line2 = $Line2
    }
}

function New-CaseQuery {
    param([int]$CaseId, [string]$Scope)
    $needMultiline = $CaseId -ge 22 -and $CaseId -le 24
    $needMixed = $CaseId -eq 28
    $info = Get-FixtureForScope $Scope -NeedMultiline:$needMultiline -NeedMixedCase:$needMixed
    $tokens = if ($null -ne $info) { @($info.tokens) } else { @() }
    $rare = @($tokens | Where-Object { $_.Length -ge 10 })
    $common = @($tokens | Where-Object { $_.Length -ge 3 -and $_.Length -le 6 })
    $tokenA = if ($rare.Count -gt 0) { [string](Pick-One $rare) } elseif ($tokens.Count -gt 0) { [string](Pick-One $tokens) } else { New-NoHitToken $CaseId }
    $tokenBChoices = @($tokens | Where-Object { -not [string]::Equals($_, $tokenA, [StringComparison]::Ordinal) })
    $tokenB = if ($tokenBChoices.Count -gt 0) { [string](Pick-One $tokenBChoices) } else { New-NoHitToken $CaseId '_B' }
    $source = New-SourceFixtureRecord $info
    $family = ''
    $query = ''
    switch ($CaseId) {
        { $_ -ge 1 -and $_ -le 5 } {
            $family = 'distinctive-harvested-literal'; $query = $tokenA; break
        }
        { $_ -ge 6 -and $_ -le 8 } {
            $family = 'common-broad-literal'
            $query = if ($common.Count -gt 0) { [string](Pick-One $common) } else { $tokenA.Substring(0, [Math]::Min(5, $tokenA.Length)) }
            break
        }
        { $_ -ge 9 -and $_ -le 11 } {
            $family = 'multi-term-literal'; $query = "$tokenA $tokenB"; break
        }
        { $_ -ge 12 -and $_ -le 15 } {
            $family = 'eligible-regex-fixed-literal'; $query = '\b' + [regex]::Escape($tokenA) + '\b'; break
        }
        16 {
            $family = 'regex-alternation'; $query = '(?:' + [regex]::Escape($tokenA) + '|' + [regex]::Escape($tokenB) + ')'; break
        }
        17 {
            $family = 'regex-anchor'; $query = '^.*' + [regex]::Escape($tokenA); break
        }
        18 {
            $family = 'regex-bounded-wildcard'; $query = [regex]::Escape($tokenA) + '.{0,40}' + [regex]::Escape($tokenB); break
        }
        19 { $family = 'regex-no-required-trigram-alpha'; $query = '[A-Za-z]{6,12}'; break }
        20 { $family = 'regex-no-required-trigram-phone'; $query = '\d{3}[- ]?\d{4}'; break }
        21 { $family = 'regex-no-required-trigram-short-words'; $query = '(?:\w{1,2}\s+){2}\w{1,2}'; break }
        { $_ -ge 22 -and $_ -le 24 } {
            $family = 'multiline-harvested-positive'
            if ($null -ne $info -and $null -ne $info.pair) {
                if ($CaseId -eq 24) {
                    $query = [regex]::Escape($info.pair.left) + '.*?' + [regex]::Escape($info.pair.right)
                } else {
                    $query = [regex]::Escape($info.pair.left) + '\r?\n[ \t]*' + [regex]::Escape($info.pair.right)
                }
                $source = New-SourceFixtureRecord $info $info.pair.line1 $info.pair.line2
            } else {
                $query = [regex]::Escape((New-NoHitToken $CaseId)) + '\r?\n' + [regex]::Escape((New-NoHitToken $CaseId '_B'))
            }
            break
        }
        25 {
            $family = 'multiline-negative-complex'; $query = [regex]::Escape((New-NoHitToken $CaseId)) + '[\s\S]{0,160}' + [regex]::Escape((New-NoHitToken $CaseId '_B')); $source = $null; break
        }
        26 {
            $family = 'multiline-negative-dotall'; $query = [regex]::Escape((New-NoHitToken $CaseId)) + '.*' + [regex]::Escape((New-NoHitToken $CaseId '_B')); $source = $null; break
        }
        27 {
            $family = 'short-literal-no-trigram'; $query = if ($random.Next(2) -eq 0) { 'a' } else { 'x' }; $source = $null; break
        }
        28 {
            $family = 'case-sensitive-mixed-case-identifier'; $query = $tokenA; break
        }
        29 {
            $family = 'seeded-no-hit-distinctive-literal'; $query = New-NoHitToken $CaseId; $source = $null; break
        }
    }
    return [pscustomobject][ordered]@{ family=$family; query=$query; source=$source }
}

function Get-RequestedFlags {
    param([int]$CaseId)
    $caseSensitiveOn = @(1,3,5,9,11,13,15,17,20,22,24,26,28) -contains $CaseId
    if ($CaseId -eq 29) { $caseSensitiveOn = $random.Next(2) -eq 0 }
    $regexOn = $CaseId -ge 12 -and $CaseId -le 26
    $multilineOn = $CaseId -ge 22 -and $CaseId -le 26
    $exactOn = @(1,3,5,6,8,28) -contains $CaseId
    if ($CaseId -eq 29) { $exactOn = $random.Next(2) -eq 0 }
    return [pscustomobject][ordered]@{
        caseSensitive = $caseSensitiveOn
        regex = $regexOn
        multiline = $multilineOn
        exact = $exactOn
        dotAll = @(24,26) -contains $CaseId
        exactApplicable = -not $regexOn
    }
}

function New-ScopePools {
    param([object[]]$HealthyRoots)
    $exact = @($HealthyRoots | ForEach-Object { Normalize-PathText ([string]$_) } | Where-Object { Test-Path -LiteralPath $_ })
    $desc = @(
        (Join-Path $repoRoot 'src\Yagu\Services\Index'),
        (Join-Path $repoRoot 'tests\Yagu.Tests\Index'),
        (Join-Path $repoRoot 'PLANS'),
        'D:\diskActivityMonitor', 'D:\installationSite', 'D:\hello', 'E:\Temp'
    ) | Where-Object { Test-Path -LiteralPath $_ -PathType Container }
    $unindexed = @(
        'F:\Temp',
        'G:\My Drive\Colab Notebooks',
        'G:\My Drive\Google AI Studio',
        'G:\My Drive\Visual Studio 2010'
    ) | Where-Object { Test-Path -LiteralPath $_ -PathType Container }
    $small = @(
        (Join-Path $repoRoot 'src\Yagu\Services\Index'),
        (Join-Path $repoRoot 'tests\Yagu.Tests\Index'),
        (Join-Path $repoRoot 'PLANS'),
        (Join-Path $repoRoot 'scripts'),
        (Join-Path $repoRoot 'src\yagu-core\src'),
        (Join-Path $repoRoot 'docs')
    ) | Where-Object { Test-Path -LiteralPath $_ -PathType Container }
    if ($exact.Count -eq 0) { throw 'No healthy exact indexed roots are available for the required scope distribution.' }
    if ($desc.Count -eq 0) { throw 'No covered descendant folders are available.' }
    if ($unindexed.Count -eq 0) { throw 'No unindexed folders are available.' }
    if ($small.Count -eq 0) { throw 'No small folders are available.' }
    return [pscustomobject]@{
        exact = @(Shuffle-Array $exact)
        descendant = @(Shuffle-Array $desc)
        unindexed = @(Shuffle-Array $unindexed)
        small = @(Shuffle-Array $small)
    }
}

function Get-ScopeClassForCase {
    param([int]$CaseId)
    if (@(1,9,12,29,30) -contains $CaseId) { return 'all-drives' }
    if (@(2,3,4,5,13,14,15) -contains $CaseId) { return 'exact-indexed-root' }
    if (@(6,7,8,16,17,18,20,21) -contains $CaseId) { return 'covered-descendant' }
    if (@(10,11,27,28) -contains $CaseId) { return 'unindexed-root-or-folder' }
    return 'small-folder'
}

function New-CaseManifest {
    param($Pools)
    $exactIndex = 0; $descIndex = 0; $unindexedIndex = 0; $smallIndex = 0
    $cases = New-Object Collections.ArrayList
    for ($id = 1; $id -le 29; $id++) {
        $class = Get-ScopeClassForCase $id
        switch ($class) {
            'all-drives' { $scope = '' }
            'exact-indexed-root' { $scope = [string]$Pools.exact[$exactIndex++ % $Pools.exact.Count] }
            'covered-descendant' { $scope = [string]$Pools.descendant[$descIndex++ % $Pools.descendant.Count] }
            'unindexed-root-or-folder' { $scope = [string]$Pools.unindexed[$unindexedIndex++ % $Pools.unindexed.Count] }
            'small-folder' { $scope = [string]$Pools.small[$smallIndex++ % $Pools.small.Count] }
        }
        $q = New-CaseQuery $id $scope
        $flags = Get-RequestedFlags $id
        [void]$cases.Add([pscustomobject][ordered]@{
            caseId=$id; seed=$seedValue; scopeClass=$class; scope=$scope
            requestedFlags=$flags; queryFamily=$q.family; query=$q.query
            sourceFixture=$q.source; repeatedFromCaseId=$null
        })
    }
    $repeatCandidates = @($cases | Where-Object {
        $_.caseId -le 18 -and $_.scopeClass -in @('exact-indexed-root','covered-descendant') -and
        $_.queryFamily -notmatch 'common-broad'
    })
    $sourceCase = Pick-One $repeatCandidates
    [void]$cases.Add([pscustomobject][ordered]@{
        caseId=30; seed=$seedValue; scopeClass='all-drives'; scope=''
        requestedFlags=$sourceCase.requestedFlags; queryFamily='repeat-consistency'; query=$sourceCase.query
        sourceFixture=$sourceCase.sourceFixture; repeatedFromCaseId=$sourceCase.caseId
    })
    return @($cases)
}

function Get-ReasonCode {
    param([string]$Reason, [long]$Gross, [long]$Rescued, [long]$Net, [string]$LogText)
    $r = if ($null -eq $Reason) { '' } else { $Reason }
    if ($r -match '(?i)disabled for this search|acceleration disabled') { return 'index-disabled' }
    if ($r -match '(?i)no trusted index|no usable|not built|no content index') { return 'no-usable-index' }
    if ($r -match '(?i)no required trigram|ineligible|non-printable|required trigram') { return 'query-ineligible' }
    if ($r -match '(?i)not selective|too many candidates|candidate') { return 'not-selective' }
    if ($r -match '(?i)Incomplete') { return 'freshness-incomplete' }
    if ($r -match '(?i)GapDetected') { return 'freshness-gap' }
    if ($r -match '(?i)JournalIdChanged') { return 'freshness-journal-reset' }
    if ($r -match '(?i)CheckpointInvalid') { return 'freshness-checkpoint-invalid' }
    if ($r -match '(?i)Unavailable|JournalUnavailable') { return 'freshness-unavailable' }
    if ($r -match '(?i)layer not fresh|JournalDiscontinuity') { return 'freshness-other' }
    if ($r -match '(?i)worker.*(open|start|protocol|budget|mapped)|query session|query-ready|map failure') { return 'worker-open-or-protocol' }
    if ($LogText -match '(?i)total spool replay|fail-safe|recovery spool|pruning pipeline.*fail') { return 'b1-fail-safe-replay' }
    if ($Gross -gt 0 -and $Rescued -ge $Gross -and $Net -eq 0) { return 'b1-fail-safe-replay' }
    if ($Net -eq 0 -and $Gross -ge 0) { return 'no-useful-pruning' }
    return 'unknown'
}

function Parse-IndexVerdicts {
    param([string]$Text, [string[]]$RequestedRoots, [object[]]$HealthyRoots)
    $evaluations = @{}
    foreach ($m in [regex]::Matches($Text, "Worker pruning evaluated '([^']+)': grossPruned=([0-9]+), rescued=([0-9]+), netPruned=([0-9]+), accelerated=(True|False)")) {
        $key = Normalize-PathText $m.Groups[1].Value
        $evaluations[$key.ToLowerInvariant()] = [pscustomobject]@{
            root=$key; gross=[long]$m.Groups[2].Value; rescued=[long]$m.Groups[3].Value; net=[long]$m.Groups[4].Value; accelerated=($m.Groups[5].Value -eq 'True')
        }
    }
    foreach ($m in [regex]::Matches($Text, "Content index evaluated '([^']+)': grossPruned=([0-9]+), rescued=([0-9]+), netPruned=([0-9]+), pruningDisabled=(True|False)")) {
        $key = Normalize-PathText $m.Groups[1].Value
        $evaluations[$key.ToLowerInvariant()] = [pscustomobject]@{
            root=$key; gross=[long]$m.Groups[2].Value; rescued=[long]$m.Groups[3].Value; net=[long]$m.Groups[4].Value; accelerated=([long]$m.Groups[4].Value -gt 0)
        }
    }
    $bypasses = @{}
    foreach ($line in [regex]::Split($Text, '\r?\n')) {
        $m = [regex]::Match($line, 'Worker pruning bypass for (.+?): (.+)\.$')
        if ($m.Success) {
            $key = Normalize-PathText $m.Groups[1].Value
            $bypasses[$key.ToLowerInvariant()] = $m.Groups[2].Value
            continue
        }
        $m = [regex]::Match($line, "Accelerator bypass for '([^']+)'(?: \(scope [^)]+\))?: (.+)\.$")
        if ($m.Success) {
            $key = Normalize-PathText $m.Groups[1].Value
            $bypasses[$key.ToLowerInvariant()] = $m.Groups[2].Value
        }
    }
    $rootRows = New-Object Collections.ArrayList
    foreach ($requested in $RequestedRoots) {
        $root = Normalize-PathText $requested
        $key = $root.ToLowerInvariant()
        $cover = Find-CoveringRoot $root $HealthyRoots
        if ($evaluations.ContainsKey($key)) {
            $e = $evaluations[$key]
            if ($e.net -gt 0 -and $e.accelerated) {
                [void]$rootRows.Add([pscustomobject][ordered]@{
                    root=$root; indexRoot=$cover; verdict='ACCELERATED'; reasonCode=$null; reason=$null
                    grossPruned=$e.gross; rescued=$e.rescued; netPruned=$e.net
                })
            } else {
                $reason = if ($bypasses.ContainsKey($key)) { [string]$bypasses[$key] } elseif ($e.gross -gt 0 -and $e.rescued -ge $e.gross) { 'B1 reconciliation rescued every provisional prune' } else { 'index opened but final net pruning was zero' }
                $code = Get-ReasonCode $reason $e.gross $e.rescued $e.net $Text
                [void]$rootRows.Add([pscustomobject][ordered]@{
                    root=$root; indexRoot=$cover; verdict='NOT ACCELERATED'; reasonCode=$code; reason=$reason
                    grossPruned=$e.gross; rescued=$e.rescued; netPruned=$e.net
                })
            }
        } elseif ($bypasses.ContainsKey($key)) {
            $reason = [string]$bypasses[$key]
            [void]$rootRows.Add([pscustomobject][ordered]@{
                root=$root; indexRoot=$cover; verdict='NOT ACCELERATED'; reasonCode=(Get-ReasonCode $reason 0 0 0 $Text); reason=$reason
                grossPruned=0; rescued=0; netPruned=0
            })
        } elseif ($null -eq $cover) {
            [void]$rootRows.Add([pscustomobject][ordered]@{
                root=$root; indexRoot=$null; verdict='NOT ACCELERATED'; reasonCode='no-usable-index'; reason='no usable covering index'
                grossPruned=0; rescued=0; netPruned=0
            })
        } else {
            [void]$rootRows.Add([pscustomobject][ordered]@{
                root=$root; indexRoot=$cover; verdict='NOT ACCELERATED'; reasonCode='unknown'; reason='no explicit final index decision was logged'
                grossPruned=0; rescued=0; netPruned=0
            })
        }
    }
    $acceleratedCount = @($rootRows | Where-Object verdict -eq 'ACCELERATED').Count
    $overall = if ($acceleratedCount -eq $rootRows.Count -and $rootRows.Count -gt 0) { 'FULLY ACCELERATED' }
        elseif ($acceleratedCount -gt 0) { 'PARTIALLY ACCELERATED' }
        else { 'NOT ACCELERATED' }
    return [pscustomobject]@{ overall=$overall; roots=@($rootRows) }
}

function Test-UiStatusAgreement {
    param([string]$Overall, [string]$Status)
    if ([string]::IsNullOrWhiteSpace($Status)) { return $false }
    switch ($Overall) {
        'FULLY ACCELERATED' { return $Status -match '(?i)full|accelerat' -and $Status -notmatch '(?i)partial|bypass|not accelerated' }
        'PARTIALLY ACCELERATED' { return $Status -match '(?i)partial' }
        default { return $Status -match '(?i)bypass|not accelerated|none|not built' }
    }
}

function Get-FilteredIndexLog {
    param([string]$Text)
    $patterns = 'Starting search #|Worker pruning active|Worker pruning bypass for|worker pruning could not start|pruning pipeline:|Worker pruning evaluated|Content index evaluated|Accelerator bypass|Accelerator accelerating|Incomplete|GapDetected|JournalIdChanged|CheckpointInvalid|Unavailable|query session|protocol|budget|Discovery finished|Search complete:|UIConsumer.*completed'
    return (@([regex]::Split($Text, '\r?\n') | Where-Object { $_ -match $patterns }) -join [Environment]::NewLine)
}

function Write-Summary {
    param($Preflight, [object[]]$CaseResults, [datetime]$EndedUtc, [object[]]$WerEvents, [bool]$Restored)
    $full = @($CaseResults | Where-Object overallVerdict -eq 'FULLY ACCELERATED').Count
    $partial = @($CaseResults | Where-Object overallVerdict -eq 'PARTIALLY ACCELERATED').Count
    $none = @($CaseResults | Where-Object overallVerdict -eq 'NOT ACCELERATED').Count
    $unknown = @($CaseResults | ForEach-Object roots | Where-Object reasonCode -eq 'unknown').Count
    $harnessFailures = @($CaseResults | Where-Object { $_.harnessWarnings.Count -gt 0 -and ($_.harnessWarnings -join ' ') -match '(?i)failure|timeout|mismatch' }).Count
    $allDefects = @($defects)
    $pass = $CaseResults.Count -eq 30 -and $unknown -eq 0 -and $allDefects.Count -eq 0 -and $WerEvents.Count -eq 0 -and $Restored
    $sb = New-Object Text.StringBuilder
    [void]$sb.AppendLine('# Index acceleration randomized UI campaign')
    [void]$sb.AppendLine()
    [void]$sb.AppendLine("- Result: **$(if($pass){'PASS'}else{'FAIL'})**")
    [void]$sb.AppendLine("- Executable: $($Preflight.executable.path)")
    [void]$sb.AppendLine("- SHA-256: $($Preflight.executable.sha256)")
    [void]$sb.AppendLine("- Binary timestamp: $($Preflight.executable.lastWriteUtc)")
    [void]$sb.AppendLine("- Seed: $seedValue")
    [void]$sb.AppendLine("- Started UTC: $($campaignStartUtc.ToString('o'))")
    [void]$sb.AppendLine("- Ended UTC: $($EndedUtc.ToString('o'))")
    [void]$sb.AppendLine("- Duration: $([Math]::Round(($EndedUtc-$campaignStartUtc).TotalMinutes,2)) minutes")
    [void]$sb.AppendLine("- Completed: $($CaseResults.Count)/30")
    [void]$sb.AppendLine("- Fully accelerated: $full; partially accelerated: $partial; not accelerated: $none")
    [void]$sb.AppendLine("- Harness failures: $harnessFailures; WER/Application crash events: $($WerEvents.Count)")
    [void]$sb.AppendLine("- Settings restored byte-for-byte: $Restored")
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('## Relevant settings')
    [void]$sb.AppendLine()
    foreach ($p in $Preflight.settings.PSObject.Properties) { [void]$sb.AppendLine("- $($p.Name): $($p.Value -join ', ')") }
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('## Drive and index inventory')
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('| Root | Type/format | Search all drives | Index status | Documents | Segments |')
    [void]$sb.AppendLine('|---|---|---:|---|---:|---:|')
    foreach ($d in $Preflight.drives) {
        $status = @($Preflight.indexInventory | Where-Object root -eq $d.root | Select-Object -First 1)
        $state = if ($status.Count -gt 0 -and $status[0].healthy) { 'healthy' } elseif ($status.Count -gt 0 -and $status[0].coveredBy) { "covered by $($status[0].coveredBy)" } else { 'none' }
        $docs = if ($status.Count -gt 0) { $status[0].documents } else { 0 }
        $segments = if ($status.Count -gt 0) { $status[0].segments } else { 0 }
        [void]$sb.AppendLine("| $($d.root) | $($d.driveType)/$($d.format) | $($d.inAllDrives) | $state | $docs | $segments |")
    }
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('## Cases')
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('| Case | Scope | Query/flags | Final verdict | Index result |')
    [void]$sb.AppendLine('|---:|---|---|---|---|')
    foreach ($r in $CaseResults) {
        $scopeText = if ([string]::IsNullOrWhiteSpace($r.scope)) { '(all drives)' } else { $r.scope }
        $queryText = ([string]$r.query).Replace('|','\|').Replace("`r",'').Replace("`n",'\n')
        if ($queryText.Length -gt 60) { $queryText = $queryText.Substring(0,57) + '...' }
        $flagText = "case=$($r.effectiveFlags.caseSensitive), regex=$($r.effectiveFlags.regex), multiline=$($r.effectiveFlags.multiline), exact=$($r.effectiveFlags.exact)"
        $accelerated = @($r.roots | Where-Object verdict -eq 'ACCELERATED')
        $notAccelerated = @($r.roots | Where-Object verdict -ne 'ACCELERATED')
        $detailParts = New-Object Collections.ArrayList
        foreach ($x in $accelerated) { [void]$detailParts.Add("$($x.root): net pruned $($x.netPruned)") }
        foreach ($x in $notAccelerated) { [void]$detailParts.Add("$($x.root): $($x.reason)") }
        [void]$sb.AppendLine("| $($r.caseId.ToString('00')) | $scopeText | $queryText ($flagText) | $($r.overallVerdict) | $((@($detailParts) -join '; ').Replace('|','\|')) |")
    }
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('## Non-acceleration reasons')
    [void]$sb.AppendLine()
    $reasonRows = @($CaseResults | ForEach-Object roots | Where-Object verdict -ne 'ACCELERATED' | Group-Object reasonCode | Sort-Object Count -Descending)
    if ($reasonRows.Count -eq 0) { [void]$sb.AppendLine('- None') }
    else { foreach ($g in $reasonRows) { [void]$sb.AppendLine("- $($g.Name): $($g.Count)") } }
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('## Defects')
    [void]$sb.AppendLine()
    if ($allDefects.Count -eq 0) { [void]$sb.AppendLine('- None') }
    else { foreach ($d in $allDefects) { [void]$sb.AppendLine("- $d") } }
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('## Notes')
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('- Logs are authoritative; B0 accelerating callbacks alone were not counted.')
    [void]$sb.AppendLine('- Broad and multiline query families were assigned to bounded descendant/small-folder scopes; case contents and concrete paths remain seed-reproducible.')
    [void]$sb.AppendLine('- Expected planner, selectivity, freshness, and no-index bypasses are not defects when explicit and safely live-scanned.')
    [IO.File]::WriteAllText($summaryPath, $sb.ToString(), $utf8NoBom)
    return $pass
}

function Close-CampaignProcess {
    if ($null -eq $campaignProcess) { return }
    try {
        if (-not $campaignProcess.HasExited -and $null -ne $mainWindow) {
            # If a watchdog/harness failure stops the campaign mid-search, cancel that exact search and
            # let its async finally release lifecycle state before closing the window. Closing directly
            # while cancellation is unwinding used to expose a product ObjectDisposedException race.
            if ((Get-SearchLabel) -eq 'Cancel') {
                $cancelButton = Find-UiById $mainWindow 'SearchCancelButton' 2
                if ($cancelButton) { [void](Invoke-UiElement $cancelButton) }
                $cancelDeadline = (Get-Date).AddSeconds(30)
                while ((Get-Date) -lt $cancelDeadline -and (Get-SearchLabel) -eq 'Cancel') {
                    Start-Sleep -Milliseconds 250
                }
            }
            try {
                $wp = $mainWindow.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
                $wp.Close()
            } catch { }
        }
    } catch { }
    $deadline = (Get-Date).AddSeconds(12)
    while ((Get-Date) -lt $deadline) {
        try { $campaignProcess.Refresh(); if ($campaignProcess.HasExited) { return } } catch { return }
        Start-Sleep -Milliseconds 300
    }
    if (Test-ExactProcessIdentity $campaignProcess.Id $campaignProcessStartTime) {
        $diag = "Campaign-owned Debug Yagu did not close through UI; targeted PID stop used for PID $($campaignProcess.Id)."
        [void]$defects.Add($diag)
        Stop-Process -Id $campaignProcess.Id -Force -ErrorAction SilentlyContinue
    }
}

function Restore-Settings {
    if ($null -eq $settingsOriginalBytes) { return }
    $dir = Split-Path -Parent $settingsPath
    if (-not (Test-Path $dir)) { [void](New-Item -ItemType Directory -Path $dir -Force) }
    [IO.File]::WriteAllBytes($settingsPath, $settingsOriginalBytes)
    $after = [IO.File]::ReadAllBytes($settingsPath)
    $settingsRestored = $after.Length -eq $settingsOriginalBytes.Length
    if ($settingsRestored) {
        for ($i = 0; $i -lt $after.Length; $i++) {
            if ($after[$i] -ne $settingsOriginalBytes[$i]) { $settingsRestored = $false; break }
        }
    }
    $script:settingsRestored = $settingsRestored
}

# UI Automation is loaded only for the execution phase so PrepareOnly also works in non-interactive shells.
function Initialize-UiAutomation {
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class IndexCampaignNative {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, uint data, IntPtr extra);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int command);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    public static void Click(int x, int y) {
        SetCursorPos(x, y);
        mouse_event(0x0002, 0, 0, 0, IntPtr.Zero);
        mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);
    }
    public static void Hover(int x, int y) { SetCursorPos(x, y); }
    public static void Activate(IntPtr hWnd) { ShowWindow(hWnd, 3); BringWindowToTop(hWnd); SetForegroundWindow(hWnd); }
}
"@
    $script:AE = [System.Windows.Automation.AutomationElement]
    $script:TS = [System.Windows.Automation.TreeScope]
    $script:CT = [System.Windows.Automation.ControlType]
    $script:PC = [System.Windows.Automation.PropertyCondition]
}

function Find-UiById {
    param($Parent, [string]$Id, [int]$TimeoutSeconds = 10, [switch]$IncludeSelf)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            $scope = if ($IncludeSelf) { $TS::Subtree } else { $TS::Descendants }
            $el = $Parent.FindFirst($scope, ($PC::new($AE::AutomationIdProperty, $Id)))
            if ($el) { return $el }
        } catch { }
        Start-Sleep -Milliseconds 200
    }
    return $null
}

function Find-UiByName {
    param($Parent, [string]$Name, [int]$TimeoutSeconds = 5)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            $el = $Parent.FindFirst($TS::Descendants, ($PC::new($AE::NameProperty, $Name)))
            if ($el) { return $el }
        } catch { }
        Start-Sleep -Milliseconds 200
    }
    return $null
}

function Find-DesktopElementForProcess {
    param([string]$AutomationId, [string]$Name = '')
    $all = $AE::RootElement.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($el in $all) {
        try {
            if ($el.Current.ProcessId -ne $campaignProcess.Id) { continue }
            if (-not [string]::IsNullOrWhiteSpace($AutomationId) -and $el.Current.AutomationId -ne $AutomationId) { continue }
            if (-not [string]::IsNullOrWhiteSpace($Name) -and $el.Current.Name -ne $Name) { continue }
            return $el
        } catch { }
    }
    return $null
}

function Find-DesktopElementAnyProcess {
    param([string]$AutomationId, [string]$Name = '')
    try {
        $all = $AE::RootElement.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    }
    catch {
        # The desktop UIA tree can be invalidated while WinUI result rows/flyouts are created. Modal
        # polling is best-effort; a transient ElementNotAvailable must not abort a running search.
        return $null
    }
    foreach ($el in $all) {
        try {
            if (-not [string]::IsNullOrWhiteSpace($AutomationId) -and $el.Current.AutomationId -ne $AutomationId) { continue }
            if (-not [string]::IsNullOrWhiteSpace($Name) -and $el.Current.Name -ne $Name) { continue }
            return $el
        } catch { }
    }
    return $null
}

function Invoke-UiElement {
    param($Element)
    if ($null -eq $Element) { return $false }
    try { $Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); return $true } catch { }
    try {
        $r = $Element.Current.BoundingRectangle
        [IndexCampaignNative]::Click([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))
        return $true
    } catch { return $false }
}

function Get-InnerEdit {
    param($Container)
    if ($Container.Current.ControlType -eq $CT::Edit) { return $Container }
    return $Container.FindFirst($TS::Descendants, ($PC::new($AE::ControlTypeProperty, $CT::Edit)))
}

function Set-EditValue {
    param($Container, [string]$Value)
    $edit = Get-InnerEdit $Container
    if ($null -eq $edit) { throw "No inner Edit for $($Container.Current.AutomationId)." }
    $vp = $edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $vp.SetValue($Value)
    Start-Sleep -Milliseconds 180
    return $edit
}

function Get-EditValue {
    param($Container)
    $edit = Get-InnerEdit $Container
    if ($null -eq $edit) { return $null }
    return $edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
}

function Get-ToggleStateBool {
    param($Element)
    $tp = $Element.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    return $tp.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On
}

function Set-ToggleStateBool {
    param($Element, [bool]$Desired, [string]$Name)
    for ($attempt = 0; $attempt -lt 3; $attempt++) {
        $current = Get-ToggleStateBool $Element
        if ($current -eq $Desired) { return }
        $Element.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
        Start-Sleep -Milliseconds 280
    }
    throw "Could not set $Name to $Desired."
}

function Test-UiVisible {
    param($Element)
    if ($null -eq $Element) { return $false }
    try {
        $r = $Element.Current.BoundingRectangle
        return -not $Element.Current.IsOffscreen -and $r.Width -gt 0 -and $r.Height -gt 0
    } catch { return $false }
}

function Get-ProcessWindows {
    $windows = $AE::RootElement.FindAll($TS::Children, ($PC::new($AE::ControlTypeProperty, $CT::Window)))
    $result = New-Object Collections.ArrayList
    foreach ($w in $windows) {
        try { if ($w.Current.ProcessId -eq $campaignProcess.Id) { [void]$result.Add($w) } } catch { }
    }
    return @($result)
}

function Handle-KnownModals {
    param([switch]$Startup)
    $handled = 0

    # Title-bar-less WinUI owned windows do not always surface as ControlType.Window children in UIA.
    # Prefer the unique action button for the readiness prompt so a non-mutating campaign can continue
    # even when the popup window itself has no accessible title/name.
    $readinessSearchLive = Find-DesktopElementAnyProcess '' 'Search live'
    if ($readinessSearchLive -and (Test-UiVisible $readinessSearchLive)) {
        [void](Invoke-UiElement $readinessSearchLive)
        [void]$startupChoices.Add([pscustomobject]@{ utc=[DateTime]::UtcNow.ToString('o'); title='Content index needs attention'; choice='Search live' })
        $handled++
        Start-Sleep -Milliseconds 500
    }

    foreach ($w in Get-ProcessWindows) {
        try {
            if ($null -ne $mainWindow -and $w.Current.NativeWindowHandle -eq $mainWindow.Current.NativeWindowHandle) { continue }
            $title = $w.Current.Name
            $choices = switch -Regex ($title) {
                '^Content index is warming up$' { @('Proceed with search'); break }
                '^HDD detected' { @('Continue search'); break }
                '^Drive not indexed by Everything$' { @('Ignore for now'); break }
                '^Content index needs attention$' { @('Search live'); break }
                '^Excluded file type$' { @('Search anyway'); break }
                '^Very broad search pattern$' { @('Search anyway'); break }
                '^This looks like an AI search$' { @('Keep Traditional'); break }
                '^This looks like a multiline search$' { throw 'HARNESS FAILURE: multiline suggestion appeared although multiline state was explicitly set.' }
                '^Search canceled - low disk space$' { throw 'APP FAILURE: search canceled because of low disk space.' }
                default {
                    if ($Startup) { @('Not now','No thanks','Skip','Later','OK','Close','Got it','Continue') }
                    else { @() }
                }
            }
            $button = $null
            foreach ($choice in $choices) {
                $candidate = Find-UiByName $w $choice 1
                if ($candidate -and $candidate.Current.ControlType -eq $CT::Button) { $button = $candidate; break }
            }
            if ($button) {
                [void](Invoke-UiElement $button)
                [void]$startupChoices.Add([pscustomobject]@{ utc=[DateTime]::UtcNow.ToString('o'); title=$title; choice=$button.Current.Name })
                $handled++
                Start-Sleep -Milliseconds 350
            } elseif (-not $Startup) {
                throw "HARNESS FAILURE: unknown blocking Yagu window '$title'."
            }
        } catch { throw }
    }
    return $handled
}

function Ensure-TraditionalMode {
    $split = Find-UiById $mainWindow 'SearchSplitButton' 2
    if ($split -and (Test-UiVisible $split)) {
        [IndexCampaignNative]::Activate([IntPtr]$mainWindow.Current.NativeWindowHandle)
        $r = $split.Current.BoundingRectangle
        [IndexCampaignNative]::Click([int]($r.X + $r.Width - 12), [int]($r.Y + $r.Height / 2))
        Start-Sleep -Milliseconds 900
        # WinUI popup menu elements can report process id 0 even though the owning HWND belongs to
        # Yagu, so search the desktop by exact accessible name like the established screenshot driver.
        $traditional = Find-UiByName $AE::RootElement 'Traditional' 4
        if ($null -eq $traditional) {
            # The persisted mode is normally already Traditional. The per-case Starting-search log
            # remains the authoritative check (mode=0) and stops before accepting any wrong-mode case.
            return
        }
        [void](Invoke-UiElement $traditional)
        Start-Sleep -Milliseconds 400
    }
}

function Open-AdvancedOptions {
    $button = Find-UiById $mainWindow 'AdvancedOptionsToggle' 5
    if ($null -eq $button) { throw 'AdvancedOptionsToggle not found.' }
    [void](Invoke-UiElement $button)
    $dot = $null
    $deadline = (Get-Date).AddSeconds(5)
    while ((Get-Date) -lt $deadline -and $null -eq $dot) {
        $dot = Find-UiById $mainWindow 'MultilineDotAllToggle' 1
        if ($null -eq $dot) { $dot = Find-DesktopElementAnyProcess 'MultilineDotAllToggle' }
        if ($null -eq $dot) { Start-Sleep -Milliseconds 200 }
    }
    if ($null -eq $dot) {
        # InvokePattern occasionally focuses the WinUI Button without opening its Flyout after hours of
        # UIA activity. Retry with a physical center click, then search both the owned tree and desktop.
        try {
            $r = $button.Current.BoundingRectangle
            [IndexCampaignNative]::Click([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))
        } catch { }
        $deadline = (Get-Date).AddSeconds(5)
        while ((Get-Date) -lt $deadline -and $null -eq $dot) {
            $dot = Find-UiById $mainWindow 'MultilineDotAllToggle' 1
            if ($null -eq $dot) { $dot = Find-DesktopElementAnyProcess 'MultilineDotAllToggle' }
            if ($null -eq $dot) { Start-Sleep -Milliseconds 200 }
        }
    }
    if ($null -eq $dot) { throw 'MultilineDotAllToggle was not found after opening Advanced Options.' }
    return $dot
}

function Close-AdvancedOptions {
    $query = Find-UiById $mainWindow 'QueryBox' 3
    if ($query) {
        try {
            $r = $query.Current.BoundingRectangle
            [IndexCampaignNative]::Click([int]($r.X + 20), [int]($r.Y + 15))
            Start-Sleep -Milliseconds 250
        } catch { }
    }
}

function Read-LiveFeatureStates {
    $states = [ordered]@{ archive=$null; imageText=$null; pdfText=$null; useContentIndex=$null }
    try {
        $dot = Open-AdvancedOptions
        foreach ($entry in @(
            @('archive','Search archives'), @('imageText','Search image text'),
            @('pdfText','Search PDF text'), @('useContentIndex','Use content index'))) {
            $el = Find-DesktopElementAnyProcess '' $entry[1]
            if ($el) { $states[$entry[0]] = Get-ToggleStateBool $el }
        }
        Close-AdvancedOptions
    } catch { }
    return [pscustomobject]$states
}

function Apply-CaseUiState {
    param($Case)
    $caseToggle = Find-UiById $mainWindow 'CaseSensitiveToggle' 5
    $regexToggle = Find-UiById $mainWindow 'RegexToggle' 5
    $multiToggle = Find-UiById $mainWindow 'MultilineToggle' 5
    $exactToggle = Find-UiById $mainWindow 'ExactMatchToggle' 5
    foreach ($x in @($caseToggle,$regexToggle,$multiToggle,$exactToggle)) { if ($null -eq $x) { throw 'A required inline toggle was not found.' } }

    Set-ToggleStateBool $multiToggle $false 'Multiline'
    Set-ToggleStateBool $regexToggle ([bool]$Case.requestedFlags.regex) 'Regex'
    Set-ToggleStateBool $exactToggle ([bool]$Case.requestedFlags.exact) 'Exact'
    Set-ToggleStateBool $caseToggle ([bool]$Case.requestedFlags.caseSensitive) 'Match case'
    if ([bool]$Case.requestedFlags.multiline) {
        Set-ToggleStateBool $multiToggle $true 'Multiline'
        $dotToggle = Open-AdvancedOptions
        Set-ToggleStateBool $dotToggle ([bool]$Case.requestedFlags.dotAll) 'DotAll'
        Close-AdvancedOptions
    }

    $directoryBox = Find-UiById $mainWindow 'DirectoryBox' 5
    $queryBox = Find-UiById $mainWindow 'QueryBox' 5
    [void](Set-EditValue $directoryBox ([string]$Case.scope))
    [void](Set-EditValue $queryBox ([string]$Case.query))
    $directoryReadback = Get-EditValue $directoryBox
    $queryReadback = Get-EditValue $queryBox
    if (-not [string]::Equals($directoryReadback, [string]$Case.scope, [StringComparison]::Ordinal)) {
        throw "DirectoryBox readback mismatch: expected '$($Case.scope)', got '$directoryReadback'."
    }
    if (-not [string]::Equals($queryReadback, [string]$Case.query, [StringComparison]::Ordinal)) {
        throw "QueryBox readback mismatch: expected '$($Case.query)', got '$queryReadback'."
    }
    return [pscustomobject][ordered]@{
        caseSensitive = Get-ToggleStateBool $caseToggle
        regex = Get-ToggleStateBool $regexToggle
        multiline = Get-ToggleStateBool $multiToggle
        exact = Get-ToggleStateBool $exactToggle
        dotAll = if ([bool]$Case.requestedFlags.multiline) { [bool]$Case.requestedFlags.dotAll } else { $null }
        exactApplicable = -not (Get-ToggleStateBool $regexToggle)
        directory = $directoryReadback
        query = $queryReadback
    }
}

function Invoke-TraditionalSearch {
    $split = Find-UiById $mainWindow 'SearchSplitButton' 2
    if ($split -and (Test-UiVisible $split)) {
        if (-not (Invoke-UiElement $split)) { throw 'Could not invoke SearchSplitButton.' }
        return
    }
    $button = Find-UiById $mainWindow 'SearchCancelButton' 3
    if ($null -eq $button -or -not (Invoke-UiElement $button)) { throw 'Could not invoke SearchCancelButton.' }
}

function Get-SearchLabel {
    $label = Find-UiById $mainWindow 'SearchCancelLabel' 2
    if ($null -eq $label) { return '' }
    try { return $label.Current.Name } catch { return '' }
}

function Capture-IndexUiState {
    $statusEl = Find-UiById $mainWindow 'IndexStatusTextBlock' 3
    $indicator = Find-UiById $mainWindow 'IndexStatusIndicator' 3
    $status = if ($statusEl) { $statusEl.Current.Name } else { '' }
    $details = if ($indicator) { $indicator.Current.HelpText } else { '' }
    $repairVisible = $false; $repairLabel = ''; $settingsVisible = $false
    if ($indicator -and (Test-UiVisible $indicator)) {
        $r = $indicator.Current.BoundingRectangle
        [IndexCampaignNative]::Hover([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))
        Start-Sleep -Milliseconds 850
        $repair = Find-DesktopElementAnyProcess 'IndexStatusRepairButton'
        if ($repair -and (Test-UiVisible $repair)) {
            $repairVisible = $true
            $repairText = Find-DesktopElementAnyProcess 'IndexStatusRepairButtonText'
            if ($repairText) { $repairLabel = $repairText.Current.Name } else { $repairLabel = $repair.Current.Name }
        }
        $settingsButton = Find-DesktopElementAnyProcess '' 'Indexing settings'
        $settingsVisible = $settingsButton -and (Test-UiVisible $settingsButton)
        # Move outside the flyout without relying on the main window's bounding rectangle. A WinUI
        # element can transiently report an Empty rectangle (Infinity coordinates) after a long,
        # layout-heavy search, which must not turn a completed case into a harness failure.
        [IndexCampaignNative]::Hover(0, 0)
        Start-Sleep -Milliseconds 500
    }
    return [pscustomobject][ordered]@{
        status=$status; details=$details; repairVisible=$repairVisible; repairLabel=$repairLabel; indexingSettingsVisible=[bool]$settingsVisible
    }
}

function Get-CaseTimeoutMinutes {
    param($Case)
    if ($Case.scopeClass -eq 'all-drives') { return $AllDrivesTimeoutMinutes }
    if ($Case.scopeClass -eq 'exact-indexed-root' -and ([IO.Path]::GetPathRoot($Case.scope) -eq $Case.scope)) { return $DriveTimeoutMinutes }
    return $FolderTimeoutMinutes
}

function Save-TimeoutDiagnostics {
    param($Case, [string]$LogText)
    $diag = [ordered]@{ caseId=$Case.caseId; utc=[DateTime]::UtcNow.ToString('o'); process=$null; workers=@(); wer=@() }
    try {
        $p = Get-Process -Id $campaignProcess.Id -ErrorAction Stop
        $diag.process = [ordered]@{ id=$p.Id; responding=$p.Responding; cpu=$p.CPU; workingSet=$p.WorkingSet64; privateBytes=$p.PrivateMemorySize64; path=$p.Path }
    } catch { }
    $diag.workers = @(Get-CimInstance Win32_Process -Filter "Name='Yagu.IndexWorker.exe'" -ErrorAction SilentlyContinue | Select-Object ProcessId,ParentProcessId,ExecutablePath,CommandLine)
    $diag.wer = @(Get-WerEvents $campaignStartUtc)
    Write-JsonFile (Join-Path $failuresDir ("case-{0:D2}-timeout.json" -f $Case.caseId)) $diag 20
    [IO.File]::WriteAllText((Join-Path $failuresDir ("case-{0:D2}-timeout.log" -f $Case.caseId)), $LogText, $utf8NoBom)
}

function Run-OneCase {
    param($Case, [string[]]$AllDriveRoots, [object[]]$HealthyRoots)
    $caseId = [int]$Case.caseId
    if ((Get-MaintenanceWorkers).Count -gt 0) { throw "HARNESS FAILURE: index maintenance worker active before case $caseId." }
    $idleDeadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $idleDeadline -and (Get-SearchLabel) -eq 'Cancel') { Start-Sleep -Milliseconds 300 }
    if ((Get-SearchLabel) -eq 'Cancel') { throw "HARNESS FAILURE: Yagu was not idle before case $caseId." }

    $effective = Apply-CaseUiState $Case
    $warnings = New-Object Collections.ArrayList
    foreach ($name in @('caseSensitive','regex','multiline','exact')) {
        if ([bool]$effective.$name -ne [bool]$Case.requestedFlags.$name) {
            [void]$warnings.Add("UI toggle mismatch for $name")
        }
    }
    if ($warnings.Count -gt 0) { throw "HARNESS FAILURE case ${caseId}: $($warnings -join '; ')." }

    $offset = Get-LogLength
    $startedUtc = [DateTime]::UtcNow
    $sw = [Diagnostics.Stopwatch]::StartNew()
    Invoke-TraditionalSearch

    $runId = $null
    $startDeadline = (Get-Date).AddSeconds(45)
    while ((Get-Date) -lt $startDeadline -and $null -eq $runId) {
        [void](Handle-KnownModals)
        $text = Read-LogFromOffset $offset
        $matches = [regex]::Matches($text, "Starting search #([0-9]+): query='([^']*)', dir='([^']*)', regex=(True|False), caseSensitive=(True|False), mode=([0-9]+)")
        if ($matches.Count -gt 0) {
            $m = $matches[$matches.Count - 1]
            $runId = [int]$m.Groups[1].Value
            if (-not [string]::Equals($m.Groups[2].Value, [string]$Case.query, [StringComparison]::Ordinal)) { throw "HARNESS FAILURE case ${caseId}: start-log query mismatch." }
            if ([bool]::Parse($m.Groups[4].Value) -ne [bool]$effective.regex) { throw "HARNESS FAILURE case ${caseId}: start-log regex mismatch." }
            if ([bool]::Parse($m.Groups[5].Value) -ne [bool]$effective.caseSensitive) { throw "HARNESS FAILURE case ${caseId}: start-log case mismatch." }
            if ([int]$m.Groups[6].Value -ne 0) { throw "HARNESS FAILURE case ${caseId}: search mode was not Traditional (mode=$($m.Groups[6].Value))." }
            if ($Case.scopeClass -eq 'all-drives') {
                if ($m.Groups[3].Value -notmatch '^<all drives: [0-9]+>$') { throw "HARNESS FAILURE case ${caseId}: all-drives start scope mismatch." }
            } elseif (-not [string]::Equals((Normalize-PathText $m.Groups[3].Value), (Normalize-PathText $Case.scope), [StringComparison]::OrdinalIgnoreCase)) {
                throw "HARNESS FAILURE case ${caseId}: start-log scope mismatch '$($m.Groups[3].Value)'."
            }
        }
        if ($null -eq $runId) { Start-Sleep -Milliseconds 300 }
    }
    if ($null -eq $runId) { throw "HARNESS FAILURE case ${caseId}: no Starting search line appeared." }
    Write-CampaignProgress ("Case {0:D2}/30 started (run #{1}, {2}, {3})" -f $caseId,$runId,$Case.scopeClass,$Case.queryFamily)

    $timeoutMinutes = Get-CaseTimeoutMinutes $Case
    $deadline = (Get-Date).AddMinutes($timeoutMinutes)
    $lastProgress = Get-Date
    $completed = $false
    while ((Get-Date) -lt $deadline -and -not $completed) {
        try { $campaignProcess.Refresh(); if ($campaignProcess.HasExited) { throw "APP FAILURE: Yagu exited during case $caseId." } } catch { throw }
        $text = Read-LogFromOffset $offset
        $completed = $text -match ("\[UIConsumer\] Search #" + $runId + " completed:")
        if (-not $completed) {
            if (((Get-Date) - $lastProgress).TotalSeconds -ge 15) {
                $lastProgress = Get-Date
                $p = Get-Process -Id $campaignProcess.Id -ErrorAction SilentlyContinue
                Write-CampaignProgress ("Case {0:D2} running {1:n1} min, UI={2}, WS={3:n0} MB" -f $caseId,$sw.Elapsed.TotalMinutes,(Get-SearchLabel),($(if($p){$p.WorkingSet64/1MB}else{0})))
            }
            Start-Sleep -Milliseconds 500
        }
    }
    if (-not $completed) {
        $text = Read-LogFromOffset $offset
        Save-TimeoutDiagnostics $Case $text
        throw "HARNESS/APP TIMEOUT: case $caseId exceeded $timeoutMinutes minute(s)."
    }
    $sw.Stop()
    Start-Sleep -Seconds 3
    $idleDeadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $idleDeadline -and (Get-SearchLabel) -eq 'Cancel') { Start-Sleep -Milliseconds 250 }
    if ((Get-SearchLabel) -eq 'Cancel') { [void]$warnings.Add('UI did not return to idle after completion event') }

    $fullText = Read-LogFromOffset $offset
    $fullLogPath = Join-Path $logsDir ("case-{0:D2}-full.log" -f $caseId)
    $indexLogPath = Join-Path $logsDir ("case-{0:D2}-index.log" -f $caseId)
    [IO.File]::WriteAllText($fullLogPath, $fullText, $utf8NoBom)
    [IO.File]::WriteAllText($indexLogPath, (Get-FilteredIndexLog $fullText), $utf8NoBom)

    $requestedRoots = if ($Case.scopeClass -eq 'all-drives') { @($AllDriveRoots) } else { @([string]$Case.scope) }
    $verdicts = Parse-IndexVerdicts $fullText $requestedRoots $HealthyRoots
    $ui = Capture-IndexUiState
    if (-not (Test-UiStatusAgreement $verdicts.overall $ui.status)) {
        $message = "STATUS UI MISMATCH case ${caseId}: logs=$($verdicts.overall), UI='$($ui.status)'"
        [void]$warnings.Add($message); [void]$defects.Add($message)
    }
    $freshnessRoots = @($verdicts.roots | Where-Object { $_.reasonCode -like 'freshness-*' })
    $shapeRoots = @($verdicts.roots | Where-Object { $_.reasonCode -in @('query-ineligible','not-selective') })
    if ($freshnessRoots.Count -gt 0 -and -not $ui.repairVisible) {
        $message = "Repair button missing for freshness failure in case $caseId ($($freshnessRoots.root -join ', '))."
        [void]$warnings.Add($message); [void]$defects.Add($message)
    }
    if ($shapeRoots.Count -gt 0 -and $ui.repairVisible) {
        $message = "Repair button incorrectly visible for query-shape/selectivity bypass in case $caseId."
        [void]$warnings.Add($message); [void]$defects.Add($message)
    }
    if ($ui.repairVisible -and -not $ui.indexingSettingsVisible) {
        $message = "Indexing settings action missing from status hover in case $caseId."
        [void]$warnings.Add($message); [void]$defects.Add($message)
    }
    foreach ($rootVerdict in $verdicts.roots) {
        if ($rootVerdict.reasonCode -eq 'unknown') {
            $message = "Missing explicit bypass reason for $($rootVerdict.root) in case $caseId."
            [void]$warnings.Add($message); [void]$defects.Add($message)
        }
        if ($rootVerdict.verdict -eq 'ACCELERATED' -and $rootVerdict.netPruned -le 0) {
            $message = "Invalid accelerated verdict with non-positive net pruning for $($rootVerdict.root) in case $caseId."
            [void]$warnings.Add($message); [void]$defects.Add($message)
        }
    }

    $completion = [regex]::Match($fullText, "\[UIConsumer\] Search #" + $runId + " completed:.*?uiMatches=([0-9,]+), groups=([0-9,]+)")
    $matchesCount = if ($completion.Success) { [long]$completion.Groups[1].Value.Replace(',','') } else { 0L }
    $filesCount = if ($completion.Success) { [long]$completion.Groups[2].Value.Replace(',','') } else { 0L }
    $completedUtc = [DateTime]::UtcNow
    $record = [pscustomobject][ordered]@{
        caseId=$caseId; seed=$seedValue; runId=$runId; scopeClass=$Case.scopeClass; scope=$Case.scope
        requestedFlags=$Case.requestedFlags; effectiveFlags=$effective; queryFamily=$Case.queryFamily; query=$Case.query
        sourceFixture=$Case.sourceFixture; repeatedFromCaseId=$Case.repeatedFromCaseId
        startedUtc=$startedUtc.ToString('o'); completedUtc=$completedUtc.ToString('o'); durationSeconds=[Math]::Round($sw.Elapsed.TotalSeconds,3)
        matches=$matchesCount; filesWithMatches=$filesCount
        uiIndexStatus=$ui.status; uiIndexDetails=$ui.details; uiHover=$ui
        overallVerdict=$verdicts.overall; roots=$verdicts.roots; harnessWarnings=@($warnings)
    }
    Write-JsonFile (Join-Path $uiaDir ("case-{0:D2}.json" -f $caseId)) ([pscustomobject]@{ effectiveFlags=$effective; index=$ui }) 20
    Append-JsonLine $resultsPath $record
    Write-CampaignProgress ("Case {0:D2} complete in {1:n1}s: {2}; UI='{3}'" -f $caseId,$sw.Elapsed.TotalSeconds,$verdicts.overall,$ui.status)
    return $record
}

# Main execution.
try {
    if (-not (Test-Path -LiteralPath $ExePath -PathType Leaf)) { throw "Debug executable not found: $ExePath" }
    if (-not (Test-Path -LiteralPath $settingsPath -PathType Leaf)) { throw "Settings file not found: $settingsPath" }
    foreach ($dir in @($OutputDirectory,$logsDir,$uiaDir,$failuresDir)) { [void](New-Item -ItemType Directory -Path $dir -Force) }
    [IO.File]::WriteAllText((Join-Path $OutputDirectory 'seed.txt'), $seedValue.ToString([Globalization.CultureInfo]::InvariantCulture) + [Environment]::NewLine, $utf8NoBom)
    if ($StartCaseId -eq 1) {
        if (Test-Path $resultsPath) { Remove-Item $resultsPath -Force }
    }
    else {
        if (-not (Test-Path $resultsPath -PathType Leaf)) {
            throw "Cannot resume at case $StartCaseId because results.jsonl does not exist in $OutputDirectory."
        }
        foreach ($line in [IO.File]::ReadLines($resultsPath)) {
            if ([string]::IsNullOrWhiteSpace($line)) { continue }
            $existingResult = $line | ConvertFrom-Json
            if ([int]$existingResult.caseId -lt $StartCaseId) {
                [void]$results.Add($existingResult)
            }
        }
        if ($results.Count -ne ($StartCaseId - 1)) {
            throw "Cannot resume at case ${StartCaseId}: found $($results.Count) prior case result(s), expected $($StartCaseId - 1)."
        }
    }

    $settingsOriginalBytes = [IO.File]::ReadAllBytes($settingsPath)
    [IO.File]::WriteAllBytes((Join-Path $OutputDirectory 'settings.snapshot.json'), $settingsOriginalBytes)
    $settingsRaw = [Text.Encoding]::UTF8.GetString($settingsOriginalBytes)
    $settings = $settingsRaw | ConvertFrom-Json

    $driveRows = New-Object Collections.ArrayList
    $allDriveRoots = New-Object Collections.ArrayList
    foreach ($drive in [IO.DriveInfo]::GetDrives()) {
        $ready = $false
        try { $ready = $drive.IsReady } catch { }
        if (-not $ready) { continue }
        $cloud = Test-LikelyCloudDrive $drive
        $inAll = (-not $cloud -and $drive.DriveType -eq [IO.DriveType]::Fixed) -or
            ($cloud -and [bool](Get-SettingValue $settings 'SearchAllDrivesIncludesCloud' $false)) -or
            ($drive.DriveType -eq [IO.DriveType]::Network -and [bool](Get-SettingValue $settings 'SearchAllDrivesIncludesNetwork' $false)) -or
            ($drive.DriveType -eq [IO.DriveType]::Removable -and [bool](Get-SettingValue $settings 'SearchAllDrivesIncludesRemovable' $false))
        if ($inAll) { [void]$allDriveRoots.Add((Normalize-PathText $drive.Name)) }
        [void]$driveRows.Add([pscustomobject][ordered]@{
            root=Normalize-PathText $drive.Name; driveType=$drive.DriveType.ToString(); format=$drive.DriveFormat
            volumeLabel=$drive.VolumeLabel; ready=$ready; cloud=$cloud; inAllDrives=$inAll
            totalBytes=[long]$drive.TotalSize; freeBytes=[long]$drive.AvailableFreeSpace
        })
    }
    if ($allDriveRoots.Count -eq 0) { throw 'No ready roots are eligible for all-drives search.' }

    $inventory = New-Object Collections.ArrayList
    $statusRoots = @($driveRows.root + @(Get-SettingValue $settings 'IndexedRoots' @()) | Select-Object -Unique)
    foreach ($root in $statusRoots) { [void]$inventory.Add((Invoke-IndexStatus ([string]$root))) }
    $healthyRoots = @($inventory | Where-Object healthy | ForEach-Object root)
    $pools = New-ScopePools $healthyRoots
    if (-not [string]::IsNullOrWhiteSpace($ManifestPath)) {
        $ManifestPath = [IO.Path]::GetFullPath($ManifestPath)
        if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) { throw "Manifest not found: $ManifestPath" }
        $manifestJson = [IO.File]::ReadAllText($ManifestPath)
        # Windows PowerShell 5.1 can preserve a top-level JSON array as one pipeline object when the
        # conversion sits directly inside @(...). Assign first so array enumeration/count is retained.
        $parsedManifest = $manifestJson | ConvertFrom-Json
        $cases = @($parsedManifest)
        if ($cases.Count -ne $Count) { throw "Manifest contains $($cases.Count) cases; expected $Count." }
        foreach ($case in $cases) {
            if ([int]$case.seed -ne $seedValue) { throw "Manifest seed $($case.seed) does not match requested seed $seedValue." }
        }
        [IO.File]::WriteAllText($casesPath, $manifestJson, $utf8NoBom)
    }
    else {
        $cases = New-CaseManifest $pools
        Write-JsonFile $casesPath $cases 30
    }

    $relevantNames = @(
        'EnableContentIndex','UseContentIndexByDefault','IndexUseWorkerQuerySessions','IndexUseNativeWorker',
        'IndexMaxCandidatePercent','IndexQueryStartupBudgetMs','IndexMaxJournalCatchupRecords','IndexMaxJournalCatchupMB',
        'IndexUpdateMode','IndexBuildTrigger','IndexAutoRepair','IndexUseWatcherHints','IndexMaxWorkerQuerySizeMB',
        'IndexedRoots','IndexAccelerateLiterals','IndexAccelerateWholeWord','IndexAccelerateRegex','IndexAccelerateMultiline',
        'SearchImageText','SearchPdfText','SearchAllDrivesIncludesNetwork','SearchAllDrivesIncludesRemovable',
        'SearchAllDrivesIncludesCloud','SearchAllDrivesForceFullScan','MaxResults','AbsoluteMaxResults','LogLevelIndex','CloseToTray'
    )
    $settingsSnapshot = [ordered]@{}
    foreach ($name in $relevantNames) { $settingsSnapshot[$name] = Get-SettingValue $settings $name }

    $indexRoot = [string](Get-SettingValue $settings 'IndexStorageDirectory' '')
    if ([string]::IsNullOrWhiteSpace($indexRoot)) { $indexRoot = Join-Path $env:LOCALAPPDATA 'Yagu\content-index' }
    $preflight = [pscustomobject][ordered]@{
        seed=$seedValue; preparedUtc=[DateTime]::UtcNow.ToString('o')
        executable=[ordered]@{
            path=$ExePath; sha256=(Get-FileHash -LiteralPath $ExePath -Algorithm SHA256).Hash
            lastWriteUtc=(Get-Item -LiteralPath $ExePath).LastWriteTimeUtc.ToString('o'); processId=$null
        }
        settings=[pscustomobject]$settingsSnapshot
        settingsFile=[ordered]@{ path=$settingsPath; length=$settingsOriginalBytes.Length; sha256=(Get-FileHash -LiteralPath $settingsPath -Algorithm SHA256).Hash }
        drives=@($driveRows); allDriveRoots=@($allDriveRoots); indexInventory=@($inventory)
        indexStorage=[ordered]@{ path=$indexRoot; sizeBytes=(Get-DirectorySizeBytes $indexRoot); exists=(Test-Path -LiteralPath $indexRoot) }
        scopePools=[ordered]@{ exact=$pools.exact; descendant=$pools.descendant; unindexed=$pools.unindexed; small=$pools.small }
        liveFeatureStates=$null; startupChoices=@(); initialLogLength=(Get-LogLength); initialLogLastWriteUtc=$(if(Test-Path $logPath){(Get-Item $logPath).LastWriteTimeUtc.ToString('o')}else{$null})
        maintenanceWorkers=@(Get-MaintenanceWorkers | Select-Object ProcessId,ParentProcessId,CommandLine)
        notes=@('CLI index status is metadata-only; no validate/rebuild/mutation was performed.','Broad/multiline cases use bounded folders to avoid unbounded result storms.')
    }
    Write-JsonFile $preflightPath $preflight 40
    if ($PrepareOnly) {
        Write-CampaignProgress "Prepared manifest only at $OutputDirectory"
        return
    }

    $existingYagu = Get-CampaignYaguProcesses
    if ($existingYagu.Count -gt 0) {
        throw ("A Yagu GUI process is already running; campaign requires an owned exact Debug process. PIDs: " + (($existingYagu | ForEach-Object ProcessId) -join ', '))
    }
    if ((Get-MaintenanceWorkers).Count -gt 0) { throw 'An index maintenance worker is active at preflight.' }

    # Temporary changes are limited to observability and deterministic normal exit. Original bytes are restored.
    Set-SettingValue $settings 'LogLevelIndex' 3
    Set-SettingValue $settings 'CloseToTray' $false
    [IO.File]::WriteAllText($settingsPath, ($settings | ConvertTo-Json -Depth 100), $utf8NoBom)

    Initialize-UiAutomation
    $campaignProcess = Start-Process -FilePath $ExePath -ArgumentList '--yagu-gui-child --window-mode traditional' -PassThru
    $campaignProcessStartTime = $campaignProcess.StartTime
    $deadline = (Get-Date).AddSeconds(35)
    while ((Get-Date) -lt $deadline -and $null -eq $mainWindow) {
        try {
            $condition = New-Object System.Windows.Automation.AndCondition(
                ($PC::new($AE::ProcessIdProperty, [int]$campaignProcess.Id)),
                ($PC::new($AE::ControlTypeProperty, $CT::Window)))
            $mainWindow = $AE::RootElement.FindFirst($TS::Children, $condition)
        } catch { }
        if ($null -eq $mainWindow) { Start-Sleep -Milliseconds 400 }
    }
    if ($null -eq $mainWindow) { throw 'Main Yagu window did not appear through UI Automation.' }
    if ($mainWindow.Current.ProcessId -ne $campaignProcess.Id) { throw 'UI Automation attached to the wrong process.' }
    [IndexCampaignNative]::Activate([IntPtr]$mainWindow.Current.NativeWindowHandle)
    Start-Sleep -Milliseconds 700
    for ($i = 0; $i -lt 20; $i++) {
        $handled = Handle-KnownModals -Startup
        if ($handled -eq 0) { break }
    }
    Ensure-TraditionalMode
    $liveStates = Read-LiveFeatureStates
    $preflight.executable.processId = $campaignProcess.Id
    $preflight.liveFeatureStates = $liveStates
    $preflight.startupChoices = @($startupChoices)
    Write-JsonFile $preflightPath $preflight 40

    foreach ($case in $cases | Where-Object { [int]$_.caseId -ge $StartCaseId }) {
        $record = Run-OneCase $case @($allDriveRoots) @($healthyRoots)
        [void]$results.Add($record)
    }

    Close-CampaignProcess
    Restore-Settings
    $campaignEndUtc = [DateTime]::UtcNow
    $wer = @(Get-WerEvents $campaignStartUtc)
    if ($wer.Count -gt 0) {
        Write-JsonFile (Join-Path $failuresDir 'wer-events.json') $wer 10
        [void]$defects.Add("$($wer.Count) Yagu-related WER/Application crash event(s) occurred during the campaign.")
    }
    $passed = Write-Summary $preflight @($results) $campaignEndUtc $wer $settingsRestored
    Write-CampaignProgress ("Campaign complete: {0}/30 cases, result={1}, output={2}" -f $results.Count,$(if($passed){'PASS'}else{'FAIL'}),$OutputDirectory)
}
catch {
    $campaignError = $_
    $campaignEndUtc = [DateTime]::UtcNow
    $message = $_.Exception.ToString()
    [IO.File]::WriteAllText((Join-Path $failuresDir 'campaign-error.txt'), $message, $utf8NoBom)
    Write-CampaignProgress ("CAMPAIGN STOPPED: " + $_.Exception.Message)
    throw
}
finally {
    try { Close-CampaignProcess } catch { }
    try { Restore-Settings } catch { }
}
