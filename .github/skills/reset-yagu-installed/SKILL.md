---
name: reset-yagu-installed
description: "Completely reset installed Yagu copies on Windows. This skill should be used when the user invokes /reset-yagu-installed or asks to kill Yagu, uninstall every installed copy, and remove Yagu settings, content indexes, caches, temporary files, shortcuts, PATH entries, and Explorer integrations."
---

# Reset Installed Yagu

Reset the current user's Yagu state and remove every registered machine or user installation without
touching the repository or Debug build outputs.

## Run the reset

Execute the bundled script from the repository root:

```powershell
& '.github\skills\reset-yagu-installed\scripts\reset-yagu-installed.ps1'
```

Run it from a normal, non-elevated PowerShell session so legacy per-user uninstallers stay
unelevated; let the script elevate only its machine-wide phase.

Approve the single Windows elevation prompt when it appears. Allow the script to finish; it
relaunches itself elevated in a hidden PowerShell process, waits for every uninstaller, removes
residue, and exits with the elevated process's exit code.

Use `-WhatIf` only when the user asks to preview the reset:

```powershell
& '.github\skills\reset-yagu-installed\scripts\reset-yagu-installed.ps1' -WhatIf
```

## Preserve safety boundaries

- Stop only processes with Yagu's exact executable names, always by PID.
- Run only uninstallers discovered from Yagu uninstall registrations.
- Remove only exact Yagu-owned install, data, shortcut, and registry paths.
- Delete a dedicated default index root completely.
- Delete only recognized Yagu index artifacts from a custom index root; preserve unrelated files.
- Delete only Yagu result files from a custom temp folder unless the folder is the dedicated
  `Temp\Yagu` location.
- Preserve repository files and Debug build outputs.
- Do not relaunch Yagu after the reset.

## Report the result

Treat a nonzero exit code as a failed or incomplete reset. Report the script's failed verification
items rather than claiming success. On success, report that no Yagu process, registration, install
directory, settings, index, cache, temp data, shortcut, PATH entry, or Explorer integration remains.
