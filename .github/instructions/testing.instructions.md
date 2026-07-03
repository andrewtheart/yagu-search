---
description: "Yagu test-authoring conventions — source-pin vs unit tests, compiling engine source into Yagu.Tests, coverage runsettings, and the iterative filter. Use when: adding a test, writing a test, source-pin test, CS0103 in tests, new .cs not found in tests, code coverage, IncludeTestAssembly, runsettings, Category!=Slow, benchmark test, Yagu.Tests, Yagu.Benchmarks."
applyTo: "Yagu.Tests/**, Yagu.Benchmarks/**"
---

# Yagu — Test Authoring

Also follow the **Test File Naming Rules** and **Test Run Rules** in
[copilot-instructions.md](../copilot-instructions.md) (name tests after the class under test;
default to the iterative `--filter "Category!=Slow"` run; don't kill long runs prematurely).

## Two kinds of tests

- **Pure engine/helper code** (files under `Yagu/Services`, `Yagu/Helpers`, `Yagu/Models`,
  `Yagu/Native` with no WinUI/Foundry dependency) gets **real unit tests**. The production `.cs` is
  `<Compile Include="..\Yagu\...">`'d directly into `Yagu.Tests`, so `internal` (and
  `private`→`internal`) members are callable from tests with no `InternalsVisibleTo`.
- **WinUI/Foundry-coupled code** — `MainViewModel`, every `MainWindow.*.cs`, `SettingsWindow`,
  dialogs, `FoundryLocalSemanticQueryTranslator`, `FoundryModelSelector`, `CliRunner` — is **not**
  compiled into Yagu.Tests, so it gets **source-pin tests**: read the `.cs` as a string and assert on
  substrings / ordering. Pin the actual `return "…"` / statement text (not a bare phrase that might
  also appear in a comment), and widen the scrape window if the anchor has comments before the token.

## Adding a source file a test needs

New `.cs` files are NOT auto-included. If a test references a new engine/helper type and fails with
**CS0103 "name does not exist"**, add a
`<Compile Include="..\Yagu\<path>\X.cs" Link="Source\<path>\X.cs" />` line to
[Yagu.Tests.csproj](../../Yagu.Tests/Yagu.Tests.csproj), next to the existing includes.

## Coverage

Runtime coverage only exists for the compiled-in engine files, and requires `IncludeTestAssembly=true`
(the Yagu code lives inside `Yagu.Tests.dll`, so coverlet's default excludes it and reports 0%). Use
the collector + a runsettings file (e.g. `TestResults\ace-coverage.runsettings`), not
`coverlet.msbuild`:

```powershell
dotnet test Yagu.Tests/Yagu.Tests.csproj -c Debug -p:RustProfile=profiling `
  --filter "FullyQualifiedName~X" --collect:"XPlat Code Coverage" `
  --settings TestResults\ace-coverage.runsettings --results-directory <dir>
```

Parse the cobertura via `[xml]` + `$cov.SelectNodes('//class')` (there are multiple `<package>`
nodes — `//class` reads them all; `$cov.coverage.packages.package.classes.class` only reads the
first). The VS Code coverage tool caches results across edits — don't trust it for an
edit→re-measure loop.

Don't chase 100% branch coverage on genuinely-unreachable defensive code (enum-exhaustive `default:`
arms, compound null-guards, `_suppressNotification` guards) — those need dead-branch refactors, not
contrived/flaky tests.

## Benchmarks

The `[Trait("Category","Slow")]` throughput `[Fact]`s live in **Yagu.Benchmarks**, not Yagu.Tests.
`Yagu.Benchmarks` is a hybrid: `dotnet run --project Yagu.Benchmarks` runs BenchmarkDotNet;
`dotnet test Yagu.Benchmarks` runs the perf facts. It has no shared `GlobalUsings`, so test files
there need an explicit `using Xunit;`.

## Guards that must stay green

Telemetry ships **offline by default**: `TelemetryConfigTests` / `TelemetryGateTests` assert
`TelemetryConfig.IsConfigured == false`. If they fail, someone committed a real proxy URL/token into
`Yagu/Services/Telemetry/TelemetryConfig.cs` — restore the placeholder; inject real values only at
build/package time.
