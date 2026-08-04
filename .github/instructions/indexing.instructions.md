---
description: "Yagu content-index invariants, atomic build/update semantics, freshness safety, worker isolation, storage measurement, and UI/CLI documentation parity. Use when: content index, indexing, Build now, rebuild index, incremental refresh, USN journal, index checkpoint, index worker, index storage, index size, format-v3, PDF-text index, index status, index repair, index validation, interrupted indexing, resume indexing."
applyTo: "src/Yagu/Services/Index/**, src/Yagu/Services/ResourceUsageMonitor.cs, src/Yagu/ViewModels/MainViewModel*.cs, src/Yagu/UI/Windows/Settings/SettingsWindow.Index*.cs, src/Yagu/UI/Windows/MainWindow/MainWindow.IndexOnboarding.cs, src/Yagu/UI/Windows/MainWindow/MainWindow.StartupChecks.cs, src/Yagu/UI/Windows/MainWindow/MainWindow.xaml, src/Yagu/CliRunner.cs, tests/Yagu.Tests/**/*Index*.cs, tests/Yagu.Tests/ResourceUsage*.cs, HELP.md"
---

# Yagu - Content Indexing Invariants

Yagu's content index is an optional, local candidate-pruning accelerator. It is never the
authoritative source of search results. The normal scanner reads every retained candidate live, and
any file or query that cannot be proven safe to prune must live-scan. Preserve this invariant:

> An unavailable, stale, interrupted, incompatible, or corrupt index may reduce acceleration, but
> it must never suppress a possible match or change search results.

## Full builds are staged, atomic, and do not resume

- `IndexBuildExecutor.BuildFullScopeUnderLease` creates a `ContentIndexBuildTransaction`.
- The transaction writes the complete replacement into a private
  `<IndexRoot>\.build-<guid>` workspace. Paged base/segment output inside that workspace is not a
  user-visible checkpoint and must never be treated as a resumable build.
- Only after the raw index, optional PDF namespace, manifests, sidecars, and checksums are ready may
  `ContentIndexBuildTransaction.Commit` import immutable artifacts and flip the live pointer.
- Cancellation or a handled failure disposes the uncommitted transaction and deletes its workspace.
  A hard crash can leave the workspace behind; `IndexStorageRecovery` removes abandoned `.build-*`
  workspaces without publishing them.
- The previously committed index remains live throughout a replacement build. If the interrupted
  operation was the first build, no index is published.
- The next full build starts again from the beginning. There is no durable per-file resume cursor.
  Do not claim that cancelling, killing Yagu, ending the worker, or losing power resumes a full build
  from its displayed percentage.
- A Pause/Resume UI command controls whether background maintenance may run. It does not turn an
  interrupted full-build workspace into a durable checkpoint; if the operation was cancelled, the
  replacement build starts fresh.
- Real application exit paths must warn and require explicit confirmation while a tracked index
  operation is active. Preserve close-to-tray as a safe non-exit path. Cover the window close button,
  tray Exit, update-driven exit, and `WM_QUERYENDSESSION`: deny a Windows restart/shutdown/sign-out
  request while indexing, bring Yagu forward, and explain that the user must retry the Windows
  operation after indexing finishes or after explicitly exiting. The warning must distinguish full
  builds (partial workspace discarded; complete build starts over) from incremental updates
  (replay from the last committed checkpoint; rebuild only if continuity cannot be proven).

This all-or-nothing design is intentional. Reusing an incomplete full build could make missing
postings look authoritative and incorrectly prune files.

## Incremental refreshes continue from the last committed checkpoint

- `ContentIndexIncrementalRefresher` reads the volume change journal from the active manifest's
  `FreshnessCheckpoint`.
- It resolves the bounded journal window, produces an immutable delta segment, and passes the
  window's `NextCheckpoint` to `ContentIndexIncrementalUpdater`.
- Publishing the segment and extending the active pointer/checkpoint is atomic. Never advance a
  checkpoint independently of the segment that represents that journal range.
- If a refresh is interrupted before publication, the active checkpoint does not advance. The next
  refresh reads again from the last committed checkpoint and reprocesses the uncommitted journal
  range. This is safe forward progress from the last commit, not mid-operation resume.
- File-system watcher events are latency hints only. The change journal remains authoritative, so
  changes made while Yagu is closed are still discovered.
- A journal reset, gap, bounded catch-up stop, missing/untrusted base, identity mismatch, or resolver
  failure must fail closed. Return the appropriate rebuild/attention outcome and live-scan affected
  files; never trust a partial delta.
- Automatic incremental mode must not silently escalate an unprovable journal history into an
  expensive full rebuild, except for the explicit legacy ReFS identity-compatibility migration.

## Publication, storage, and recovery

- All durable index files are checksummed. The live generation is selected through validated,
  redundant pointer state; retention must not remove a generation referenced by a valid pointer.
- Preserve the single-writer lease (`IndexMutationContext` / `IndexWriteLease`) for every mutation.
  Query readers may coexist with immutable old generations while a replacement is staged.
- Disk quota, reserved free-space, and stop-when-full checks must fail before an unsafe publication.
  Disk-full leaves the prior committed generation unchanged.
