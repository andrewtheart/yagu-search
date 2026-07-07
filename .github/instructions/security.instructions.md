---
description: "Yagu security invariants — verify Authenticode before running downloaded installers, keep telemetry offline-by-default with build-time secret injection, sanitize Function inputs, and constant-time token compare. Use when: editing AuthenticodeVerifier, downloading and running an installer elevated (runas), Everything Search installer, TelemetryConfig, TelemetryScrubber, telemetry token/secret, telemetry.local.props, the TelemetryFunction, bugreport correlationId, blob path, SanitizeCorrelationId, FixedTimeEquals, Zip Slip, SecurityAuditRegressionTests."
applyTo: "src/Yagu/Services/AuthenticodeVerifier.cs, src/Yagu/Services/Telemetry/**, src/cloud/Yagu.TelemetryFunction/**, src/Yagu/UI/Windows/MainWindow/MainWindow.StartupChecks.cs, src/Yagu/CliRunner.cs"
---

# Yagu — Security Invariants

These fixes are pinned by `tests/Yagu.Tests/SecurityAuditRegressionTests.cs`. **Do not regress them.**

## Verify signatures before running downloaded executables

The voidtools **Everything Search** installer is downloaded and then run elevated (`Verb = "runas"`)
in **both** `MainWindow.StartupChecks.cs` and `CliRunner.cs`. Before launching it you MUST call
`AuthenticodeVerifier.IsTrustedPublisher(tempPath, "voidtools", out _)` and **refuse + delete** the
file on failure (OWASP A08). `AuthenticodeVerifier` wraps `WinVerifyTrust`
(`WINTRUST_ACTION_GENERIC_VERIFY_V2`, whole-chain revocation) plus a subject-publisher check and
**fails safe** (any exception ⇒ untrusted). It is linked into `Yagu.Tests` via `<Compile Include>`.
Never add a new "download an EXE and run it" path without this gate.

## Telemetry ships offline-by-default; secrets are build-time-injected, never committed

- Committed source MUST keep telemetry disabled: `Services/Telemetry/TelemetryConfig.Defaults.cs`
  holds `ProxyBaseUrl = PlaceholderBaseUrl; AppToken = ""`, so a fresh clone / CI build has
  `TelemetryConfig.IsConfigured == false` and sends nothing. Never commit a real proxy URL or token.
- Real values are injected at build time only, from the **gitignored** `Yagu/telemetry.local.props`
  (or env `YAGU_PROXY_URL` / `YAGU_APP_TOKEN`) via the `GenerateYaguTelemetryConfig` MSBuild target,
  which writes `obj/.../TelemetryConfig.Configured.g.cs` and swaps out the placeholder partial.
  Compute the generated path **inside** the target — `$(IntermediateOutputPath)` is empty during body
  evaluation, so a body-level path drops the `obj\` prefix and lands the secret file in the project
  root (not gitignored).
- `Yagu.Tests` compiles the placeholder partial (not the generated file), so tests are always
  offline/deterministic. Telemetry is opt-in at runtime (`AppSettings.TelemetryEnabled` default false)
  and only emits on a critical unhandled error.

## Function (`Yagu.TelemetryFunction`) input handling

- Any caller-supplied `correlationId` used in a blob path (`{id}/settings.json`) or echoed back MUST
  go through `SanitizeCorrelationId` (restrict to `[a-zA-Z0-9-]`, max 64 chars, else a fresh
  `Guid.NewGuid().ToString("N")`). **Never** assign `correlationId = payload.CorrelationId` verbatim.
  The bug-report container stays `PublicAccessType.None`.
- Keep the existing controls: token comparison uses `CryptographicOperations.FixedTimeEquals` (not
  `==`); the telemetry silent path scrubs filesystem paths via `TelemetryScrubber`; archive/OCR/nupkg
  extraction uses `Path.GetFileName` (no Zip Slip); zip-preview extracts to a random GUID temp name.
