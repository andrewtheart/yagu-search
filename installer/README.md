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

**Current release — direct downloads:**

| Installer | Direct download |
| --- | --- |
| x64 (most PCs) | [YaguSetup-1.0.0.2407-x64.exe](https://github.com/andrewtheart/yagu-search/releases/download/v1.0.0.2407/YaguSetup-1.0.0.2407-x64.exe) (~195 MB) |
| x64 · Offline (OCR + Everything bundled) | [YaguSetup-1.0.0.2407-x64-offline.exe](https://github.com/andrewtheart/yagu-search/releases/download/v1.0.0.2407/YaguSetup-1.0.0.2407-x64-offline.exe) (~485 MB) |
| Arm64 (Windows on ARM) | [YaguSetup-1.0.0.2407-arm64.exe](https://github.com/andrewtheart/yagu-search/releases/download/v1.0.0.2407/YaguSetup-1.0.0.2407-arm64.exe) (~191 MB) |
| x86 (32-bit Windows) | [YaguSetup-1.0.0.2407-x86.exe](https://github.com/andrewtheart/yagu-search/releases/download/v1.0.0.2407/YaguSetup-1.0.0.2407-x86.exe) (~173 MB) |

See the [README "Download Installer" section](../README.md#download-installer) for which edition to
pick and details about offline/OCR support.

> The repository `andrewtheart/yagu-search` is private, so these download links require an
> authenticated GitHub account with access to the repo.

## Building installers locally

Build them into this folder (they stay untracked) with:

```powershell
# All four variants, then commit + push + prompt for a draft or officially published release
.\build-all-installers.ps1 -Push

# Unattended release-mode overrides
.\build-all-installers.ps1 -Push -ReleaseMode Draft
.\build-all-installers.ps1 -Push -ReleaseMode Published

# Build only (no publish); or build/push one architecture with the same release prompt
.\build-all-installers.ps1
.\build-installer.ps1 -Architecture x64
.\build-installer.ps1 -Architecture x64 -Push
```

The publish step uploads the on-disk `YaguSetup-<version>-*.exe` files here as the release's assets
and rewrites the README download table to point at that release. Unless `-ReleaseMode Draft` or
`-ReleaseMode Published` is supplied, the scripts ask whether the release should remain a draft for
review or be published officially as the latest release.

## Authenticode code signing (optional)

Builds are unsigned by default. Pass a code-signing certificate thumbprint to sign a release:

```powershell
.\build-all-installers.ps1 -SignCertThumbprint <40-hex-sha1-thumbprint> -Push
.\build-installer.ps1 -Architecture x64 -SignCertThumbprint <40-hex-sha1-thumbprint>
```

The certificate must be in `Cert:\CurrentUser\My` or `Cert:\LocalMachine\My` (insert the hardware
token first). `signtool.exe` is resolved from `PATH` or the newest Windows SDK; override either with
`-SignToolPath` and `-SignTimestampUrl`.

Signing is **all-or-nothing by design**. Each variant signs `Yagu.exe`, `yagu_core.dll`, and all
three worker executables (`Yagu.OcrWorker.exe`, `Yagu.SemanticWorker.exe`, `Yagu.IndexWorker.exe`)
before Inno compresses the staging tree, then signs the setup EXE itself — because at runtime:

- `AuthenticodeVerifier.IsWorkerTrustedForHost` refuses to launch a worker whose publisher does not
  match a signed `Yagu.exe`, so signing the app alone would break OCR and semantic search.
- `AuthenticodeVerifier.IsInstallerTrustedForHostPublisher` refuses a downloaded update unless both
  the running build and the installer are signed by the same publisher — this is why in-app updates
  currently fail with "the running Yagu build is not Authenticode-signed".

Signed builds also compile with `/DYaguSigned=1`, which removes the Smart App Control enforce gate
in `yagu-installer.iss` that otherwise cancels setup on SAC-protected machines.

Third-party payloads (Windows App Runtime, WebView2, Everything, PaddleOCR natives, `pdftotext`)
keep their vendor signatures and are never re-signed.

## License information

The installer follows the same Inno Setup flow as MultiTerm: interactive setup displays Yagu's
complete GPLv3 license on the standard agreement page during setup, and displays the consolidated
third-party notices on the post-install information page. Both files are also installed beside
Yagu so they remain available after setup finishes.
