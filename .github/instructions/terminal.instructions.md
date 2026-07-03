---
description: "Yagu embedded terminal (WebView2 + xterm.js over a redirected cmd.exe). Use when: editing ConPtyTerminalService, MainWindow.Terminal, terminal.html, TerminalCompletionService, TerminalDirectoryGuard, xterm, WebView2 terminal, terminal input/echo, shell backend."
applyTo: "Yagu/Services/ConPtyTerminalService.cs, Yagu/Services/TerminalCompletionService.cs, Yagu/Services/TerminalDirectoryGuard.cs, Yagu/Services/TerminalShellKind.cs, Yagu/UI/Windows/MainWindow/MainWindow.Terminal.cs, Yagu/Assets/terminal.html"
---

# Yagu — Embedded Terminal

The embedded terminal is **WebView2 + xterm.js** (`Yagu/Assets/terminal.html`) driving a **redirected
`cmd.exe /Q /K` over pipes** — NOT a real ConPTY, even though the host class is named
`ConPtyTerminalService`.

## Backend

- The real ConPTY path is **deliberately disabled** (`const bool enablePseudoConsole = false`): a
  pseudo-console's differential renderer never streams the prompt as printable text, so xterm shows a
  blinking cursor with no prompt. Keep the redirected backend.
- `StartRedirectedShell` MUST set `_backend = TerminalBackend.RedirectedPipes` right after
  `_process.Start()` — otherwise `WriteInput` skips both local echo and line-ending normalization and
  commands never run.
- Redirected `cmd.exe` reads line-by-line and needs `\r\n`; xterm sends a lone `\r` for Enter, so
  `WriteInput` runs `NormalizeShellLineEndings` before writing. Regression tests assert **real
  execution** (output contains `42` from `set /a 6*7`), not just local echo.

## Input path

- Keyboard input is handled **inside `terminal.html`** via a window-level `keydown` **capture**
  listener that forwards keys to the host. Do **NOT** add a WinUI host-side `KeyDown`/
  `CharacterReceived` bridge on the WebView2 element — WebView2 in WinUI 3 handles keyboard input in
  its own Chromium process and never routes it through XAML (verified, tried, removed).
- Prompt-line editing/history lives page-side (`inputBuffer` in `terminal.html`). Enter sends
  `type:'input'` with `echoInput:false` so the C# local echo doesn't duplicate the line. Don't forward
  Arrow/Backspace/Home/End as raw escape sequences at the prompt (produces `ESC[A` junk).
- Paste and **generated CLI command** insertion post `type:'pasteText'` back into `terminal.html`
  (keeps the page-side line editor in sync); don't write pasted text straight to the service. A
  generated command must first verify the shell cwd via `TerminalDirectoryGuard` before it's posted.

## Process safety

Never broad-kill by process name. Cancel (Ctrl+C) and Reset kill only **descendants** of the embedded
shell PID (Toolhelp snapshot); `Dispose()` kills descendants **first**, then writes `exit`.

## WebView2 runtime prerequisite

The terminal is the **only** WebView2 consumer in Yagu. On a clean machine `CoreWebView2Environment.
CreateAsync()` throws `FileNotFoundException` — catch it and show `TerminalWebView2MissingPanel`
(graceful message + install link); the installer bundles the runtime (see the installer instruction).
