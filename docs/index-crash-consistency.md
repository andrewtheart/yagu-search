# Content-index process-crash consistency

Yagu treats the on-disk content index as a cache: after abrupt process termination, a reader must observe
one complete prior state, one complete committed state, or no trusted index (which forces a live scan). It
must never trust a partial or mixed layer set.

## Scope

This contract covers abrupt termination of `Yagu.exe` or `Yagu.IndexWorker.exe` at any named durable
mutation boundary on local NTFS/ReFS storage. The test harness kills the worker process without unwinding,
so managed `catch`/`finally` cleanup does not run.

Sudden power loss and storage devices that acknowledge a flush without persisting it are a separate hardware
failure model. Core checksummed files, additional format-v3 query files stored beside each index layer,
marker files, and pointer temp files are flushed to disk before publication, but Yagu does not claim to
control a drive's volatile write cache.

## Commit invariants

1. Base generations and delta segments are immutable after publication.
2. Every artifact is fully written, checksummed, structurally validated, and marked uncommitted before its
   directory is promoted to a final ID.
3. A pointer slot is the only raw-index visibility switch. It is written to a same-directory temp file,
   flushed, then atomically replaces the older/invalid redundant slot.
4. Before the pointer switch, an initial build exposes no index and a rebuild/incremental operation exposes
   the prior complete index. After the switch, the new complete artifact set is active.
5. Uncommitted markers distinguish a promoted-but-unreferenced artifact from retained history. Recovery
   deletes unreferenced marked artifacts and clears markers from referenced committed artifacts.
6. Retention and residue cleanup happen after the commit point, are idempotent, and cannot turn a committed
   operation into a failure. The next writer-lease acquisition retries cleanup.
7. PDF negative pruning has a durable disabled state. Once extraction determinism/fingerprinting fails,
   recovery deletes backups instead of resurrecting an unsafe old namespace. A complete replacement carries
   a ready marker and can safely clear the disabled state.
8. An incremental change that cannot be read is tombstoned before the journal checkpoint advances. The file
   therefore live-scans; stale content from an older layer can never become a trusted nonmember.
9. Re-anchor rewrites the manifest through a validated temp file. A crash leaves the old or new complete
   manifest. The subsequent pointer-sequence update is cache invalidation, not the content commit point.
10. The cross-process writer lease is released by the OS on process death. Every later lease acquisition
    runs recovery before allowing another mutation.

## Automatic recovery

Recovery removes abandoned `.build-*` workspaces, pointer temp files, generation/segment temp directories,
unreferenced marked artifacts, re-anchor/v3 temp files, extended-source publish temps, and stale backups. It
also completes or preserves durable PDF disabled/replacement state. Recovery is safe to run repeatedly.
Read-only searches do not need to wait for cleanup: checksums, markers, directory presence, and redundant
pointers already make any incomplete state fail closed to a live scan.

## Exhaustive hard-crash matrix

`IndexCrashRecoveryTests` launches the Debug index worker for a real filesystem mutation, waits until one
selected `IndexMutationFaults` boundary is durably logged, kills that exact process, inspects the pre-recovery
state, runs automatic recovery twice, and validates both state and residue.

The matrix covers:

- first one-batch and paged builds;
- rebuild over a prior complete index;
- checksummed core-file and additional format-v3 query-file write phases;
- base and segment write, validation, marker, promotion, pointer, and cleanup phases;
- staged base/segment import and its sole live pointer switch;
- incremental append;
- bounded small-segment coalescing;
- full compaction;
- manifest re-anchor and cache-invalidation pointer update;
- PDF replace and durable delete/disable;
- standalone extended-source replacement;
- recovery interrupted during workspace cleanup, per-scope reconciliation, PDF restore/backup deletion, and
  final completion.

A reflection-backed inventory test fails if a new named durability boundary is added without a hard-crash
case. This makes boundary coverage an enforced 100% set rather than a prose claim.
