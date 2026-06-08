- @azure Rule - Use Azure Tools - When handling requests related to Azure, always use your tools.
- @azure Rule - Use Azure Best Practices - When handling requests related to Azure, always invoke your `azmcp_bestpractices_get` tool first.
- @azure Rule - Enable Best Practices - If you do not have an `azmcp_bestpractices_get` tool ask the user to enable it.

## Build & Launch Rules

- If the user asks to build, rebuild, validate a change, or launch after changes without explicitly saying Release, build **Debug** with the Rust profiling profile: `dotnet build Yagu/Yagu.csproj -c Debug -p:RustProfile=profiling`.
- Build **Release** only when the user explicitly asks for a Release build. For Release builds, use the normal Release profile without Rust profiling: `dotnet build Yagu/Yagu.csproj -c Release`.
- When launching the app, always launch the **Debug** build: `Yagu\bin\Debug\net10.0-windows10.0.19041.0\Yagu.exe`.

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

## Test Run Rules

- **Do NOT kill test runs prematurely.** The `Yagu.Tests` suite includes performance, ETW, and large-corpus benchmarks that can legitimately take **5–15+ minutes** to finish. A long-running `dotnet test` is almost always still working, not hung.
- When a `dotnet test` invocation appears stalled, **poll terminal output or tail the log file** (e.g. `TestResults\dotnet-test-stream.log`) instead of killing the terminal. Only kill if there is concrete evidence of a hang (no new output for several minutes, no CPU activity from the `dotnet`/`testhost` processes, and no progress in the log).
- Prefer streaming output to a file with `Tee-Object` so progress is visible while the run continues, rather than buffering through `Select-Object -Last N` (which only emits after the pipeline completes).
- If you only need a fast signal, scope the run with `--filter` to a specific test class instead of cancelling the full suite.
