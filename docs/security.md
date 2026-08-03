# Security Fixes Needed

**Audit date:** 2026-07-26

**Scope:** Current Yagu working tree, including the desktop application, Rust/FFI code, workers, Azure telemetry Function, installer/build scripts, CI, dependencies, and tracked secrets. Generated `bin`/`obj` output was excluded.

**Result:** 0 Critical, 3 High, 10 Medium, 3 Low.

> The working tree contained extensive uncommitted changes during the audit. These findings apply to that working-tree state, not only to the last commit.

## Recommended remediation order

1. Rotate and untrack the telemetry token; make Function authentication fail closed.
2. Verify WebView2 prerequisites before packaging and elevated execution.
3. Pin and verify all downloaded OCR runtimes, models, and language data.
4. Restrict content-index storage and clear-all deletion to Yagu-owned directories.
5. Add archive, regex, PDF, session, and OCR resource limits.
6. Sign release artifacts and harden release provenance.
7. Pin CI actions and production dependencies.

---

## High severity

### H-1: Unverified WebView2 payload is executed as administrator

**Evidence**

- `scripts/webview2-prereq.ps1:29-49` downloads or reuses the WebView2 bootstrapper and validates it only by a minimum size.
- `scripts/webview2-prereq.ps1:72-94` applies the same size-only validation to the offline installer.
- `installer/yagu-installer.iss:75-84` requires administrator privileges for setup.
- `installer/yagu-installer.iss:315-335` executes whichever staged WebView2 executable exists without verifying its signature or digest.

**Attack preconditions**

A release cache is poisoned, a trusted TLS/download path or vendor endpoint is compromised, or the release workstation is compromised before packaging.

**Impact**

An attacker-controlled executable can be bundled into Yagu and executed as administrator on every machine receiving the affected installer.

**Required fixes**

- Require a valid Microsoft Authenticode signature and expected publisher before staging.
- Pin the expected version and SHA-256/SHA-512 digest for offline payloads.
- Repeat verification immediately before installer execution.
- Reject redirect chains that leave an approved Microsoft host set.
- Delete cached files and fail closed on any integrity failure.

### H-2: OCR downloads and loads native code without integrity verification

**Evidence**

- `src/Yagu.OcrWorker/NativeRuntime.cs:48-70` accepts cached native runtimes based only on probe-file existence.
- `src/Yagu.OcrWorker/NativeRuntime.cs:119-153` can dynamically select a newer matching Paddle runtime instead of staying strictly pinned.
- `src/Yagu.OcrWorker/NativeRuntime.cs:162-215` downloads and extracts native NuGet packages without validating a package signature or pinned digest.
- `src/Yagu.OcrWorker/TesseractWorkerEngine.cs:202-225` downloads Tesseract language data without a digest.
- Paddle model downloads use the same integrity-free cache/download pattern in `src/vendor/PaddleSharp/src/Sdcb.PaddleOCR.Models.Online/Details/Utils.cs`.
- The host verifies the OCR worker executable, but not the native libraries or models later loaded by that worker.

**Attack preconditions**

An upstream feed, mutable asset, CDN, trusted TLS path, local cache, or build host is compromised. The user must normally have approved OCR downloads.

**Impact**

Malicious native code executes in the OCR worker under the user's token. Worker process separation limits a crash but is not a security sandbox; the worker retains access to the user's files and network.

**Required fixes**

- Maintain a signed first-party manifest containing exact version, URL, size, and digest for every runtime, model, and language file.
- Verify downloaded files before extraction and verify every cache hit before loading.
- Remove dynamic version selection.
- Reject unexpected archive members.
- Download into a new temporary directory and atomically publish only after complete verification.
- Prefer bundled signed assets for release builds.
- Consider running the worker with a restricted token or AppContainer and resource-limited Job Object.

### H-3: Developer bootstrap executes numerous unverified downloads

**Evidence**

- `install-dev-prerequisites.ps1:107-114` downloads files without signature or hash validation.
- The script subsequently executes downloaded Git, Rust, .NET, Visual Studio Build Tools, Inno Setup, Everything, and WebView2 payloads, with several operations elevated.

**Attack preconditions**

A vendor/CDN, redirect, trusted TLS path, temporary directory, or developer/release host is compromised.