- The status-bar index-size value is the physical size of every file under the configured index
  storage root, not only active generations. It deliberately includes retained generations, PDF/v3
  data, staging, quarantine, and recovery residue so it matches disk consumption. Keep measurement
  off the UI thread, cached for at least one minute, and suppressed/cancelled during searches. Honor
  `FileListerBackend`: forced SDK uses one size-only query serialized through `EverythingSdk.Lock`;
  forced es.exe starts the detected executable with `-get-total-size`, `-size-format 1`, and
  `-no-digit-grouping`; Managed walks file metadata; Auto follows SDK → es.exe → Managed. When the
  selected Everything route is unavailable or incomplete, use the cancellation-aware managed scan.
- Compaction follows the same immutable-layer/pointer discipline. It must not expose a half-compacted
  base or advance freshness beyond represented content.
- `ContentIndexRecoverySpool` is a search-time prune fail-safe. It is not an index-build resume log.
  Do not conflate it with build recovery or incremental checkpoints.
- Extended PDF text is a separate namespace. A replacement must either be proven compatible and
  committed with the raw generation, deliberately preserved, or safely disabled/deleted; stale PDF
  pruning must never survive a failed determinism/fingerprint proof.
- Format-v3 sidecars are optional query structures. Missing or incompatible sidecars must safely
  fall back to another reader or a live scan; they cannot change result semantics.

## Worker isolation boundary

- Query work normally uses the long-lived `Yagu.IndexWorker`; builds, refreshes, compaction,
  validation, and PDF population use a short-lived maintenance worker.
- If a worker is unavailable before accepting work, `IndexBuildCoordinator` may retry once
  in-process with the same core semantics.
- Once the worker has accepted memory-heavy work, a crash/failure is surfaced. Do not repeat that
  workload automatically inside the main Yagu process, because doing so defeats fault/OOM isolation.
- Cancellation, busy/lease conflicts, typed failures, and disk-full are not worker-unavailability
  fallbacks.

## Scope and query safety

- Registered roots form a non-overlapping maintained coverage set. A registered ancestor can serve a
  descendant search, but discovery remains scoped to the user's requested directory.
- Never open parent and child postings together. Broader registrations consolidate narrower roots;
  redundant on-disk child data is inert until explicitly deleted.
- Per-root include/exclude globs are build-time ingestion policy. Excluded or otherwise unindexed
  files live-scan; they are not omitted from search results.
- Query-family switches only narrow eligibility. Unsupported/unsafe regex shapes, non-ASCII
  case-insensitive queries, excessive candidate ratios, memory/size limits, startup timeout, stale
  layers, and unsupported binary cases must bypass acceleration and live-scan.
- The per-result "indexed" badge is provenance: the index selected the file as a candidate. It does
  not mean match content came from the index.
- Raw-file indexing is orthogonal to OCR, PDF extraction, and archive traversal. Raw pruning must not
  suppress extracted-content sources.

## User-facing behavior and documentation

- Keep GUI, CLI, `Yagu.exe --help`, and `HELP.md` behavior in sync. Index management includes use/
  bypass, build, forced rebuild, status, validation/repair/delete/clear, registered-root management,
  per-root filters, and persisted `--index-config` settings.
- Use these terms consistently:
  - **Build now**: create a complete staged generation for the selected maintained scope. If a
    complete index already exists, it remains active until the new generation commits.
  - **Rebuild**: explicitly request a complete staged replacement, normally after build-output
    settings change or when existing data needs replacement/repair.
  - **Validate**: re-read and verify the current stored index; it does not rebuild.
  - **Repair**: create a safe replacement for recoverable broken/incompatible data.
  - **Remove folder**: unregister maintenance without necessarily deleting stored index data.
  - **Delete index**: remove stored data for a scope.
- When discussing interruption, say:
  - Full build: partial work is discarded; the next full build starts over.
  - Incremental refresh: the next pass replays from the last committed USN checkpoint.
  - In both cases, the previous complete index remains unchanged until a new commit succeeds.
- Readiness prompts must distinguish recoverable incremental catch-up from a required rebuild.
  **Increase limit & update** raises the bounded replay limit and retries incremental proof; it must
  not imply that a partial build is being resumed.
- Query-shape or selectivity bypass cannot be fixed by rebuilding and must not offer misleading
  rebuild actions.

## Testing expectations

- Full-build tests must cover cancellation/failure before commit, pointer-flip fault points,
  abandoned-workspace recovery, prior-generation preservation, and failed-first-build publication.
- Incremental tests must prove that the checkpoint advances only with its delta, cancellation leaves
  it unchanged, journal discontinuity requests attention/rebuild, and compaction preserves freshness.
- Keep failure-injection coverage around every mutation boundary in `IndexMutationFaults`.
- UI/VM/CLI wiring that is not compiled into `Yagu.Tests` requires source-pin tests; pure index
  services receive runtime unit tests.
- For local validation, use the repository's iterative test filter unless full/slow coverage is
  explicitly requested. Follow `.github/instructions/testing.instructions.md`.
