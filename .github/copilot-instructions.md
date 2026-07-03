# Yagu — AI agent instructions

Yagu ("Yet Another Grep Utility") is a hyperfast Windows directory search tool: a native hybrid
**WinUI 3 desktop + CLI** app on **.NET 10 (Native AOT, unpackaged)**, with a **Rust** search-engine
DLL for the hot content-scanning path and optional voidtools **Everything** integration for file
discovery. See [README.md](../README.md) for the feature tour and build prerequisites, and
[HELP.md](../HELP.md) for user-facing behavior. Windows-only.

## Projects (Yagu.sln)

- **Yagu** — the app. Same binary is both the WinUI 3 GUI and the CLI; [Program.cs](../Yagu/Program.cs) / [App.xaml.cs](../Yagu/App.xaml.cs) dispatch to GUI vs [CliRunner.cs](../Yagu/CliRunner.cs) (CLI mode via `--cli`).
- **Yagu.Tests** — xUnit. It does **not** reference the Yagu project; instead it `<Compile Include="..\Yagu\...">`'s the engine source files **directly into the test assembly** so `internal` members are testable and coverage works (`IncludeTestAssembly=true`). Adding a new engine/helper `.cs` that a test needs requires adding a matching `<Compile Include>` line to [Yagu.Tests.csproj](../Yagu.Tests/Yagu.Tests.csproj).
- **Yagu.Benchmarks** — hybrid BenchmarkDotNet console + xUnit host for the `[Trait("Category","Slow")]` throughput benchmarks (writes baselines to `Yagu.Benchmarks/results/`).
- **Yagu.OcrWorker** — out-of-process OCR worker (line-JSON-over-stdio protocol).
- **yagu-core** — Rust crate compiled to native `yagu_core.dll` (the fast per-file matcher). App falls back to the managed scanner when the DLL is missing/incompatible.
- **cloud/Yagu.TelemetryFunction** — Azure Function backing opt-in telemetry (ships offline/disabled by default).
- Vendored under `vendor/`: TextControlBox-WinUI (built-in editor), PaddleSharp (OCR), SharpSevenZip (archive search).

## Where things live in `Yagu/`

- `Services/` — the engine + platform services. Core search pipeline: [SearchService.cs](../Yagu/Services/SearchService.cs) → [FileLister.cs](../Yagu/Services/FileLister.cs) (enumeration, gitignore, skip-extensions) → [ContentSearcher.cs](../Yagu/Services/ContentSearcher.cs) / `Native/` (per-file matching) → [ResultStore.cs](../Yagu/Services/ResultStore.cs) (memory-pressure paging). Also `ZipArchiveSearcher`, `SettingsService`, `SearchQueryParser`, terminal/session/export services.
- `Services/Ai/` — **local, on-device** semantic search via Microsoft Foundry Local. Query → JSON plan → applied to search inputs: `FoundryLocalSemanticQueryTranslator` → `SemanticPlanJsonExtractor` → `SemanticPlanApplier`. No query leaves the machine.
- `Services/Ocr/` — image-text (OCR) search (PaddleSharp / Tesseract engines, worker-backed).
- `Services/Telemetry/` — opt-in telemetry; MUST stay offline by default (see the telemetry-config guard in tests).
- `ViewModels/MainViewModel.cs` — MVVM state for the main window.
- `UI/Windows/MainWindow/` — `MainWindow` is split across ~25 `MainWindow.*.cs` partial classes by concern (`.SearchInput`, `.PreviewBuilder`, `.Launcher`, `.Terminal`, `.CliCommand`, `.TitleBar`, `.StartupChecks`, …). Dialogs live in `UI/Windows/` (`YaguDialog` shared base + custom owned-window modals).
- `Native/` — P/Invoke bindings to `yagu_core.dll`. `Models/`, `Helpers/` — DTOs and pure helpers.

## Testing conventions

