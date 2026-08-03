---
description: "Yagu startup self-relaunch & single-instance gotchas for profiling/debugging. Use when: a VS diagnostics/profiling session stops on its own, the profiler detaches immediately, attaching to Yagu.exe measures nothing, editing Program.cs, the detached-GUI relaunch, --yagu-gui-child, TryRelaunchDetachedGui, the single-instance mutex, ActivateExistingInstance, or wiring a profiling launch profile."
applyTo: "src/Yagu/Program.cs, src/Yagu/App.xaml.cs"
---

# Yagu — Profiling & Debugging the GUI (startup self-relaunch)

Yagu's GUI launch is **not** a single process. A normal GUI start in
[`Program.cs`](../../src/Yagu/Program.cs) immediately **re-launches itself as a detached child**
and the original process exits. This breaks naive profiling/diagnostics: the tool attaches to the
launcher, the launcher exits within milliseconds, and the session **ends on its own** while the real
GUI keeps running in a *different* process the tool never attached to.

## The two early-exit paths (both look like "the session stopped by itself")

1. **Detached-GUI relaunch (the usual cause).** For a GUI launch (no `--cli`, no `--yagu-gui-child`),
   `Main` calls `TryRelaunchDetachedGui(args)`, which `Process.Start`s a fresh copy of `Yagu.exe`
   with the `--yagu-gui-child` flag and then `return`s — the launched (profiled) process dies right
   away. The relaunch exists to detach the GUI from the parent console (no lingering console window).
2. **Single-instance mutex.** After the relaunch check, `Main` acquires `Global\YaguSingleInstance`.
   If another Yagu already owns it, the process calls `ActivateExistingInstance()` and `return`s —
   again an immediate exit. So even the child exits instantly if any other Yagu is already running.

## To profile / attach-debug the actual GUI process

- **Pass `--yagu-gui-child`** as a launch argument. Line-of-truth is the guard in `Main`:
  `if (!isGuiChild && TryRelaunchDetachedGui(args)) return;` — when the flag is present, `isGuiChild`
  is `true`, the relaunch is **skipped**, and the GUI runs **in-process**, so the profiler/debugger
  stays attached for the whole session. Set it in *Project Properties → Debug → Command line
  arguments*, or as `"commandLineArgs": "--yagu-gui-child"` in a `launchSettings.json` profile.
- **Kill every stray `Yagu.exe` first.** Otherwise the single-instance mutex path exits the child
  immediately (symptom is identical to the relaunch case).
- Prefer a dedicated **"Yagu (Profiling)"** launch profile carrying `--yagu-gui-child` so the normal
  F5 launch (which should keep the detached-relaunch behavior) is unaffected.
- Build per the repo's Native Crash & Profiling Rules for a symbol-rich native binary:
  `dotnet build src/Yagu/Yagu.csproj -c Debug -p:RustProfile=profiling -p:SkipYaguVersionIncrement=true`.

## When editing `Program.cs`

`--yagu-gui-child` is a **load-bearing profiling/debugging affordance**, not just an internal relaunch
token — do not remove it or make the relaunch unconditional. If you change `TryRelaunchDetachedGui`,
`ConsumeGuiChildFlag`, or the single-instance mutex flow, keep an in-process path reachable via a
single documented flag so the GUI process remains attachable.