**Impact**

Code execution on developer and release machines, often elevated. A compromised release machine can also backdoor downstream artifacts.

**Required fixes**

- Pin versions and vendor-published hashes.
- Verify exact Authenticode publisher identities for executable installers.
- Verify a signature or pinned digest for downloaded PowerShell scripts.
- Use unique, access-restricted temporary directories.
- Fail closed before executing any unverifiable payload.

---

## Medium severity

### M-1: Tracked telemetry credential and fail-open Function authentication

**Evidence**

- `src/Yagu/telemetry.local.props:5-6` contains a configured endpoint and non-empty application token.
- Git validation confirmed the file is tracked, is not ignored, and exists in repository history.
- `.gitignore:33-36` ignores `Yagu/telemetry.local.props`, not the actual `src/Yagu/telemetry.local.props` path.
- `src/Yagu/Yagu.csproj:48-73` injects the token into configured client builds and passes it on a child-process command line.
- `src/cloud/Yagu.TelemetryFunction/TelemetryFunctions.cs:58-64` and `:90-96` expose both routes through anonymous Function triggers.
- `src/cloud/Yagu.TelemetryFunction/TelemetryFunctions.cs:256-269` accepts all requests when `YAGU_APP_TOKEN` is absent or empty.
- The example configuration and documentation permit a blank token for development.

**Impact**

The tracked value must be considered compromised. A missing production setting opens Internet-facing telemetry and bug-report ingestion, enabling telemetry poisoning, log flooding, blob creation, and consumption-cost abuse.

A token embedded in a distributed desktop binary is not durable client authentication because any recipient can extract it. It can provide abuse throttling only.

**Immediate actions**

1. Rotate the deployed token.
2. Remove `src/Yagu/telemetry.local.props` from Git tracking and correct the ignore rule.
3. Review repository access and build logs; assume the historical token is exposed.
4. Stop passing secrets through process command lines.

**Required fixes**

- Reject requests or fail Function startup when production authentication configuration is missing.
- Allow token-free operation only behind an explicit local-development setting.
- Add ingress rate limits, quotas, request throttling, and anomaly alerts.
- If sender identity is required, issue per-install short-lived credentials rather than embedding a static shared secret.
- Add regression tests for missing-token rejection.

### M-2: Custom index storage can cause broad recursive deletion

**Evidence**

- `src/Yagu/Services/SettingsService.cs:463-467` only trims a configured index directory.
- `src/Yagu/Services/Index/IContentIndexPathProvider.cs:49-68` uses the configured path directly as the storage root.
- `src/Yagu/Services/Index/IndexBuildOperations.cs:258-267` requires only an absolute path.
- `src/Yagu/Services/Index/ContentIndexManager.cs:981-1010` recursively deletes every immediate subdirectory under that root during clear-all.

**Attack preconditions**

A user selects a populated general-purpose directory, settings are maliciously modified, or a CLI configuration points storage at the wrong location; clear-all is then invoked.

**Impact**

Destruction of unrelated user data. Elevating Yagu would increase the reachable files.

**Required fixes**

- Canonicalize and validate storage before persistence and before every mutation.
- Reject drive roots, profile roots, application directories, UNC/mapped/removable/cloud-backed locations, and reparse points.
- Require a Yagu-owned marker with a versioned schema.
- Delete only validated 32-hex scope directories and explicitly recognized staging directories.
- Consider always appending a Yagu-owned child directory to a user-selected parent.

### M-3: Archive decompression is not bounded cumulatively

**Evidence**

- `src/Yagu/Services/ZipArchiveSearcher.cs:315-426` applies a per-entry metadata limit but has no archive-wide expanded-byte or entry-count budget.
- It creates and retains one `MemoryStream`/task per candidate; the semaphore limits simultaneous extraction, not cumulative retained output.
- `src/Yagu/Services/ZipArchiveSearcher.cs:806-842` does not reject oversized preview entries before copying the full extracted data.
- Preview limits are checked only after extraction completes.

**Attack preconditions**

The user searches or previews an attacker-controlled archive.

**Impact**

Memory, CPU, and disk exhaustion through many individually legal entries, deeply nested content, falsified metadata, or a preview decompression bomb.

**Required fixes**

