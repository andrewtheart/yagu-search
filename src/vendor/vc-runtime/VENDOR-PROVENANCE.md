# VENDOR PROVENANCE — Visual C++ Runtime (app-local CRT)

**These are one-time copies of Microsoft-signed redistributable binaries.
They have never been refreshed since they were taken.**

There is **no upstream GitHub repository and therefore no base commit** for this directory. It is not
open-source code; it is the app-local deployment of the Microsoft Visual C++ Redistributable CRT
(`Microsoft.VC145.CRT`), shipped beside the Yagu binaries so the native OCR/Paddle dependencies load
without requiring a machine-wide redistributable install.

| | |
|---|---|
| Source | Microsoft Visual C++ Redistributable — app-local CRT file set |
| **File version (all files, both architectures)** | **`14.51.36231.0`** |
| x64 file count | 10 |
| x86 file count | 9 |

## Files

x64 and x86 both contain: `concrt140.dll`, `msvcp140.dll`, `msvcp140_1.dll`, `msvcp140_2.dll`,
`msvcp140_atomic_wait.dll`, `msvcp140_codecvt_ids.dll`, `vccorlib140.dll`, `vcruntime140.dll`,
`vcruntime140_threads.dll`. x64 additionally contains `vcruntime140_1.dll` (x64-only by design).

## How this was determined

Read from the `FileVersion` resource of every DLL in this directory. All 19 files report the same
version, so the set is a single uniform servicing revision.

## Refresh rule

Update each architecture as one complete, uniform `Microsoft.VC145.CRT` file set. Require a valid
Microsoft Authenticode signature on every file and a single identical file version across the whole
set. Never mix servicing revisions or architecture file sets, and never update individual DLLs.
