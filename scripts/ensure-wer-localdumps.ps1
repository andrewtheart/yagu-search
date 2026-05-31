<#
.SYNOPSIS
    Ensures Yagu-specific Windows Error Reporting (WER) LocalDumps are configured so
    every native crash drops a full minidump into the repo's TestResults\CrashDumps
    folder. Invoked automatically on Debug builds of Yagu.csproj.

.NOTES
    Sets HKCU values so no admin elevation is required.
    Also creates the dump folder up front since WER will silently skip dump capture
    if the configured DumpFolder does not exist.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $DumpFolder,
    [string] $Image = 'Yagu.exe',
    [int] $DumpType = 2,   # 2 = full dump
    [int] $DumpCount = 10
)

$ErrorActionPreference = 'Stop'

try {
    if (-not (Test-Path -LiteralPath $DumpFolder)) {
        New-Item -ItemType Directory -Path $DumpFolder -Force | Out-Null
    }

    $base = 'HKCU:\Software\Microsoft\Windows\Windows Error Reporting\LocalDumps'
    if (-not (Test-Path -LiteralPath $base)) { New-Item -Path $base -Force | Out-Null }
    $key = Join-Path $base $Image
    if (-not (Test-Path -LiteralPath $key)) { New-Item -Path $key -Force | Out-Null }

    # ExpandString preserves the literal path; matches what `reg add /t REG_EXPAND_SZ` writes.
    New-ItemProperty -Path $key -Name 'DumpFolder' -PropertyType ExpandString -Value $DumpFolder -Force | Out-Null
    New-ItemProperty -Path $key -Name 'DumpType'   -PropertyType DWord       -Value $DumpType   -Force | Out-Null
    New-ItemProperty -Path $key -Name 'DumpCount'  -PropertyType DWord       -Value $DumpCount  -Force | Out-Null

    Write-Host "WER LocalDumps configured: $Image -> $DumpFolder (Type=$DumpType, Count=$DumpCount)"
}
catch {
    # Never fail the build over WER setup; just warn.
    Write-Warning "Failed to configure WER LocalDumps for ${Image}: $($_.Exception.Message)"
    exit 0
}