- Copy through a counting stream that stops when actual expanded bytes exceed the per-entry limit.
- Add archive-wide expanded-byte, entry-count, compression-ratio, nesting, elapsed-time, and task-count budgets.
- Bound outstanding search tasks instead of creating one task per candidate.
- Apply identical limits to preview/editor extraction.
- Consider moving native 7-Zip parsing to a disposable, resource-limited worker.

### M-4: Line-mode regular expressions have an infinite timeout

**Evidence**

- `src/Yagu/Services/SearchRegexFactory.cs:63-75` explicitly assigns `Regex.InfiniteMatchTimeout` for line-mode expressions.
- Managed file, archive, OCR, and PDF matching can evaluate these expressions synchronously.

**Attack preconditions**

A pathological user- or semantic-generated expression is evaluated against a sufficiently long attacker-controlled line.

**Impact**

Catastrophic backtracking can consume a worker thread indefinitely, and cancellation cannot interrupt the active match.

**Required fixes**

- Apply a finite timeout to every managed regex.
- Catch `RegexMatchTimeoutException` per source and report or skip that source safely.
- Prefer `RegexOptions.NonBacktracking` when expression features permit it.

### M-5: Release application and installer artifacts are unsigned

**Evidence**

- `installer/yagu-installer.iss:75-84` describes and builds an unsigned, elevated per-machine installer.
- Build and publish scripts contain no signing stage before artifacts are uploaded.

**Impact**

Users cannot cryptographically verify Yagu's publisher, Windows reputation and Smart App Control block or distrust the product, and distribution-channel tampering is harder to detect.

**Required fixes**

- Sign `Yagu.exe`, all workers, the installer, and the uninstaller through Microsoft Trusted Signing or a protected code-signing identity.
- Sign only in a protected release environment.
- Verify all signatures after packaging and immediately before upload.
- Publish checksums, an SBOM, build provenance, and attestations.
- Protect release environments and require human approval.

### M-6: CI and dependency inputs are not reproducibly pinned

**Evidence**

- `.github/workflows/ci.yml:10-28` uses mutable major Action tags and the mutable Rust `stable` channel.
- The workflow does not declare explicit top-level permissions.
- `src/Yagu/Yagu.csproj:164-180` includes wildcard production dependency versions.
- No enforced NuGet lockfile was found.

**Impact**

Compromised tags or newly published dependency versions can affect CI or local release output. Restores and builds are not fully reproducible.

**Required fixes**

- Pin GitHub Actions to full commit SHAs.
- Declare `permissions: contents: read` unless a narrower job requires more.
- Pin SDK, Rust, and production package versions for release builds.
- Generate and enforce NuGet lockfiles with locked restore.
- Add dependency review, Dependabot/Renovate, SBOM generation, NuGet advisory scanning, and `cargo audit`.

### M-7: PDF extraction buffers unbounded child-process output

**Evidence**

- `src/Yagu/Services/Pdf/PdfTextExtractor.cs:143-176` reads complete stdout and stderr into managed strings. The timeout limits time but not output size.

**Attack preconditions**

PDF search processes a crafted file whose extractor emits a very large text stream before the timeout.

**Impact**

Main-process memory exhaustion despite parsing occurring out of process.

**Required fixes**

- Drain stdout and stderr incrementally with explicit byte/character caps.
- Kill the extractor as soon as output exceeds policy.
- Enforce a reasonable extraction expansion ratio.

### M-8: Session-file parsing permits unbounded allocation

**Evidence**

- `src/Yagu/Services/SessionFileService.cs:294-303` repeatedly doubles its buffer for an incomplete token without a maximum.
- String arrays and completed results lack strict per-string, per-result, and total-result limits around `SessionFileService.cs:572-654`.
- Loaded results are retained in the GUI collection rather than paged through the disk-backed result store.

**Attack preconditions**

A user opens an attacker-supplied `.yagu-session` file.

**Impact**

Memory exhaustion, long UI stalls, and process termination.

**Required fixes**

- Cap total file size, token/string length, context-array size, and result count.
- Reject declared counts above policy.
- Cap buffer growth.
- Page large sessions through `ResultStore`.

### M-9: OCR requests lack image and process resource limits

**Evidence**

