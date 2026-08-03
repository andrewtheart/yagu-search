---
name: build-and-publish-release
description: "Publish a verified Yagu GitHub release — bump the version, build exact installer assets, review commits, push, and create or refresh a draft/published release. Use when: cut a release, publish a release, ship a release, make a release, release Yagu, publish Yagu, build-all-installers -push, draft GitHub release, gh release, upload installers to GitHub, tag a version. For building installers WITHOUT publishing, use the build-installers skill instead."
---

# Build & Publish a Yagu Release

Yagu ships as **tri-architecture + offline** self-contained Native AOT installers, built with Inno
Setup and distributed via **GitHub Releases**. The detailed packaging conventions (staging, release assets,
Windows App Runtime, version churn) live in
`.github/instructions/installer-packaging.instructions.md`; this skill is the on-demand **build +
publish runbook**. To only **build** installers (no commit/push/release), use the **`build-installers`**
skill.

## One command: build → commit → push → verified release

Run from the repo root, with `gh` and GitHub Copilot CLI installed and authenticated:

```powershell
.\build-all-installers.ps1 -push
```

This does, in order:

1. **Bumps the release version ONCE** so every variant shares the same version.
2. Builds **x64, x86, arm64, and x64-offline** installers (self-contained Native AOT published, then
  Inno Setup compiled), each written as `installer/YaguSetup-<version>-<suffix>.exe` (ignored local output
  uploaded directly as a GitHub Release asset).
3. If the working tree is dirty, interactively review `git add --patch` selections one functional
  group at a time and commit each group with an explicit message. The script does not infer semantic
  ownership or split source hunks automatically.
4. After the build, commit only the known release-generated version/README paths, then push.
5. Before push, GitHub Copilot generates constrained user-facing notes from the bounded commit/diff
  context. The script appends deterministic Assets, Validation, Installation, and Full changelog sections.
6. After push, creates or refreshes the GitHub release with only the exact installers built in this run,
  applies the selected Draft/Published mode, and verifies the live body, mode, tag target, sizes, and
  SHA-256 digests (when GitHub exposes them).

Useful flags:

- `-SkipRelease` — build + commit + push, but do not create the GitHub release.
- `-Variant x64,arm64` — build a subset instead of all four.
- `-KeepVersion` — do not bump the version.
- `-ReleaseMode Draft|Published` — choose publication mode without the interactive prompt.
- `-CopilotPath <path>` — use an explicit Copilot CLI executable.
- `-SkipBuild` — explicitly repackage existing publish output.
- `-Commit` — commit but do not push (and no release).
- `-WhatIf` — print the plan only; download and change nothing.

## Pending-change safety

The publish scripts organize pending work **before** building, so release artifacts are never built
from source that was silently swept into a catch-all commit. On a dirty interactive working tree,
select the hunks for one functional change in `git add --patch`, quit patch mode, review the staged
stat, and provide its focused commit message. Repeat until clean.

This workflow deliberately favors stopping over guessing:

- Existing staged changes require explicit confirmation and a commit message.
- Conflicts and detected renames/copies must be handled manually.
- A dirty non-interactive/CI invocation stops instead of creating commits.
- Untracked files enter patch mode via intent-to-add; their content is not staged automatically.
- Post-build commits are path-scoped to `build-version.txt`, `AppInfo.g.cs`, and (for the all-variant
  script) `README.md`. Any other post-build change stops the push.

## Publish the release

The default prompts for Draft or Published. Re-running the same version refreshes an existing release
only when its remote tag still targets the current pushed commit. Missing tools, authentication errors,
note-generation failures, upload failures, and live-verification mismatches are release failures, not
warnings after a successful push. `-SkipRelease` is the explicit build/commit/push-only escape hatch.

`build-installer.ps1 -Push` delegates to this canonical workflow, preserving its requested architecture,
offline edition, `-SkipBuild`, and version-retention options. It does not maintain a second release path.

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
- Installer outputs are ignored by Git and uploaded directly as release assets; never stage them into
  source commits or rediscover assets with a wildcard after the build.
- Do **not** pass `-p:SkipYaguVersionIncrement=true` here — release builds must bump the version so the
  artifact names match the binary. (That flag is only for local Debug/validation builds.)
