param(
    [string]$CoverageFile = "",
    [switch]$SkipTestRun
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$manifestPath = Join-Path $repoRoot "tests\Yagu.Tests\index-worker-migration-coverage-manifest.txt"
$runSettings = Join-Path $repoRoot "tests\Yagu.Tests\content-index-coverage.runsettings"
$testProject = Join-Path $repoRoot "tests\Yagu.Tests\Yagu.Tests.csproj"

if (-not (Test-Path $manifestPath)) {
    throw "Migration coverage manifest not found: $manifestPath"
}

if (-not $SkipTestRun) {
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $results = Join-Path $repoRoot ("TestResults\IndexWorkerCoverage\gate-" + $stamp)
    & dotnet test $testProject -c Debug -p:RustProfile=profiling `
        --filter "FullyQualifiedName~Yagu.Tests.Index" `
        --collect:"XPlat Code Coverage" `
        --settings $runSettings `
        --results-directory $results
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
    $generated = Get-ChildItem $results -Recurse -Filter coverage.cobertura.xml | Select-Object -First 1
    if ($null -eq $generated) {
        throw "The test run did not produce coverage.cobertura.xml."
    }
    $CoverageFile = $generated.FullName
}

if ([string]::IsNullOrWhiteSpace($CoverageFile) -or -not (Test-Path $CoverageFile)) {
    throw "Coverage file not found: $CoverageFile"
}

[xml]$report = Get-Content $CoverageFile -Raw
$coverageSourceRoot = @($report.coverage.sources.source)[0]
$manifest = Get-Content $manifestPath | Where-Object {
    -not [string]::IsNullOrWhiteSpace($_) -and -not $_.TrimStart().StartsWith("#")
}

$failed = $false
foreach ($relativePath in $manifest) {
    $absolutePath = [IO.Path]::GetFullPath((Join-Path $repoRoot ($relativePath -replace "/", "\")))
    if (-not (Test-Path $absolutePath)) {
        Write-Host "MISSING SOURCE: $relativePath" -ForegroundColor Red
        $failed = $true
        continue
    }

    $classes = @($report.SelectNodes("//class") | Where-Object {
        try {
            $reportedPath = if ([IO.Path]::IsPathRooted($_.filename)) {
                $_.filename
            } else {
                Join-Path $coverageSourceRoot $_.filename
            }
            [IO.Path]::GetFullPath($reportedPath) -eq $absolutePath
        } catch { $false }
    })
    if ($classes.Count -eq 0) {
        Write-Host "MISSING COVERAGE NODE: $relativePath" -ForegroundColor Red
        $failed = $true
        continue
    }

    $lines = @($classes | ForEach-Object { $_.lines.line })
    $methods = @($classes | ForEach-Object { $_.methods.method })
    if ($lines.Count -eq 0 -or $methods.Count -eq 0) {
        Write-Host "NO INSTRUMENTABLE CODE: $relativePath" -ForegroundColor Red
        $failed = $true
        continue
    }

    $lineCovered = @($lines | Where-Object { [int]$_.hits -gt 0 }).Count
    $branchCovered = 0
    $branchTotal = 0
    foreach ($line in $lines | Where-Object { $_.branch -eq "true" }) {
        if ($line."condition-coverage" -match "\((\d+)/(\d+)\)") {
            $branchCovered += [int]$Matches[1]
            $branchTotal += [int]$Matches[2]
        }
    }
    $methodCovered = @($methods | Where-Object {
        @($_.lines.line | Where-Object { [int]$_.hits -gt 0 }).Count -gt 0
    }).Count

    $uncoveredLines = @($lines | Where-Object { [int]$_.hits -eq 0 } | ForEach-Object { $_.number })
    $uncoveredMethods = @($methods | Where-Object {
        @($_.lines.line | Where-Object { [int]$_.hits -gt 0 }).Count -eq 0
    } | ForEach-Object { $_.name })

    $lineOk = $lineCovered -eq $lines.Count
    $branchOk = $branchCovered -eq $branchTotal
    $methodOk = $methodCovered -eq $methods.Count
    $color = if ($lineOk -and $branchOk -and $methodOk) { "Green" } else { "Red" }
    Write-Host ("{0}: lines {1}/{2}, branches {3}/{4}, functions {5}/{6}" -f `
        $relativePath, $lineCovered, $lines.Count, $branchCovered, $branchTotal, $methodCovered, $methods.Count) `
        -ForegroundColor $color

    if (-not $lineOk) { Write-Host ("  uncovered lines: " + ($uncoveredLines -join ", ")) -ForegroundColor Red }
    if (-not $methodOk) { Write-Host ("  uncovered functions: " + ($uncoveredMethods -join ", ")) -ForegroundColor Red }
    if (-not ($lineOk -and $branchOk -and $methodOk)) { $failed = $true }
}

Write-Host "Coverage artifact: $CoverageFile"
if ($failed) { exit 1 }
Write-Host "Index worker migration coverage gate passed: 100% lines, branches, and functions." -ForegroundColor Green
