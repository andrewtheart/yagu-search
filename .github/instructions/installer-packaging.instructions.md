---
description: "Yagu installer & packaging conventions — tri-arch/offline Inno Setup builds, self-contained Native AOT, Windows App Runtime staging, Git LFS, and version churn. Use when: build installer, build-installer.ps1, build-all-installers.ps1, Inno Setup, .iss, yagu-installer.iss, offline installer, prerequisite staging, WebView2, Everything bundle, Windows App Runtime, WAR, publish installer, LFS installer."
applyTo: "installer/**, build-installer.ps1, build-all-installers.ps1, scripts/*prereq*.ps1, scripts/install-windows-app-runtime.ps1"
---

# Yagu — Installer & Packaging

Yagu ships as **self-contained Native AOT** (`PublishAot=true`, `--self-contained`), unpackaged
WinUI 3 (`WindowsPackageType=None`). Target machines need NO .NET runtime — only the Windows App
Runtime, which the installer bundles and installs. Building from source needs the .NET 10 SDK.

## Building installers

- `build-installer.ps1 -Architecture x64|x86|arm64|all` (default `all`) builds one Inno Setup EXE per
  arch: `dotnet publish -r win-<arch> --self-contained` → stage `publish\` → compile with
  `ISCC.exe /DYaguArch=<arch>`. Output `installer/output/YaguSetup-<version>-<arch>.exe`, copied to
  `installer/`.
- `build-all-installers.ps1 -Variant x64|x86|arm64|x64-ocr|all` also builds the **x64-offline/OCR**
  edition via `-IncludeOcr` (bundles the OCR runtime + models, the voidtools Everything setup, and the
  full WebView2 standalone installer for air-gapped machines).
- Installer artifacts are NOT rebuilt by a code commit. After any change that must reach the installer
  (app code OR a bundled payload), **re-run the build** — otherwise a published installer ships stale
  behavior.
- arm64 cross-build from an x64 host needs the MSVC ARM64 build tools + Windows SDK ARM64 +
  `rustup target add aarch64-pc-windows-msvc`; without them the AOT link step fails fatally. Don't
  auto-install these (shared-system change) — ask first.

## Inno Setup (`installer/yagu-installer.iss`) gotchas

- Inside Pascal `{ }` comments in `[Code]`, never write `{app}` or any literal `}` (e.g. a GUID) — the
  `}` closes the comment early and ISCC fails with a misleading "Error on line N". Keep braces out of
  comments; GUIDs inside single-quoted string literals are fine.
- Validate ISS edits by compiling per arch with `C:\Program Files (x86)\Inno Setup 6\ISCC.exe`.
- `[Code]` has no .NET-runtime logic; it keeps `InstallWindowsAppRuntime()` (ssPostInstall, `Abort`
  on failure), the WebView2/Everything install-time steps, the `/VERBOSELOG` registry override, the
  Smart App Control enforce gate (`InitializeSetup` cancels setup when SAC is enforcing), and
  context-menu registry cleanup on uninstall.

## Git LFS & the pre-commit policy

- Installer EXEs exceed GitHub's 100 MB raw limit and are tracked via **Git LFS** (`.gitattributes`:
  `*.exe filter=lfs`). A committed installer must be an LFS pointer (first line
  `version https://git-lfs.github.com/spec/v1`), not a raw blob.
- `scripts/git-hooks/pre-commit` enforces **latest-per-arch/edition**: it keeps only the
  highest-version installer for each of x64 / x86 / arm64 / x64-ocr and untracks the rest. Install or
  refresh it with [scripts/install-git-hooks.ps1](../../scripts/install-git-hooks.ps1) (copies into
  `.git/hooks/`).
- `build-installer.ps1` deletes the same-arch previous root installer, so a tracked LFS installer can
  vanish from the working tree after a build — restore with `git lfs checkout`.

## Version churn

Release/publish auto-increment `src/Yagu/Properties/build-version.txt` + `AppInfo.g.cs`. Revert that churn
after validation builds unless a version bump was requested:
`git checkout -- src/Yagu/Properties/build-version.txt src/Yagu/Properties/AppInfo.g.cs`.

## Prerequisite staging scripts

`scripts/*prereq*.ps1` download to the gitignored cache `installer/prerequisites/` and copy into the
staging tree. Scripts that Inno runs via `powershell.exe -File` must resolve `$PSScriptRoot`-based
defaults AFTER `param(...)` (Windows PowerShell 5.1 sees `$PSScriptRoot` as empty during param-default
binding) and stay ASCII-only or UTF-8-with-BOM (5.1 mis-decodes BOM-less UTF-8).
