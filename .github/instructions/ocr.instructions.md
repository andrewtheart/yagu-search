---
description: "Yagu image-text (OCR) search — out-of-process worker + engines. Use when: editing Services/Ocr, Yagu.OcrWorker, WorkerOcrEngine, PaddleSharp, Tesseract, OCR protocol, OCR worker, image text search, YAGU_OCR env vars, OcrTextCache, ocr-worker staging."
applyTo: "src/Yagu/Services/Ocr/**, src/Yagu.OcrWorker/**"
---

# Yagu — Image-Text (OCR) Search

OCR runs **out-of-process**: `Yagu.OcrWorker.exe` (separate project, `net10.0`, **NON-AOT**,
framework-dependent, always built **x64**) hosts PaddleSharp (default) or Tesseract. It is isolated
because the native OCR stack (paddle_inference / OpenCvSharpExtern) is **not** Native-AOT compatible.

- **Never add a `<ProjectReference>`** from `Yagu` to `Yagu.OcrWorker` — it would drag the non-AOT
  PaddleSharp graph into Yagu's `PublishAot` build.

## Worker protocol (host = `WorkerOcrEngine.cs`, pure managed / AOT-safe)

- Line-delimited **JSON over the worker's stdin/stdout**; stderr = diagnostics only.
- stdin encoding MUST be **BOM-less** (`new UTF8Encoding(false)`). A UTF-8 BOM corrupts the first
  request line (`'0xEF' is an invalid start of a value`) and the search hangs forever.
- Anything that writes to stdout pollutes the protocol — the worker captures the real stdout first,
  then `Console.SetOut(Console.Error)` so library writes (download progress) go to stderr.

## Build & deployment

- The worker auto-builds via `Yagu.csproj` targets `BuildOcrWorker` / `CopyOcrWorkerToOutput` /
  `CopyOcrWorkerToPublish` into `<app>\ocr-worker\`. **Build it as a child `dotnet build` `<Exec>`,
  never an in-proc `<MSBuild>` task** — the in-proc task corrupts the WinUI XAML markup compiler
  (`WMC1509`/`WMC9999`, cascading into bogus `CS0103`). Opt out with `-p:BuildOcrWorker=false`.
- Probe order (`WorkerOcrEngine.ResolveWorkerPath`): env `YAGU_OCR_WORKER` →
  `%LOCALAPPDATA%\Yagu\ocr-runtime\worker\Yagu.OcrWorker.exe` → beside-app `ocr-worker\`. Re-probed
  each search, so a redeployed worker is picked up on the next search without restarting the app.

## Engines & quality knobs

- Default engine is **PaddleSharp on x64/Arm64, Tesseract on x86** (`AppSettings.EffectiveDefaultImageOcrEngine`;
  `DefaultImageOcrEngine` == `"paddle"` is the *preferred* engine, but PaddleOCR's native runtime is win-x64
  only, so `AppSettings.PaddleOcrSupported` is false on x86 and the effective default — plus any persisted
  `paddle` — is coerced to `"tesseract"`). Paddle is faster + more accurate than Tesseract on CPU (OCR runs
  in the x64 worker), and the offline installer (x64) bundles Paddle's full runtime + models so it runs
  download-free. Tesseract stays a user-selectable engine (also bundled offline).
- Quality threads to the worker via env vars — `YAGU_OCR_MODEL`, `YAGU_OCR_MAX_SIDE` — both **ignored
  by Tesseract**. Default model `ChineseV5`, default maxSide `960`. maxSide sentinel: `-1` unspecified
  (omit env var), `0` unlimited, `>0` cap.
- Both engines need `OpenCvSharpExtern.dll` staged (`NativeRuntime.EnsureOpenCvAsync`); Tesseract-only
  paths must stage it too, not just the Paddle path.

## Cache & result attribution

- OCR text cache is **PID-scoped**: `%LOCALAPPDATA%\Yagu\ocr-cache\<hash>.p<pid>.txt`. These cache
  `.txt` files must **never** appear as their own result rows — `SearchService` discovery excludes the
  cache directory. Matches always attribute to the **image path** (`OcrTextMatcher.Match(displayPath=
  imagePath, …)`); no sidecar `.txt` is written next to the image.

## Testing

Pure helpers (`OcrEngineFactory`, `OcrTextMatcher`, `ImageOcrSupport`, `OcrTextCache`, `EngineFactory`)
are compiled into Yagu.Tests → real unit tests. `WorkerOcrEngine` has reachable-path tests plus a
**source-pin** of the wire protocol (consts / switch / probe order). A direct worker smoke test:
`'{"Id":1,"Path":"<img>.png"}' | & Yagu.OcrWorker.exe` → `{"Type":"result","Id":1,"Ok":true,...}`.
