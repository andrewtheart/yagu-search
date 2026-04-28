# QuickGrep

Fast directory text/regex search for Windows. Native WinUI 3 app on .NET 10 that
shells out to [voidtools Everything](https://www.voidtools.com/) (`es.exe`) for
near-instant file discovery and uses parallel compiled regex / `MemoryMappedFile`
for content search.

Implementation tracks [PLAN.md](PLAN.md).

## Build

```powershell
dotnet restore QuickGrep.sln
dotnet build QuickGrep.sln -c Release
```

## Run tests

```powershell
dotnet test QuickGrep.Tests/QuickGrep.Tests.csproj
```

The test project compiles the engine sources directly (no WindowsAppSDK
dependency) so tests run on any Windows .NET 10 host without WinUI tooling.

## Run benchmarks

```powershell
dotnet run -c Release --project QuickGrep.Benchmarks
```

## Run the app

```powershell
dotnet run -c Release --project QuickGrep -- --dir "D:\projects\myapp"
```

## Register Explorer context menu

```powershell
# install
.\scripts\register-context-menu.ps1 -ExePath 'C:\Tools\QuickGrep\QuickGrep.exe'
# uninstall
.\scripts\register-context-menu.ps1 -Uninstall
```

## Project layout

| Path | Description |
|------|-------------|
| `QuickGrep/` | WinUI 3 app (UI + engine sources) |
| `QuickGrep.Tests/` | xUnit tests for engine, helpers, settings |
| `QuickGrep.Benchmarks/` | BenchmarkDotNet harness |
| `scripts/register-context-menu.ps1` | Per-user shell extension registration |
| `.github/workflows/ci.yml` | Build + test + benchmark smoke pipeline |
