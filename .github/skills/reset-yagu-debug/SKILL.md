---
name: reset-yagu-debug
description: "Reset Yagu Debug runtime state on Windows without uninstalling Yagu or deleting build outputs. This skill should be used when the user invokes /reset-yagu-debug or asks to stop the repository Debug copy and remove its shared settings, content indexes, caches, and temporary files."
---

# Reset Yagu Debug

Stop Yagu processes running from this repository's Debug output and remove the current user's shared
Yagu runtime state. Preserve installed copies, uninstall registrations, integrations, PATH entries,
and all repository build outputs.

## Run the reset

Execute the bundled script from the repository root:

```powershell
& '.github\skills\reset-yagu-debug\scripts\reset-yagu-debug.ps1'
```

Use `-WhatIf` only when the user asks to preview the reset:

```powershell
& '.github\skills\reset-yagu-debug\scripts\reset-yagu-debug.ps1' -WhatIf
```

## Preserve safety boundaries

- Resolve the repository root from the skill's own location.
- Stop only exact Yagu executable names whose executable paths are below a repository `bin\Debug`
  directory, always by PID.
- Abort if an installed Yagu process is running because installed and Debug copies share runtime
  state; leave that process and all installation metadata untouched.
- Remove exact Yagu-owned settings, index, cache, and dedicated temp paths.
- Delete only recognized Yagu index artifacts from a custom index root; preserve unrelated files.
- Delete only Yagu result files from a custom temp folder unless it is a dedicated `Temp\Yagu`
  folder.
- Never invoke an installer or uninstaller.
- Never delete `bin`, `obj`, publish output, or another repository file.
- Do not relaunch Yagu after the reset.

## Report the result

Treat a nonzero exit code as a failed or incomplete reset. Report the failed verification items. On
success, report that Debug Yagu is stopped and its shared runtime state is absent while installations
and build outputs were preserved.
