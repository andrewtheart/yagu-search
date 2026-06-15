---
description: "Deploy Yagu to the Azure Static Site. Builds, packages, uploads ZIP and installer EXE to the private downloads container, and updates ONLY the Yagu card in the shared index.html. Use when: deploy to static site, publish to azure, upload to download site, push release."
mode: "agent"
---

# Deploy to Azure Static Site

Run the publish script from the workspace root:

```powershell
& "$PSScriptRoot\..\..\scripts\publish-to-azure.ps1"
```

## Rules

- Do NOT modify the script before running it.
- The script handles everything: build, package, installer build, ZIP upload, installer EXE upload (to the private `downloads` container), and targeted index.html update.
- It will only touch the Yagu card in the shared `index.html` at `D:\installationSite\index.html` — other projects' cards are left unchanged.
- All projects sharing the download site use `D:\installationSite\index.html` as the single source of truth.
- **Never modify the auth gate** — the login overlay (`#login-overlay`), Google script tags, and auth JavaScript must remain intact.
- Binaries go to the private `downloads` container, NOT `$web`. The auth JS generates SAS download URLs at runtime.
- The Yagu card should offer both `Download Installer` (`YaguSetup-<version>-x64.exe`) and `Download ZIP` (`Yagu-<version>.zip`).
- Download links use `data-blob="filename"` attributes — never hard-code direct blob URLs.
- Requires `az` CLI to be authenticated (account key auth-mode).
- If it fails, show the full error output to the user — do not retry automatically.