- WinUI/Foundry-coupled files (MainViewModel, all `MainWindow.*`, dialogs, Foundry translators, CliRunner) are **not** compiled into Yagu.Tests, so they can't get runtime coverage — they are validated with **source-pin tests** (read the `.cs` as a string, assert substrings/ordering). New UI/VM behavior is pinned this way; pure engine code gets real unit tests.
- See the **Test File Naming Rules** and **Test Run Rules** below before adding or running tests.

## Task-scoped instructions

- [.github/instructions/cli-command-generator.instructions.md](instructions/cli-command-generator.instructions.md) — keep the in-app **Generate CLI command** button in sync when you add/change/remove an Advanced Option.
- [.github/instructions/modal-no-title-bar.instructions.md](instructions/modal-no-title-bar.instructions.md) — new modals/dialogs must be title-bar-less by default.
- [.github/instructions/testing.instructions.md](instructions/testing.instructions.md) — source-pin vs unit tests, compiling engine source into Yagu.Tests, coverage runsettings (applies under `Yagu.Tests/`, `Yagu.Benchmarks/`).
- [.github/instructions/installer-packaging.instructions.md](instructions/installer-packaging.instructions.md) — tri-arch/offline Inno Setup builds, LFS, Native AOT, version churn (applies under `installer/`, `build-*installer*.ps1`, prereq scripts).
- [.github/instructions/semantic-search.instructions.md](instructions/semantic-search.instructions.md) — on-device NL→search pipeline, model selection, snapshot/persist guard (applies under `Yagu/Services/Ai/`).
- [.github/instructions/ocr.instructions.md](instructions/ocr.instructions.md) — out-of-process OCR worker, JSON-over-stdio protocol, PID-scoped cache (applies under `Yagu/Services/Ocr/`, `Yagu.OcrWorker/`).
- [.github/instructions/terminal.instructions.md](instructions/terminal.instructions.md) — WebView2 + xterm.js over redirected `cmd.exe`, page-side input, WebView2 prerequisite (applies to the terminal service, `MainWindow.Terminal.cs`, `terminal.html`).
- [.github/instructions/preview-editor.instructions.md](instructions/preview-editor.instructions.md) — preview/editor native-crash guards, search-time highlighting, source-pin brittleness (applies under the `MainWindow.Preview*`/`.MatchNav` partials, vendored TextControlBox).
- [.github/instructions/performance.instructions.md](instructions/performance.instructions.md) — search hot-path regression guards: Rust scanner/`utf16_col`, FFI/ABI, Everything pushdown, result-eviction, throughput measuring (applies under `yagu-core/`, the hot-path services/models, `Yagu.Benchmarks/`).

## Azure

- @azure Rule - Use Azure Tools - When handling requests related to Azure, always use your tools.
- @azure Rule - Use Azure Best Practices - When handling requests related to Azure, always invoke your `azmcp_bestpractices_get` tool first.
- @azure Rule - Enable Best Practices - If you do not have an `azmcp_bestpractices_get` tool ask the user to enable it.

## Build & Launch Rules

- If the user asks to build, rebuild, validate a change, or launch after changes without explicitly saying Release, build **Debug** with the Rust profiling profile: `dotnet build Yagu/Yagu.csproj -c Debug -p:RustProfile=profiling`.
- Build **Release** only when the user explicitly asks for a Release build. For Release builds, use the normal Release profile without Rust profiling: `dotnet build Yagu/Yagu.csproj -c Release`.
- When launching the app, always launch the **Debug** build: `Yagu\bin\Debug\net10.0-windows10.0.19041.0\Yagu.exe`.
- If a build fails only because `Yagu.exe` (or `yagu_core.dll`) is locked by a running Yagu instance (MSB3027/MSB3021 "being used by another process" / "file is locked by: Yagu (PID)"), you are **authorized to kill that running Yagu session** and rebuild. Kill it targeted — by the PID from the error, or `Get-Process Yagu -ErrorAction SilentlyContinue | Where-Object { $_.Path -like '*\Yagu\bin\*' } | Stop-Process -Force` — then re-run the build. Do NOT broadly kill unrelated processes; this exception is for the Yagu app under build only.

