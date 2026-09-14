# Verifies that architecture-specific staged payloads match the installer variant being built.
#
# The MCP server is published per-RID into a RID-specific folder and copied into the app's mcp-server
# directory. If that ever regresses, an x64 server would be staged into the x86 installer and would
# simply fail to start on 32-bit Windows -- a defect that a green build says nothing about, because
# nothing in the compile chain compares the two. So it is asserted here, before Inno compresses it.
#
# Deliberately does NOT call Set-StrictMode: this file is dot-sourced into build-installer.ps1, so any
# mode set here would apply to the rest of that script, which was neither written nor tested under it.

# PE machine types (IMAGE_FILE_HEADER.Machine).
$script:YaguPeMachineNames = @{
  0x014C = 'x86'
  0x8664 = 'x64'
  0xAA64 = 'arm64'
}

function Get-YaguPeMachine {
  <#
    .SYNOPSIS
      Returns the architecture ('x86', 'x64', 'arm64') recorded in a PE image's COFF header.
  #>
  param([Parameter(Mandatory)][string] $Path)

  $stream = [System.IO.File]::OpenRead($Path)
  try {
    $reader = New-Object System.IO.BinaryReader($stream)

    # MZ header -> e_lfanew at 0x3C points at the PE signature; Machine is 4 bytes past it.
    if ($reader.ReadUInt16() -ne 0x5A4D) { throw "Not a PE image: $Path" }
    [void]$stream.Seek(0x3C, [System.IO.SeekOrigin]::Begin)
    $peOffset = $reader.ReadUInt32()

    [void]$stream.Seek($peOffset, [System.IO.SeekOrigin]::Begin)
    if ($reader.ReadUInt32() -ne 0x00004550) { throw "Missing PE signature: $Path" }

    $machine = $reader.ReadUInt16()
    if ($script:YaguPeMachineNames.ContainsKey([int]$machine)) {
      return $script:YaguPeMachineNames[[int]$machine]
    }
    return ('unknown(0x{0:X4})' -f $machine)
  }
  finally { $stream.Dispose() }
}

function Assert-YaguStagedPayloadArchitecture {
  <#
    .SYNOPSIS
      Throws when a staged architecture-specific Yagu binary does not match the installer variant.
  #>
  param(
    [Parameter(Mandatory)][string] $StagingDir,
    [Parameter(Mandatory)][ValidateSet('x86', 'x64', 'arm64')][string] $Architecture
  )

  # Only Yagu's own per-RID binaries. Third-party payloads keep whatever architecture their vendor
  # shipped, and the x64-only OCR runtime is a deliberate exception documented in the OCR notes.
  $expected = @(
    @{ Path = 'Yagu.exe';                              Required = $true },
    @{ Path = 'mcp-server\Yagu.McpServer.exe';         Required = $true },
    @{ Path = 'index-worker\Yagu.IndexWorker.exe';     Required = $false }
  )

  foreach ($entry in $expected) {
    $full = Join-Path $StagingDir $entry.Path
    if (-not (Test-Path -LiteralPath $full)) {
      if ($entry.Required) { throw "Staged payload missing: $($entry.Path)" }
      continue
    }

    $actual = Get-YaguPeMachine -Path $full
    if ($actual -ne $Architecture) {
      throw ("Staged $($entry.Path) is $actual but this is the $Architecture installer. " +
             'It would fail to start on the target machine. Check the per-RID publish output.')
    }
    Write-Host "  arch OK: $($entry.Path) is $actual"
  }
}

function Assert-YaguStagedMcpServerPayload {
  <#
    .SYNOPSIS
      Throws when the staged MCP server is incomplete or older than the app it ships with.
    .DESCRIPTION
      The architecture check only reads one PE header, so it passes for a payload left behind by an
      EARLIER build: a failed child publish removes its output, but a previously staged copy under the
      publish directory survives, and -SkipBuild repackages it. A stale server shells out to a newer
      Yagu.exe and silently never receives later fixes, which a green build says nothing about.

      Freshness is measured against the server's own COMPILE INPUTS, not against Yagu.exe. Yagu.exe is
      relinked on every build by the version bump, while an unchanged Yagu.McpServer.exe is correctly
      left alone by incremental build, so comparing the two reports a stale payload for a server that
      is perfectly current.
  #>
  param(
    [Parameter(Mandatory)][string] $StagingDir,
    [string] $ProjectDir
  )

  $serverDir = Join-Path $StagingDir 'mcp-server'
  $serverExe = Join-Path $serverDir 'Yagu.McpServer.exe'

  # A self-contained publish is more than its launcher; without these the server cannot start at all.
  foreach ($required in @('Yagu.McpServer.dll', 'Yagu.McpServer.runtimeconfig.json')) {
    $path = Join-Path $serverDir $required
    if (-not (Test-Path -LiteralPath $path)) {
      throw "Staged MCP server is incomplete: mcp-server\$required is missing."
    }
  }

  # staging lives at <repo>\installer\staging, so the repo root is two levels up.
  if (-not $ProjectDir) {
    $repoRoot = Split-Path -Parent (Split-Path -Parent $StagingDir)
    $ProjectDir = Join-Path $repoRoot 'src\Yagu.McpServer'
  }

  $projectFile = Join-Path $ProjectDir 'Yagu.McpServer.csproj'
  if (-not (Test-Path -LiteralPath $projectFile)) {
    Write-Warning "Skipping MCP server freshness check: $projectFile not found."
    Write-Host "  payload OK: mcp-server is complete"
    return
  }

  # Every input the server compiles from: its own sources, the shared files it links in, and the
  # project file itself (a changed Compile list or dependency must also force a rebuild).
  $inputs = New-Object System.Collections.Generic.List[System.IO.FileInfo]
  $inputs.Add((Get-Item -LiteralPath $projectFile))
  Get-ChildItem -LiteralPath $ProjectDir -Recurse -Filter *.cs -File |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
    ForEach-Object { $inputs.Add($_) }
  foreach ($match in Select-String -LiteralPath $projectFile -Pattern '<Compile\s+Include="([^"]+)"') {
    $linked = Join-Path $ProjectDir $match.Matches[0].Groups[1].Value
    if (Test-Path -LiteralPath $linked) {
      $inputs.Add((Get-Item -LiteralPath $linked))
    }
  }

  $serverWritten = (Get-Item -LiteralPath $serverExe).LastWriteTimeUtc
  # Writing the publish output takes a moment after the last source read; allow for that skew only.
  $slack = [TimeSpan]::FromMinutes(2)
  $newer = @($inputs | Where-Object { ($_.LastWriteTimeUtc - $serverWritten) -gt $slack })
  if ($newer.Count -gt 0) {
    $sample = ($newer | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 3 |
               ForEach-Object { "$($_.Name) ($($_.LastWriteTimeUtc))" }) -join ', '
    throw ("Staged MCP server is stale: Yagu.McpServer.exe was written $serverWritten but " +
           "$($newer.Count) compile input(s) changed after it: $sample. " +
           'Rebuild the server, or clear installer\staging.')
  }

  Write-Host "  payload OK: mcp-server is complete and newer than all $($inputs.Count) compile inputs"
}
