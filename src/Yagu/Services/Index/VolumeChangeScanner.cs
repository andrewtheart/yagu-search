using Microsoft.Extensions.Logging;
using Yagu.Services.Logging;

namespace Yagu.Services.Index;

/// <summary>
/// The outcome of a volume rescan that substitutes for a lost change-journal interval (plan §3.5).
/// <para>
/// <see cref="ChangedIdentities"/> are ordinary <see cref="UsnChange"/> records, so a caller feeds them
/// through the same <see cref="ContentIndexUsnChangeResolver"/> a normal journal replay uses — the rescan
/// only replaces <i>how</i> the changed set was discovered, never how it is applied.
/// <see cref="UnprovablePaths"/> are files whose last-change USN could not be read (denied/locked); they
/// are tombstoned so they live-scan rather than being trusted.
/// </para>
/// </summary>
internal readonly record struct VolumeChangeScanResult(
    bool Succeeded,
    string? Failure,
    IReadOnlyList<UsnChange> ChangedIdentities,
    IReadOnlyList<string> UnprovablePaths,
    long FilesExamined)
{
    public static VolumeChangeScanResult Failed(string failure)
        => new(false, failure, Array.Empty<UsnChange>(), Array.Empty<string>(), 0);
}

/// <summary>
/// Discovers every file under an indexed root that changed after a checkpoint, <b>without</b> replaying the
/// change journal. This is the recovery path for a journal whose retention window has moved past the index's
/// checkpoint (<see cref="UsnReadStatus.GapDetected"/>) or that is farther behind than the configured
/// catch-up limit (<see cref="UsnReadStatus.Incomplete"/>) — situations that previously forced a full
/// rebuild of the whole root.
/// <para>
/// It is only valid when the journal <b>id is unchanged</b>, because it compares each file's persisted
/// last-change USN against the index checkpoint's USN; a journal reset renumbers USNs and makes that
/// comparison meaningless. <see cref="UsnJournalReader.TryCollectChanges"/> reports
/// <see cref="UsnReadStatus.JournalIdChanged"/> / <see cref="UsnReadStatus.CheckpointAhead"/> before it
/// reports a gap, so a gap already implies continuous numbering.
/// </para>
/// </summary>
internal interface IVolumeChangeScanner : IDisposable
{
    /// <summary>Short name for diagnostics (also the reason a scanner was chosen or skipped).</summary>
    string Name { get; }

    /// <summary>
    /// Returns the changes after <paramref name="since"/> for files under <paramref name="normalizedRoot"/>.
    /// Never throws except <see cref="OperationCanceledException"/>; any other failure is reported as
    /// <see cref="VolumeChangeScanResult.Succeeded"/> = false so the caller falls back.
    /// </summary>
    VolumeChangeScanResult Scan(
        string normalizedRoot,
        UsnCheckpoint since,
        IndexIngestionPolicy policy,
        string excludedStorageRoot,
        int parallelism,
        Action<long>? progress,
        CancellationToken cancellationToken);
}

/// <summary>
/// Tries each scanner in order and returns the first successful result. Production order is
/// <see cref="MftUsnChangeScanner"/> (fast, elevated-only) then <see cref="PerFileUsnChangeScanner"/>
/// (universal), so an elevated session gets the cheap sweep and everyone else still avoids a full rebuild.
/// </summary>
internal sealed class FallbackVolumeChangeScanner : IVolumeChangeScanner
{
    private readonly IReadOnlyList<IVolumeChangeScanner> _scanners;

    public FallbackVolumeChangeScanner(params IVolumeChangeScanner[] scanners)
    {
        ArgumentNullException.ThrowIfNull(scanners);
        if (scanners.Length == 0)
            throw new ArgumentException("At least one scanner is required.", nameof(scanners));
        _scanners = scanners;
    }

    /// <summary>The production chain: elevated MFT sweep first, unprivileged per-file rescan as the fallback.</summary>
    public static FallbackVolumeChangeScanner CreateDefault()
        => new(new MftUsnChangeScanner(), new PerFileUsnChangeScanner());

    public string Name => string.Join(" → ", _scanners.Select(static s => s.Name));

    public VolumeChangeScanResult Scan(
        string normalizedRoot,
        UsnCheckpoint since,
        IndexIngestionPolicy policy,
        string excludedStorageRoot,
        int parallelism,
        Action<long>? progress,
        CancellationToken cancellationToken)
    {
        VolumeChangeScanResult last = VolumeChangeScanResult.Failed("no rescan strategy was available");
        foreach (IVolumeChangeScanner scanner in _scanners)
        {
            cancellationToken.ThrowIfCancellationRequested();
            last = scanner.Scan(
                normalizedRoot, since, policy, excludedStorageRoot, parallelism, progress, cancellationToken);
            if (last.Succeeded)
                return last;

            YaguLog.For("ContentIndex").LogInformation(
                "Rescan strategy '{Scanner}' is unavailable for '{Root}': {Reason}.",
                scanner.Name, normalizedRoot, last.Failure);
        }

        return last;
    }

    public void Dispose()
    {
        foreach (IVolumeChangeScanner scanner in _scanners)
            scanner.Dispose();
    }
}
