# Installers

The built installer `.exe` files are **not committed to this repository**. They are large
(≈150–560 MB each) and would exhaust the repository's Git LFS budget. Instead, they are published as
**GitHub Release assets**, and `build-all-installers.ps1` writes them here on disk only so it can
upload them to the release.

`installer/YaguSetup-*.exe` is therefore listed in [`.gitignore`](../.gitignore); the only tracked
files in this folder are configuration and this README.

## Download the installers

**Always-current (latest release):**

- **[⬇ Latest release — pick your installer](https://github.com/andrewtheart/yagu-search/releases/latest)**

**Current version — direct downloads (v1.0.0.2380):**

| Installer | Direct download |
| --- | --- |
| x64 (most PCs) | [YaguSetup-1.0.0.2380-x64.exe](https://github.com/andrewtheart/yagu-search/releases/download/v1.0.0.2380/YaguSetup-1.0.0.2380-x64.exe) (~163 MB) |
| x64 · Offline (OCR + Everything bundled) | [YaguSetup-1.0.0.2380-x64-offline.exe](https://github.com/andrewtheart/yagu-search/releases/download/v1.0.0.2380/YaguSetup-1.0.0.2380-x64-offline.exe) (~538 MB) |
| Arm64 (Windows on ARM) | [YaguSetup-1.0.0.2380-arm64.exe](https://github.com/andrewtheart/yagu-search/releases/download/v1.0.0.2380/YaguSetup-1.0.0.2380-arm64.exe) (~163 MB) |
| x86 (32-bit Windows) | [YaguSetup-1.0.0.2380-x86.exe](https://github.com/andrewtheart/yagu-search/releases/download/v1.0.0.2380/YaguSetup-1.0.0.2380-x86.exe) (~144 MB) |

See the [README "Download Installer" section](../README.md#download-installer) for which edition to
pick and details about offline/OCR support.

> The repository `andrewtheart/yagu-search` is private, so these download links require an
> authenticated GitHub account with access to the repo.

## Building installers locally

Build them into this folder (they stay untracked) with:

```powershell
# All four variants, then commit + push + create a draft GitHub release with the installers attached
.\build-all-installers.ps1 -Push

# Build only (no publish); or a single architecture
.\build-all-installers.ps1
.\build-installer.ps1 -Architecture x64
```

The publish step uploads the on-disk `YaguSetup-<version>-*.exe` files here as the release's assets
and rewrites the README download table to point at that release.