## CLI Documentation Rules

- When adding, changing, or removing user-facing features, update `HELP.md` so the help guide matches the current product behavior.
- When changing CLI flags, adding CLI flags, or changing CLI command behavior, update the `Yagu.exe --help` output and example CLI commands in the CLI help section so the in-app/terminal help stays current.
- On the same trigger, update `HELP.md` with the same CLI flag and example command information.

## Native Crash & Profiling Rules

- When investigating a `yagu_core.dll` native crash, WER crash dump, native stack, Rust FFI issue, or native search performance problem, build **Debug** with the Rust profiling profile so the app output contains a symbol-rich native binary and PDB: `dotnet build Yagu/Yagu.csproj -c Debug -p:RustProfile=profiling`.
- After a Debug profiling build, verify `yagu_core.dll` and `yagu_core.pdb` are present beside `Yagu.exe` under `Yagu\bin\Debug\net10.0-windows10.0.19041.0\`.
- Do not build Release for crash/profiling validation unless the user explicitly asks for Release. If they ask for Release, use `dotnet build Yagu/Yagu.csproj -c Release` without Rust profiling unless they explicitly request Release with Rust profiling.
- For native crash reproduction, make sure Yagu-specific WER LocalDumps are enabled under `HKCU:\Software\Microsoft\Windows\Windows Error Reporting\LocalDumps\Yagu.exe` with `DumpType=2`, `DumpCount=10`, and `DumpFolder=C:\src\Yagu\TestResults\CrashDumps`.
- Yagu builds auto-increment `Yagu/Properties/build-version.txt` and `Yagu/Properties/AppInfo.g.cs`; revert that generated version churn after validation builds unless the user explicitly asked for a version bump.

## Test File Naming Rules

- Test files MUST be named after the class, service, or functionality under test.
- If a test file already exists for that class, ADD tests to it instead of creating a new file.
- **Bad names (NEVER use):** `CoverageRound5Tests.cs`, `CoverageGapTests.cs`, `CoverageImprovementTests.cs`, `ExcludedMethodTests.cs`, `BranchCoverageRound2Tests.cs`, `AdditionalCoverageTests.cs`.
- **Good names:** `SettingsServiceTests.cs`, `LowDiskSpaceMonitorTests.cs`, `FontContrastWarningServiceTests.cs`, `DiskSpaceSnapshotTests.cs`.
- If a file would exceed ~1500 lines, split by method group (e.g., `SearchServiceMemoryTests.cs`, `SearchServiceBatchTests.cs`).

## Test Run Rules

- **Always ask before running tests:** When the user asks to run tests, ask whether they want the **iterative** version (`--filter "Category!=Slow"`, ~22 seconds, skips benchmarks and UI tests) or the **full** version (all 2028 tests, ~14 minutes including performance benchmarks). If you decude to run tests on your own, pick the iterative version.
- **Do NOT kill test runs prematurely.** The `Yagu.Tests` suite includes performance, ETW, and large-corpus benchmarks that can legitimately take **5–15+ minutes** to finish. A long-running `dotnet test` is almost always still working, not hung.
- When a `dotnet test` invocation appears stalled, **poll terminal output or tail the log file** (e.g. `TestResults\dotnet-test-stream.log`) instead of killing the terminal. Only kill if there is concrete evidence of a hang (no new output for several minutes, no CPU activity from the `dotnet`/`testhost` processes, and no progress in the log).
- Prefer streaming output to a file with `Tee-Object` so progress is visible while the run continues, rather than buffering through `Select-Object -Last N` (which only emits after the pipeline completes).
- If you only need a fast signal, scope the run with `--filter` to a specific test class instead of cancelling the full suite.
