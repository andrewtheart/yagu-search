<#
    remove-license-headers.ps1

    Removes the known GPL-3.0 notice variants currently present at the top of
    first-party .cs, .rs, and .xaml files. The script preserves UTF-8 BOM
    state and newline style, validates every transformation before writing,
    replaces files atomically, and rolls back all replacements if one fails.

    Usage:
        pwsh -File scripts/remove-license-headers.ps1 -DryRun
        pwsh -File scripts/remove-license-headers.ps1
#>
[CmdletBinding()]
param(
    [string]$Root = '',
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($Root)) {
    $Root = Split-Path -Parent $PSScriptRoot
}
$Root = [System.IO.Path]::GetFullPath($Root)

# Keep this script ASCII-only for Windows PowerShell 5.1 compatibility.
$emdash = [char]0x2014
$program = "Yagu $emdash Yet Another Grep Utility"
$holder = 'the Yagu author (github.com/andrewtheart)'
$year = '2025-2026'

$notice = @(
    $program,
    "Copyright (C) $year $holder",
    '',
    'This program is free software: you can redistribute it and/or modify',
    'it under the terms of the GNU General Public License as published by',
    'the Free Software Foundation, either version 3 of the License, or',
    '(at your option) any later version.',
    '',
    'This program is distributed in the hope that it will be useful,',
    'but WITHOUT ANY WARRANTY; without even the implied warranty of',
    'MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the',
    'GNU General Public License for more details.',
    '',
    'You should have received a copy of the GNU General Public License',
    'along with this program.  If not, see <https://www.gnu.org/licenses/>.'
)

# A small set of files carried older shortened forms of the same notice.
# Keep these variants exact; never remove an arbitrary leading comment.
$shortNotice = @($notice[0..6])
$shortNoticeWithoutOption = @($notice[0..5]) + @('any later version.')
$warrantyNotice = @($notice[0..9]) + @(
    'MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the',
    $notice[11]
)
$blockNoticeVariants = @($notice, $shortNotice, $shortNoticeWithoutOption, $warrantyNotice)

function Build-BlockComment([string[]]$Lines, [string]$NewLine) {
    $builder = "/*$NewLine"
    foreach ($line in $Lines) {
        $builder += if ($line -eq '') { " *$NewLine" } else { " * $line$NewLine" }
    }
    $builder + " */$NewLine"
}

function Build-XmlComment([string]$NewLine) {
    $builder = "<!--$NewLine"
    foreach ($line in $notice) {
        $builder += if ($line -eq '') { $NewLine } else { "  $line$NewLine" }
    }
    $builder + "-->$NewLine"
}

function Test-Excluded([string]$Path) {
    return ($Path -match '\\(bin|obj|node_modules)\\') -or
           ($Path -match '\\src\\vendor\\') -or
           ($Path -match '\\TestResults\\') -or
           ($Path -match '\.g\.cs$') -or
           ($Path -match '\.g\.i\.cs$') -or
           ($Path -match '\.Designer\.cs$') -or
           ($Path -match 'AssemblyInfo\.cs$') -or
           ($Path -match '\.AssemblyAttributes\.cs$') -or
           ($Path -match 'GlobalUsings') -or
           ($Path -match 'AppInfo\.g\.cs$')
}

function Get-ByteHash([byte[]]$Bytes) {
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        return [BitConverter]::ToString($sha256.ComputeHash($Bytes)).Replace('-', '')
    }
    finally {
        $sha256.Dispose()
    }
}

function ConvertTo-Utf8Bytes([string]$Text, [bool]$WithBom) {
    $encoding = New-Object System.Text.UTF8Encoding($false, $true)
    [byte[]]$body = $encoding.GetBytes($Text)
    if (-not $WithBom) {
        return $body
    }

    [byte[]]$result = New-Object byte[] ($body.Length + 3)
    $result[0] = 0xEF
    $result[1] = 0xBB
    $result[2] = 0xBF
    [Array]::Copy($body, 0, $result, 3, $body.Length)
    return $result
}

$targets = Get-ChildItem $Root -Recurse -File -Include '*.cs', '*.rs', '*.xaml' -ErrorAction Stop |
    Where-Object { -not (Test-Excluded $_.FullName) } |
    Sort-Object FullName

$strictUtf8 = New-Object System.Text.UTF8Encoding($false, $true)
$planned = New-Object System.Collections.Generic.List[object]
$mismatches = New-Object System.Collections.Generic.List[string]
$skipped = 0

