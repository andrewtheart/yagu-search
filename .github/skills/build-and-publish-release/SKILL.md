---
name: build-and-publish-release
description: "Publish a Yagu GitHub release — bump the version, build all installers, commit, push, and create a draft GitHub release. Use when: cut a release, publish a release, ship a release, make a release, release Yagu, publish Yagu, build-all-installers -push, draft GitHub release, gh release, upload installers to GitHub, tag a version. For building installers WITHOUT publishing (local/QA, single-arch, offline, repackage), use the build-installers skill instead."
---

# Build & Publish a Yagu Release

Yagu ships as **tri-architecture + offline** self-contained Native AOT installers, built with Inno
Setup and distributed via **GitHub Releases**. The detailed packaging conventions (staging, LFS,
Windows App Runtime, version churn) live in
`.github/instructions/installer-packaging.instructions.md`; this skill is the on-demand **build +
publish runbook**. To only **build** installers (no commit/push/release), use the **`build-installers`**
skill.

## One command: build → commit → push → draft release

Run from the repo root, with `gh` installed and authenticated:

```powershell
.\build-all-installers.ps1 -push
```

This does, in order:

1. **Bumps the release version ONCE** so every variant shares the same version.
2. Builds **x64, x86, arm64, and x64-offline** installers (self-contained Native AOT published, then
   Inno Setup compiled), each written as `installer/YaguSetup-<version>-<suffix>.exe` (Git LFS-tracked).
3. `git add -A`, commit, and push.
4. After a successful push, creates a **DRAFT** GitHub release via `gh` — tag `v<version>`, the built
   installers attached, auto-generated notes.

Useful flags:

- `-SkipRelease` — build + commit + push, but do not create the GitHub release.
- `-Variant x64,arm64` — build a subset instead of all four.
- `-KeepVersion` — do not bump the version.
- `-Commit` — commit but do not push (and no release).
- `-WhatIf` — print the plan only; download and change nothing.

## Publish the release

The release is created as a **draft on purpose** — review its notes and attached assets on the GitHub
Releases page, then publish it manually. Re-running the same version refreshes the existing release's
assets (`gh release upload --clobber`) instead of failing. If `gh` is missing or unauthenticated, the
release step only **warns** (the build + push already succeeded) and prints the manual
`gh release create ...` command.

## Repackage without rebuilding

To re-run Inno Setup over an already-published output (e.g. after only a staging tweak) and skip the
multi-minute AOT publish:

```powershell
.\build-installer.ps1 -Architecture x64 -SkipBuild -SkipVersionIncrement
```

## Prerequisites & gotchas

- Requires **Inno Setup 6**, the Windows App Runtime prerequisite staging, and — for **arm64** — the
  MSVC ARM64 C++ build tools + `rustup target add aarch64-pc-windows-msvc`. On a fresh machine run
  `.\install-dev-prerequisites.ps1` first.
- The repo `andrewtheart/yagu-search` is **private**, so raw-file download links only work for
  authenticated users — GitHub **Releases** are the public distribution path.
- Installers are **Git LFS** pointers (they exceed GitHub's 100 MB raw limit). A pre-commit hook keeps
  only the latest installer per arch/edition.
- Do **not** pass `-p:SkipYaguVersionIncrement=true` here — release builds must bump the version so the
  artifact names match the binary. (That flag is only for local Debug/validation builds.)