- `src/Yagu/Services/Ocr/WorkerOcrEngine.cs:162-211` has no independent per-request deadline.
- The OCR worker processes requests synchronously, so one stuck decode blocks subsequent replies.
- Engines allocate decoded images without strict width, height, pixel-count, or decoded-byte limits.
- The OCR worker is not placed in a resource-limited kill-on-close Job Object.

**Attack preconditions**

OCR is enabled and an attacker-controlled image has extreme dimensions, triggers expensive native processing, or causes a native hang.

**Impact**

Unbounded CPU/RAM use, a stalled search pipeline, and potentially orphaned worker processes.

**Required fixes**

- Validate compressed size, dimensions, pixel count, decoded bytes, and output text before or during processing.
- Add a per-request deadline that kills and recreates the worker.
- Assign the worker to a kill-on-close Job Object with memory, process-count, and CPU controls.

### M-10: Azure Function request limits and payload shape are incomplete

**Evidence**

- `src/cloud/Yagu.TelemetryFunction/TelemetryFunctions.cs:272-284` compares `StringBuilder.Length`, which counts UTF-16 characters rather than received UTF-8 bytes.
- Event, property, measurement, key-name, and collection cardinalities are weakly bounded.

**Attack preconditions**

The endpoint is unauthenticated, the shared token is known, or a legitimate client is abused.

**Impact**

Requests can exceed the intended byte budget, create excessive telemetry fan-out, produce avoidable errors, and amplify telemetry costs.

**Required fixes**

- Bound the raw request stream in bytes before decoding.
- Configure platform/ingress request-size limits.
- Cap events, properties, measurements, names, strings, and attachment sizes.
- Reject nulls, non-finite numeric values, and oversized shapes with `400` or `413`.

---

## Low severity

### L-1: Bare-name native DLL fallbacks allow conditional preloading

**Evidence**

- `src/Yagu/Native/NativeSearcher.cs:244-256` falls back from an application-relative path to the bare name `yagu_core`.
- `src/Yagu.IndexWorker/NativeIndexEngine.cs:88-115` falls back to default OS DLL search.
- `src/Yagu/Native/EverythingSdk.cs` uses a bare P/Invoke library name.

**Impact and conditions**

If expected installation files are absent or invalid and an attacker controls a searched directory, an attacker-controlled DLL can execute at the process integrity level. Normal Program Files installations reduce practical exposure.

**Required fixes**

Load only canonical absolute paths below the application/worker directory. If unavailable or invalid, fail safely to the managed/live-search path.

### L-2: Valid caller-selected report IDs can overwrite existing blobs

**Evidence**

- `src/cloud/Yagu.TelemetryFunction/TelemetryFunctions.cs:235-254` sanitizes characters but permits any syntactically valid caller-provided identifier.
- `TelemetryFunctions.cs:195-227` uses that identifier as the blob prefix and uploads without a create-only condition.

**Impact and conditions**

A caller with the shared token and knowledge of another report's random correlation ID can replace that report's private blobs. Blind guessing remains impractical.

**Required fixes**

Always mint IDs server-side, or upload with `If-None-Match: *` and retry under a fresh server-generated ID on collision.

### L-3: Search queries and directories are persisted in the default log

**Evidence**

- Search logging records the complete query and directory at Warning level in `src/Yagu/ViewModels/MainViewModel.cs`.
- The default file log level includes Warning entries.

**Impact**

Search terms and filesystem locations persist in `%APPDATA%\Yagu\yagu.log` and can be included in an explicitly submitted bug report.

**Required fixes**

Move raw query/path logging to Verbose, log lengths or redacted values at Warning, and apply path/query scrubbing where full values are unnecessary.

---

## Existing controls that appear sound

- `WinVerifyTrust` verification uses whole-chain revocation and fails closed on errors.
- Downloaded Everything installers are checked against an expected publisher before elevation.
- Application updates require expected architecture/name, GitHub SHA-256 metadata, size validation, and same-publisher Authenticode checks.
- Signed hosts require OCR, semantic, and index workers to carry the same trusted publisher signature.
- Worker IPC uses inherited private standard-I/O handles rather than public network or named endpoints.
- Index workers use kill-on-close Job Objects and protocol/version checks.
- Blob access uses managed identity rather than storage account keys.
- Bug-report containers are requested with `PublicAccessType.None`.
- Correlation IDs reject path separators, control characters, and excessive lengths.
- Shared-token comparison uses `CryptographicOperations.FixedTimeEquals`.
- Archive preview uses random GUID temporary names and does not use archive entry names as extraction filesystem paths, preventing Zip Slip in that path.
- HTML report values are encoded.
- No concrete request-controlled SSRF or shell-command injection path was identified.
- Telemetry remains offline by default in committed application source when no generated configuration is supplied.