foreach ($file in $targets) {
    [byte[]]$originalBytes = [System.IO.File]::ReadAllBytes($file.FullName)
    $hasBom = $originalBytes.Length -ge 3 -and
        $originalBytes[0] -eq 0xEF -and
        $originalBytes[1] -eq 0xBB -and
        $originalBytes[2] -eq 0xBF
    $offset = if ($hasBom) { 3 } else { 0 }
    $text = $strictUtf8.GetString($originalBytes, $offset, $originalBytes.Length - $offset)
    $newLine = if ($text.Contains("`r`n")) { "`r`n" } else { "`n" }

    $headerOffset = 0
    if ($file.Extension -eq '.xaml' -and $text.StartsWith('<?xml', [StringComparison]::Ordinal)) {
        $declarationEnd = $text.IndexOf('?>', [StringComparison]::Ordinal)
        if ($declarationEnd -lt 0) {
            if ($text.IndexOf($program, [StringComparison]::Ordinal) -ge 0) {
                $mismatches.Add($file.FullName)
            }
            else {
                $skipped++
            }
            continue
        }
        $headerOffset = $declarationEnd + 2 + $newLine.Length
    }

    $headers = if ($file.Extension -eq '.xaml') {
        @((Build-XmlComment $newLine))
    }
    else {
        @($blockNoticeVariants | ForEach-Object { Build-BlockComment -Lines $_ -NewLine $newLine })
    }

    $insertedSpan = $null
    if ($headerOffset -le $text.Length) {
        foreach ($header in $headers) {
            $candidateSpan = $header + $newLine
            if ($text.Substring($headerOffset).StartsWith($candidateSpan, [StringComparison]::Ordinal)) {
                $insertedSpan = $candidateSpan
                break
            }
        }
    }
    $hasExactHeader = $null -ne $insertedSpan
    if (-not $hasExactHeader) {
        $headLength = [Math]::Min(1200, $text.Length)
        if ($text.Substring(0, $headLength).IndexOf($program, [StringComparison]::Ordinal) -ge 0) {
            $mismatches.Add($file.FullName)
        }
        else {
            $skipped++
        }
        continue
    }

    $newText = $text.Remove($headerOffset, $insertedSpan.Length)
    $reconstructed = $newText.Insert($headerOffset, $insertedSpan)
    if (-not [string]::Equals($text, $reconstructed, [StringComparison]::Ordinal)) {
        throw "Preflight reconstruction failed for '$($file.FullName)'."
    }

    [byte[]]$newBytes = ConvertTo-Utf8Bytes $newText $hasBom
    $planned.Add([pscustomobject]@{
        Path = $file.FullName
        NewBytes = $newBytes
        OriginalHash = Get-ByteHash $originalBytes
        NewHash = Get-ByteHash $newBytes
        TempPath = $null
        BackupPath = $null
    })
}

if ($mismatches.Count -gt 0) {
    $details = $mismatches -join [Environment]::NewLine
    throw "Found notice-like headers that did not exactly match the generated form. No files were changed:$([Environment]::NewLine)$details"
}

if ($DryRun) {
    Write-Host "Would remove exact header from $($planned.Count) file(s); skipped $skipped without that header."
    return
}

$applied = New-Object System.Collections.Generic.List[object]
try {
    foreach ($change in $planned) {
        $token = [Guid]::NewGuid().ToString('N')
        $change.TempPath = "$($change.Path).yagu-header-$token.tmp"
        $change.BackupPath = "$($change.Path).yagu-header-$token.bak"

        [System.IO.File]::WriteAllBytes($change.TempPath, $change.NewBytes)
        $tempHash = Get-ByteHash ([System.IO.File]::ReadAllBytes($change.TempPath))
        if ($tempHash -ne $change.NewHash) {
            throw "Temporary-file verification failed for '$($change.Path)'."
        }

        [System.IO.File]::Replace($change.TempPath, $change.Path, $change.BackupPath, $true)
        $applied.Add($change)

        $backupHash = Get-ByteHash ([System.IO.File]::ReadAllBytes($change.BackupPath))
        $writtenHash = Get-ByteHash ([System.IO.File]::ReadAllBytes($change.Path))
        if ($backupHash -ne $change.OriginalHash -or $writtenHash -ne $change.NewHash) {
            throw "Atomic replacement verification failed for '$($change.Path)'."
        }
    }
}
catch {
    $failure = $_
    for ($index = $applied.Count - 1; $index -ge 0; $index--) {
        $change = $applied[$index]
        if (Test-Path -LiteralPath $change.BackupPath) {
            [System.IO.File]::Replace($change.BackupPath, $change.Path, $null, $true)
        }
    }
    foreach ($change in $planned) {
        if ($change.TempPath -and (Test-Path -LiteralPath $change.TempPath)) {
            Remove-Item -LiteralPath $change.TempPath -Force
        }
        if ($change.BackupPath -and (Test-Path -LiteralPath $change.BackupPath)) {
            Remove-Item -LiteralPath $change.BackupPath -Force
        }
    }
    throw $failure
}

foreach ($change in $applied) {
    Remove-Item -LiteralPath $change.BackupPath -Force
}

Write-Host "Removed exact header from $($applied.Count) file(s); skipped $skipped without that header."