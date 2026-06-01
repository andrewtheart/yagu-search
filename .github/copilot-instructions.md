- @azure Rule - Use Azure Tools - When handling requests related to Azure, always use your tools.
- @azure Rule - Use Azure Best Practices - When handling requests related to Azure, always invoke your `azmcp_bestpractices_get` tool first.
- @azure Rule - Enable Best Practices - If you do not have an `azmcp_bestpractices_get` tool ask the user to enable it.

## Build & Launch Rules

- Always build **both** Debug and Release with the Rust profiling profile so that `yagu_core.dll` always has symbols for crash analysis: `dotnet build Yagu/Yagu.csproj -c Debug -p:RustProfile=profiling` and `dotnet build Yagu/Yagu.csproj -c Release -p:RustProfile=profiling`.
- When launching the app, always launch the **Debug** build: `Yagu\bin\Debug\net10.0-windows10.0.19041.0\Yagu.exe`.

## Native Crash & Profiling Rules

- When investigating a `yagu_core.dll` native crash, WER crash dump, native stack, Rust FFI issue, or native search performance problem, build with the Rust profiling profile so the app output contains a symbol-rich native binary and PDB: `dotnet build Yagu/Yagu.csproj -c Debug -p:RustProfile=profiling` and `dotnet build Yagu/Yagu.csproj -c Release -p:RustProfile=profiling`.
- After a profiling build, verify `yagu_core.dll` and `yagu_core.pdb` are present beside `Yagu.exe` under both `Yagu\bin\Debug\net10.0-windows10.0.19041.0\` and `Yagu\bin\Release\net10.0-windows10.0.19041.0\`.
- For native crash reproduction, make sure Yagu-specific WER LocalDumps are enabled under `HKCU:\Software\Microsoft\Windows\Windows Error Reporting\LocalDumps\Yagu.exe` with `DumpType=2`, `DumpCount=10`, and `DumpFolder=C:\src\Yagu\TestResults\CrashDumps`.
- Yagu builds auto-increment `Yagu/Properties/build-version.txt` and `Yagu/Properties/AppInfo.g.cs`; revert that generated version churn after validation builds unless the user explicitly asked for a version bump.

## Test Run Rules

- **Do NOT kill test runs prematurely.** The `Yagu.Tests` suite includes performance, ETW, and large-corpus benchmarks that can legitimately take **5–15+ minutes** to finish. A long-running `dotnet test` is almost always still working, not hung.
- When a `dotnet test` invocation appears stalled, **poll terminal output or tail the log file** (e.g. `TestResults\dotnet-test-stream.log`) instead of killing the terminal. Only kill if there is concrete evidence of a hang (no new output for several minutes, no CPU activity from the `dotnet`/`testhost` processes, and no progress in the log).
- Prefer streaming output to a file with `Tee-Object` so progress is visible while the run continues, rather than buffering through `Select-Object -Last N` (which only emits after the pipeline completes).
- If you only need a fast signal, scope the run with `--filter` to a specific test class instead of cancelling the full suite.