---

## Validation performed

- A NuGet direct/transitive advisory query reported no known vulnerable packages from the configured sources at audit time.
- A redacted heuristic scan found no additional tracked high-entropy credential-like assignments outside the known telemetry configuration.
- Git metadata confirmed `src/Yagu/telemetry.local.props` was tracked, not ignored, configured, and present in history; its secret value was not printed.
- `cargo-audit` was not installed, so current Rust advisory status was not verified.
- No penetration testing, fuzzing, deployed-Azure configuration review, or runtime exploit testing was performed.

## Follow-up verification after fixes

After remediation:

1. Add or extend focused regression tests in `SecurityAuditRegressionTests` and the relevant service test files.
2. Run the iterative Yagu test suite.
3. Run `dotnet list Yagu.slnx package --vulnerable --include-transitive`.
4. Install and run `cargo audit` for `src/yagu-core`.
5. Build installers in a clean release environment and verify every Authenticode signature and published digest.
6. Test the Azure Function with missing, blank, incorrect, and correct authentication configuration, plus oversized and high-cardinality requests.
7. Fuzz archive/session parsers and Rust FFI boundaries with strict memory/time limits.

---

## Previous security improvements made

This section records protections that were implemented before this audit and remain present in the
current working tree. It is intentionally separate from the findings above: an existing control may
substantially reduce a risk without eliminating the remaining issue.

### How this history was determined

- The local coding-session index was searched for Yagu security, Authenticode, telemetry, binary-
	planting, injection, and archive-hardening work. Its retained turn history covers approximately
	May 2 through June 2, 2026, so it does not contain the later July implementation conversations.
- Git history was therefore used for the later work. The principal security commits found were:
	- `1e9a065` (July 2, 2026), which added the initial security-audit fixes.
	- `5665e45` (July 5, 2026), which refreshed the security regression pins after the Everything
		asset-path refactor.
	- `3bb09d6` (July 8, 2026), which removed the OCR worker's user-writable fallback to prevent binary
		planting.
	- `ce7a576` (July 8, 2026), which added same-publisher verification for OCR and semantic workers.
	- `4c3b34e` (July 10, 2026), which kept the Authenticode-protected OCR path compiling in Release CI.
- Current source and regression tests were then inspected to verify that each control still exists.

### Downloaded Everything installer verification

Yagu previously added `AuthenticodeVerifier`, backed by `WinVerifyTrust`, before allowing a downloaded
or bundled Everything installer to run elevated.

Implemented protections:

- Whole-chain revocation checking is enabled with `WTD_REVOKE_WHOLECHAIN` and
	`WTD_REVOCATION_CHECK_CHAIN`.
- Verification is fail-closed: missing, unsigned, tampered, revoked, untrusted, and wrong-publisher
	files are rejected.
- The signer subject must contain the expected voidtools publisher identity.
- Both the GUI and CLI verify the installer before using `runas`, delete a failed downloaded payload,
	and fall back to built-in enumeration.
- Everything download URLs are centralized and required to use HTTPS.

Current evidence:

- `src/Yagu/Services/AuthenticodeVerifier.cs`
- `src/Yagu/UI/Windows/MainWindow/MainWindow.StartupChecks.cs`
- `src/Yagu/CliRunner.cs`
- `tests/Yagu.Tests/SecurityAuditRegressionTests.cs`
- `tests/Yagu.Tests/EverythingSearchDialogRegressionTests.cs`

This was introduced by the July 2 security pass (`1e9a065`) and retained through the later
`EverythingAssetPaths` refactor (`5665e45`). It materially reduces remote software-integrity risk,
although path-based verification still does not eliminate every local file-swap/TOCTOU possibility.

### Worker binary-planting defenses

Yagu hardened its out-of-process worker launch paths after identifying that automatically executing a
worker from a predictable user-writable directory could let malware plant a replacement executable.

