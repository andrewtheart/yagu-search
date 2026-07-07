---
description: "Yagu test-authoring conventions — source-pin vs unit tests, compiling engine source into Yagu.Tests, coverage runsettings, and the iterative filter. Use when: adding a test, writing a test, source-pin test, CS0103 in tests, new .cs not found in tests, code coverage, IncludeTestAssembly, runsettings, Category!=Slow, Category=GPU, GPU test, Slow test, test trait, benchmark test, Yagu.Tests, Yagu.Benchmarks."
applyTo: "tests/Yagu.Tests/**, tests/Yagu.Benchmarks/**"
---

# Yagu — Test Authoring

Also follow the **Test File Naming Rules** and **Test Run Rules** in
[copilot-instructions.md](../copilot-instructions.md) (name tests after the class under test;
default to the iterative `--filter "Category!=Slow&Category!=GPU&Category!=Headed"` run; don't kill long runs prematurely).

## Test categories

- `[Trait("Category", "Slow")]` — perf/UI/large-corpus tests that take minutes (excluded from the iterative run).
- `[Trait("Category", "GPU")]` — tests that need a GPU + a local Foundry model (e.g. `SemanticEvalGoldenTests`, which
  is tagged BOTH `Slow` and `GPU`).
- `[Trait("Category", "Headed")]` — tests that launch the WinUI app and drive it through UIAutomation
  (`MultilineGuiRegressionTests`; `MatchNavRegressionTests` is also `Slow`). They **self-gate at runtime**
  via `HeadedTestEnvironment.CanRun`: **auto-run** on an interactive Windows desktop and **auto-skip** in
  CI / non-interactive / non-Windows (GitHub Actions, Azure DevOps, GitLab, Jenkins are detected). So a
  normal `dotnet test` on a dev desktop exercises them; CI runs the same filter and they skip themselves —
  no `Category!=Headed` exclusion is needed in CI. Add `&Category!=Headed` only for the fast iterative
  loop, to keep a GUI window from popping up. Run only them with `--filter "Category=Headed"` (interactive
  Windows desktop, no other Yagu instance running — single-instance would hijack the launch).
  `MatchNavRegressionTests` additionally keeps a `YAGU_RUN_UI_REGRESSION=1` opt-in on top of the capability
  gate because it is screenshot-fragile.
- CI and headless machines use `--filter "Category!=Slow&Category!=GPU"`; headed tests self-skip there.
  The fast iterative dev loop uses `--filter "Category!=Slow&Category!=GPU&Category!=Headed"`.

## Two kinds of tests

- **Pure engine/helper code** (files under `src/Yagu/Services`, `src/Yagu/Helpers`, `src/Yagu/Models`,
  `src/Yagu/Native` with no WinUI/Foundry dependency) gets **real unit tests**. The production `.cs` is
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
[Yagu.Tests.csproj](../../tests/Yagu.Tests/Yagu.Tests.csproj), next to the existing includes.

## Coverage

Runtime coverage only exists for the compiled-in engine files, and requires `IncludeTestAssembly=true`
(the Yagu code lives inside `Yagu.Tests.dll`, so coverlet's default excludes it and reports 0%). Use
the collector + a runsettings file (e.g. `TestResults\ace-coverage.runsettings`), not
`coverlet.msbuild`:

```powershell
dotnet test tests/Yagu.Tests/Yagu.Tests.csproj -c Debug -p:RustProfile=profiling `
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

## Native fast path in tests

Search-engine unit and parity tests exercise the native `yagu_core.dll`. `Yagu.Tests` copies the
`RustProfile=profiling` build of that DLL, which can go **ABI-stale** after Rust changes: when its
`qg_abi_version()` no longer matches the C# expectation, `NativeSearcher.IsAvailable` is **false**, the
search falls back to the managed scanner, and native-path tests silently cover nothing (parity
comparisons become meaningless). If native coverage/parity looks wrong, rebuild it first:

```powershell
cargo build --profile profiling --manifest-path src/yagu-core/Cargo.toml
```

Also run the fast suite from a **non-elevated** shell — an elevated terminal caches admin state and
fails the `FileLister` "not elevated" admin-path tests.

## Benchmarks

The `[Trait("Category","Slow")]` throughput `[Fact]`s live in **Yagu.Benchmarks**, not Yagu.Tests.
`Yagu.Benchmarks` is a hybrid: `dotnet run --project Yagu.Benchmarks` runs BenchmarkDotNet;
`dotnet test Yagu.Benchmarks` runs the perf facts. It has no shared `GlobalUsings`, so test files
there need an explicit `using Xunit;`.

## Guards that must stay green

Telemetry ships **offline by default**: `TelemetryConfigTests` / `TelemetryGateTests` assert
`TelemetryConfig.IsConfigured == false`. If they fail, someone committed a real proxy URL/token into
`src/Yagu/Services/Telemetry/TelemetryConfig.cs` — restore the placeholder; inject real values only at
build/package time.
