---
name: build-installers
description: "Build Yagu installer EXEs locally (no publish) — all four variants, a single architecture, the offline/OCR edition, or repackage without rebuilding. Use when: build the installers, build installer, build an installer, build all installers, build-installer.ps1, build-all-installers.ps1, build the x64/x86/arm64 installer, single-arch installer, build the offline installer, OCR-bundled installer, repackage installer, rebuild installer, recompile Inno Setup, compile the .iss, local/QA installer build, test installer build. For publishing a GitHub release (commit + push + draft release), use the build-and-publish-release skill instead."
---

# Build Yagu Installers (local, no publish)

Yagu ships **tri-architecture + offline** self-contained Native AOT installers compiled with Inno
Setup. This skill is the on-demand runbook for **building** those installer EXEs locally (QA / test /
inspection) **without** committing, pushing, or cutting a GitHub release — for that, use the
**`build-and-publish-release`** skill. Deep packaging conventions (staging, LFS, Windows App Runtime,
version churn) live in `.github/instructions/installer-packaging.instructions.md`.

Both scripts live at the repo root and are invoked in place — **do not move them**:
`build-all-installers.ps1` (orchestrator) and `build-installer.ps1` (per-architecture worker).

## Build every variant (no publish)

```powershell
.\build-all-installers.ps1
```

Builds all four: **x64, x86, arm64, and x64-offline**, each written to
`installer/YaguSetup-<version>-<suffix>.exe` (and `installer/output/`). No commit/push/release without
`-Commit`/`-Push`.

Useful flags:

- `-Variant x64,arm64` — build a subset instead of all four (comma-separated; order/dupes normalized).
- `-Variant x64-offline` — only the OFFLINE x64 edition (OCR runtime + models bundled; Tesseract default).
- `-WhatIf` — print the resolved build plan only; build nothing.
- `-SkipReadmeUpdate` — don't rewrite the README "Download Installer" table.

## Build a single architecture

```powershell
.\build-installer.ps1 -Architecture x64          # or x86 / arm64 / all
```

- `-IncludeOcr` — build the offline/OCR-bundled edition (forced to **x64**; output
  `YaguSetup-<version>-x64-offline.exe`). Sources the bundled OCR payload from
  `%LOCALAPPDATA%\Yagu\ocr-runtime` (override with `-OcrPayloadCacheDir`); missing assets are
  downloaded by running the staged worker.
- `-SkipVersionIncrement` — don't bump `build-version.txt` (use for throwaway local/QA builds to avoid
  version churn). Omit it when the installer will actually be distributed so its version is unique.

## Repackage without rebuilding

Re-run Inno Setup over an already-published `publish\` output (e.g. after only a staging/`.iss` tweak),
skipping the multi-minute AOT publish:

```powershell
.\build-installer.ps1 -Architecture x64 -SkipBuild -SkipVersionIncrement
```

## Prerequisites & gotchas

- Requires **Inno Setup 6** (`ISCC.exe`; auto-detected, override with `-InnoSetupPath`) and the Windows
  App Runtime prerequisite staging. **arm64** additionally needs the MSVC ARM64 C++ build tools +
  `rustup target add aarch64-pc-windows-msvc`, or the AOT link step fails. On a fresh machine run
  `.\install-dev-prerequisites.ps1` first.
- There is intentionally **no x86-offline / arm64-offline** variant — the bundled OCR native runtime
  (PaddleOCR + OpenCv) is win-x64 only; on x86/arm64 OCR still works by downloading its assets.
- Installer EXEs are **Git LFS**-tracked; a pre-commit hook keeps only the latest per arch/edition.
  Building the same-arch installer deletes the previous root copy — restore with `git lfs checkout`.
- Installer artifacts are **not** rebuilt by a code commit — after any change that must reach the
  installer (app code OR a bundled payload), re-run the build.