Implemented protections:

- Production OCR worker resolution no longer probes `%LOCALAPPDATA%`; it resolves from the
	application installation directory. The explicit override remains a development/test mechanism.
- Signed Yagu hosts require OCR, semantic, and index worker executables to have a valid Authenticode
	signature from the exact same publisher as the host.
- Worker trust is checked before `Process.Start()`.
- Worker processes communicate through inherited private standard-I/O handles rather than public TCP
	listeners or named endpoints.
- Worker startup uses `UseShellExecute = false`, redirected streams, no visible console, and explicit
	UTF-8 protocol encodings.
- The index worker is assigned to a Windows kill-on-close Job Object so it normally cannot survive a
	host crash or force-close; startup orphan cleanup provides a secondary backstop.
- Worker protocol/version handshakes and startup deadlines reject incompatible or non-responsive
	children.

Current evidence:

- `src/Yagu/Services/Ocr/WorkerOcrEngine.cs`
- `src/Yagu/Services/Ai/Worker/WorkerSemanticQueryTranslator.cs`
- `src/Yagu/Services/Index/IndexWorkerClient.cs`
- `src/Yagu/Services/Index/WindowsJobObject.cs`
- `src/Yagu/Services/OrphanedWorkerCleanup.cs`
- `tests/Yagu.Tests/WorkerOcrEngineTests.cs`
- `tests/Yagu.Tests/WorkerSemanticQueryTranslatorTests.cs`
- `tests/Yagu.Tests/Index/IndexWorkerSourcePinTests.cs`
- `tests/Yagu.Tests/SecurityAuditRegressionTests.cs`

The OCR path restriction was committed in `3bb09d6`; same-publisher OCR and semantic checks followed
in `ce7a576`. The check is deliberately inactive for an unsigned development host, so signing the
released host and workers remains necessary for this control to protect production builds.

### Application-update integrity and consent checks

The current working tree contains a defense-in-depth update flow that does not treat a successful
HTTPS download as sufficient proof of authenticity.

Implemented protections:

- No GitHub request is made on launch until the user approves the update check.
- Release metadata is read through a 512 KB bounded stream.
- Draft and prerelease releases are rejected.
- The selected installer must have the exact expected version/architecture filename, a positive size,
	an HTTPS `github.com` URL, and a valid GitHub SHA-256 digest.
- The downloaded file must match both the expected byte length and SHA-256 digest.
- The installer and running host must both have trusted Authenticode signatures from the same exact
	publisher; an unsigned host is not allowed to auto-launch an updater.
- Size/hash and publisher verification are repeated immediately before the `runas` boundary.
- Failed files are deleted, Yagu remains running if launch fails, and the app exits only after the
	trusted installer starts successfully.

Current evidence:

- `src/Yagu/Services/AppUpdateChecker.cs`
- `src/Yagu/Services/MultiTermUpdateDownloader.cs`
- `src/Yagu/UI/Windows/MainWindow/MainWindow.AppUpdate.cs`
- `tests/Yagu.Tests/AppUpdateCheckerTests.cs`

These files are part of the current uncommitted working tree, so no committed implementation date was
available from Git history. The controls are present in source and pinned by tests. They do not replace
the need to sign release artifacts or close the remaining writable-staging/TOCTOU window.

### Argument-injection prevention for external tools

The July 2 security pass changed caller-influenced `es.exe` queries from a manually composed raw
argument string to `ProcessStartInfo.ArgumentList` in the production path.

Implemented protections:

- The user-typed directory prefix is passed as one argument, so quotes, spaces, or text resembling
	command-line switches cannot escape the query and inject extra `es.exe` options.
- Other important process paths, including PDF extraction and worker launch, also use structured
	argument lists or fixed argument tokens rather than invoking a command shell with concatenated user
	input.

Current evidence:

- `src/Yagu/Services/DirectoryAutoCompleteService.cs`
- `tests/Yagu.Tests/SecurityAuditRegressionTests.cs`

This was introduced in `1e9a065` and is regression-pinned.

### Telemetry privacy and consent architecture

Yagu previously introduced a privacy-oriented telemetry architecture designed to be inert unless both
build-time configuration and runtime consent are present.

Implemented protections:

- Committed default constants use a placeholder HTTPS endpoint and an empty token, causing
	`TelemetryConfig.IsConfigured` to remain false in an unconfigured build.
- Real endpoint values are designed to be generated under `obj` at build time from local properties
	or environment variables instead of being hard-coded into the logic source.
- Telemetry endpoints must be absolute HTTPS URLs.
- `TelemetryGate` controls all automatic sending; disabled, unconfigured, or headless paths are hard
	no-ops.
- Automatic exception telemetry passes messages, stacks, and free-text properties through
	`TelemetryScrubber`, which redacts drive-letter and UNC paths while retaining only a short extension
	where useful.
- The silent channel does not intentionally include search queries, file contents, directory names,
	or machine identifiers.
- The telemetry queue is capped at 256 events and drops excess entries rather than growing without
	bound during an error storm.
- Explicit bug reports are separate from silent telemetry: the user can review the report before
	submission, and settings/log attachments are capped to 256 KB each.
- The telemetry consent modal is sequenced through the awaited startup-modal chain, preventing consent
	prompts from racing or stacking with other startup dialogs.

Current evidence:

- `src/Yagu/Services/Telemetry/TelemetryConfig.cs`
- `src/Yagu/Services/Telemetry/TelemetryConfig.Defaults.cs`
- `src/Yagu/Services/Telemetry/TelemetryGate.cs`
- `src/Yagu/Services/Telemetry/TelemetryScrubber.cs`
- `src/Yagu/Services/Telemetry/TelemetryService.cs`
- `src/Yagu/Services/Telemetry/BugReportService.cs`
- `tests/Yagu.Tests/TelemetryInjectionTests.cs`
- `tests/Yagu.Tests/TelemetryScrubberTests.cs`
- `tests/Yagu.Tests/TelemetryGateTests.cs`
- `tests/Yagu.Tests/TelemetryConsentStartupSequencingTests.cs`

The architecture predates this audit and was present in the June 30/July 5 history. However, the
current tracked `src/Yagu/telemetry.local.props` file violates the intended secret-separation design;
that regression is recorded as M-1 and must still be fixed.

### Azure telemetry Function hardening

The Function proxy already contains several controls that keep privileged cloud credentials and bug-
report data out of the desktop client.

Implemented protections:

- Application Insights and storage credentials remain server-side.
- Blob access uses `DefaultAzureCredential`/managed identity rather than embedding storage account
	keys in the client.
- The configured shared token is compared with
	`CryptographicOperations.FixedTimeEquals`, avoiding ordinary secret-dependent string comparison.
- Caller-supplied correlation IDs are length-bounded and restricted to ASCII letters, digits, and
	hyphens before being used in blob names; invalid values are replaced with a server-generated GUID.
- The bug-report container is created with `PublicAccessType.None`.
- Application-level request-size checks and bounded body reads reject oversized individual payloads.

Current evidence:

- `src/cloud/Yagu.TelemetryFunction/Program.cs`
- `src/cloud/Yagu.TelemetryFunction/TelemetryFunctions.cs`
- `tests/Yagu.Tests/SecurityAuditRegressionTests.cs`

Correlation-ID sanitization and its regression test were part of `1e9a065`. These controls prevent
path/blob-name injection but do not yet prevent reuse of another valid ID, and the token gate still
fails open when its setting is absent; those residual issues are recorded as L-2 and M-1.

### OCR download-consent enforcement

OCR assets can involve hundreds of megabytes of external downloads. Yagu added an explicit consent
gate so OCR cannot silently fetch missing components.

Implemented protections:

- The engine describes missing components and approximate download size before requesting approval.
- Concurrent initialization is serialized so multiple files do not produce overlapping prompts.
- Headless hosts without previously recorded consent fail closed instead of downloading.
- The parent sets `YAGU_OCR_ALLOW_DOWNLOAD=1` only after consent; the worker's actual download sites
	enforce that authorization rather than relying solely on UI state.
- If the parent incorrectly believes assets are already present, download authorization stays off and
	the worker fails instead of silently fetching them.

Current evidence:

- `src/Yagu/Services/Ocr/OcrDownloadGate.cs`
- `src/Yagu/Services/Ocr/WorkerOcrEngine.cs`
- `src/Yagu.OcrWorker/DownloadGuard.cs`
- OCR consent and download-guard tests under `tests/Yagu.Tests`.

