---
description: "Launch the already-compiled Release build of Yagu without rebuilding. Use when: run release, launch release, start release."
mode: "agent"
---

Kill any running Yagu instance, then launch the Release executable directly.

```powershell
$p = Get-Process -Name Yagu -ErrorAction SilentlyContinue; if ($p) { Stop-Process -Id $p.Id -Force; Start-Sleep -Seconds 1 }
Start-Process Yagu\bin\Release\net10.0-windows10.0.19041.0\Yagu.exe
```
