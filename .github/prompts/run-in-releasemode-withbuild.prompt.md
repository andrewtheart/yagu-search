---
description: "Build Yagu in Release mode and launch it. Use when: run release, build and run release, test release build."
mode: "agent"
---

Kill any running Yagu instance, build in Release mode, then launch the Release executable.

```powershell
$p = Get-Process -Name Yagu -ErrorAction SilentlyContinue; if ($p) { Stop-Process -Id $p.Id -Force; Start-Sleep -Seconds 1 }
dotnet build Yagu/Yagu.csproj -c Release -nologo -v:minimal
Start-Process Yagu\bin\Release\net10.0-windows10.0.19041.0\Yagu.exe
```