This control protects user consent and unexpected network use. It does not authenticate downloaded
content; unverified OCR payload integrity remains H-2.

### Archive extraction path-traversal prevention

Archive entry names are not written directly to arbitrary filesystem paths for preview extraction.

Implemented protections:

- Preview output is placed under a Yagu temporary directory using a fresh GUID filename.
- Only the entry extension is retained; directory components and the original archive entry name are
	not used as the output path.
- OCR/native NuGet staging flattens selected members through `Path.GetFileName`, preventing archive
	paths such as `../` from escaping the destination in that extraction path.

Current evidence:

- `src/Yagu/Services/ZipArchiveSearcher.cs`
- `src/Yagu.OcrWorker/NativeRuntime.cs`
- archive tests under `tests/Yagu.Tests/ZipArchiveSearcherTests.cs`.

These controls address Zip Slip/path traversal. They do not impose sufficient decompression budgets;
archive resource-exhaustion remains M-3.

### HTML report output encoding

Generated HTML reports encode all caller- and file-controlled text before inserting it into markup.

Implemented protections:

- Queries, titles, paths, file names, match text, context lines, and multiline markers pass through
	`WebUtility.HtmlEncode`.
- Highlight markup is generated only after separately encoding the text before, inside, and after the
	matched range.

Current evidence:

- `src/Yagu/Services/HtmlReportExportService.cs`
- HTML report tests under `tests/Yagu.Tests`.

This prevents searched file contents and paths from becoming executable HTML/script in exported
reports.

### Native and content-index integrity checks

Several defensive checks were added around native output and on-disk index data.

Implemented protections:

- Native result-buffer parsing rejects buffers larger than the managed addressable range, checks every
	record boundary and string length, prevents integer overflow, clamps native numeric values before
	constructing UI models, and converts malformed native output into an error/fallback rather than
	reading past the buffer.
- Managed exceptions are contained at the native boundary rather than being allowed to unwind through
	unmanaged callbacks.
- Content-index files carry trailing SHA-256 digests, use constant-time digest comparison, and are
	rejected on truncation or mismatch.
- Index formats also validate versions, section lengths, record boundaries, and freshness metadata;
	uncertain/corrupt index state falls toward live scanning instead of trusting a potentially stale
	prune decision.

Current evidence:

- `src/Yagu/Native/NativeSearcher.cs`
- `src/Yagu/Services/Index/ChecksummedFile.cs`
- index serializers/readers under `src/Yagu/Services/Index` and their tests.

The SHA-256 trailer detects accidental corruption and torn writes. It is not a keyed authenticity
mechanism, so another principal who can rewrite a custom/shared index can recompute it; restricting
custom index storage remains part of M-2.

### Regression tests and repository security invariants

Security-sensitive behavior is explicitly pinned so later refactors cannot silently remove it.

Current coverage includes:

- Fail-safe Authenticode behavior and whole-chain revocation flags.
- Verify-before-elevate ordering for GUI and CLI Everything installers.
- Same-publisher checks before OCR, semantic, and index worker launch.
- HTTPS-only Everything download construction.
- Correlation-ID sanitization and private blob-container policy.
- `ArgumentList` use for caller-influenced `es.exe` queries.
- App-update size, digest, publisher, consent, and `runas` ordering.
- Telemetry offline-defaults, injection plumbing, privacy scrubbing, and consent sequencing.
- Worker path-resolution rules preventing a per-user-writable fallback.

Primary evidence:

- `tests/Yagu.Tests/SecurityAuditRegressionTests.cs`
- `tests/Yagu.Tests/AppUpdateCheckerTests.cs`
- `tests/Yagu.Tests/TelemetryInjectionTests.cs`
- `tests/Yagu.Tests/TelemetryScrubberTests.cs`
- `tests/Yagu.Tests/WorkerOcrEngineTests.cs`
- `tests/Yagu.Tests/WorkerSemanticQueryTranslatorTests.cs`
- `tests/Yagu.Tests/Index/IndexWorkerSourcePinTests.cs`

The source-pin tests complement behavioral tests for WinUI-, cloud-, and worker-coupled paths that are
not directly executed inside `Yagu.Tests`.
