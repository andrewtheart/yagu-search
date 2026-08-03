---
description: "Yagu installer & packaging conventions — tri-arch/offline Inno Setup builds, self-contained Native AOT, Windows App Runtime staging, verified GitHub release assets, and version churn. Use when: build installer, build-installer.ps1, build-all-installers.ps1, Inno Setup, .iss, yagu-installer.iss, offline installer, prerequisite staging, WebView2, Everything bundle, Windows App Runtime, WAR, publish installer."
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

## Release assets

- Root installer outputs (`installer/YaguSetup-*.exe`) are ignored by Git and uploaded directly to
  GitHub Releases. Do not stage or commit them even though the repository has a general `*.exe` LFS rule.
- The canonical workflow records the exact expected output for every selected variant, requires it to
  be non-empty and freshly written by that invocation, and uploads those exact paths. Never replace this
  with wildcard re-discovery that could pick up stale installers.
- `build-installer.ps1 -Push` delegates to `build-all-installers.ps1`; all release modes share one
  implementation for commit review, version pinning, notes, tag protection, and live verification.

## Commit/push safety

- `-Push` (and `build-all-installers.ps1 -Commit`) organizes a dirty tree **before** building through
  reviewed `git add --patch` groups. The user selects one functional group and supplies its commit
  message; the scripts never assign source hunks automatically.
- Conflicts, renames/copies, dirty non-interactive runs, and unexpected post-build files stop the
  workflow before push.
- After a successful build, only the explicit release-generated version/README paths may be staged.
  Never restore catch-all `git add -A` behavior to either publish path.
- Publish preflight requires authenticated `gh` and Copilot CLI before commits/version mutation unless
  `-SkipRelease` is explicit. Release notes are comprehensive, user-facing, and generated read-only from
  bounded context, with deterministic Assets/Validation/Installation/Full changelog sections.
- Refreshing an existing release is allowed only when its remote tag targets current HEAD. After create
  or refresh, verify the live body, state, tag target, exact asset names/sizes, and SHA-256 digests when
  GitHub exposes them. Any mismatch fails the release.

## Version churn

Release/publish auto-increment `src/Yagu/Properties/build-version.txt` + `AppInfo.g.cs`. Revert that churn
after validation builds unless a version bump was requested:
`git checkout -- src/Yagu/Properties/build-version.txt src/Yagu/Properties/AppInfo.g.cs`.

## Prerequisite staging scripts

`scripts/*prereq*.ps1` download to the gitignored cache `installer/prerequisites/` and copy into the
staging tree. Scripts that Inno runs via `powershell.exe -File` must resolve `$PSScriptRoot`-based
defaults AFTER `param(...)` (Windows PowerShell 5.1 sees `$PSScriptRoot` as empty during param-default
binding) and stay ASCII-only or UTF-8-with-BOM (5.1 mis-decodes BOM-less UTF-8).
