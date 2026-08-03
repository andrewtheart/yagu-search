using System.Text.Json;
using System.Text.Json.Serialization;

namespace Yagu.Services.Index;

/// <summary>
/// The persisted generation manifest (plan §3.4): scope identity, the independent
/// <see cref="IndexFormatVersion"/> and <see cref="ContentRepresentationVersion"/> (which gate trust),
/// the freshness checkpoint, counts, and build provenance. The <see cref="PlannerSemanticsVersion"/>
/// is recorded for provenance only and does <b>not</b> make query-independent postings incompatible.
/// </summary>
public sealed record IndexManifest
{
    /// <summary>The current format version this build writes/expects.</summary>
    public const int CurrentFormatVersion = 2;

    public int IndexFormatVersion { get; init; } = CurrentFormatVersion;

    public int ContentRepresentationVersion { get; init; } = ContentRepresentation.Version;

    public string ScopeId { get; init; } = string.Empty;

    public string VolumeIdentity { get; init; } = string.Empty;

    /// <summary>The NTFS volume serial number, keying the generation's file identities to one volume
    /// (plan §3.6). Default 0 when the identity could not be captured (journal freshness then degrades
    /// conservatively for this generation).</summary>
    public ulong VolumeSerialNumber { get; init; }

    /// <summary>Canonical <c>\\?\Volume{GUID}\</c> identity captured at build time. Null only for
    /// indexes written before mounted-volume binding was introduced.</summary>
    public string? VolumeGuidPath { get; init; }

    /// <summary>Filesystem name captured with the volume binding (normally NTFS or ReFS).</summary>
    public string? FileSystemName { get; init; }

    /// <summary>Indexed root relative to the containing mount point. This distinguishes a subfolder root
    /// while allowing the volume itself to be identified independently of its current drive letter.</summary>
    public string? VolumeRelativeRootPath { get; init; }

    public string NormalizedRootPath { get; init; } = string.Empty;

    public UsnCheckpoint FreshnessCheckpoint { get; init; } = UsnCheckpoint.None;

    public long ContentCount { get; init; }

    public long AliasCount { get; init; }

    /// <summary>UTC when this logical index was created by its full build or rebuild. Preserved across
    /// incremental compaction. Null only for indexes written before this provenance field existed.</summary>
    public DateTimeOffset? CreatedUtc { get; init; }

    /// <summary>UTC of the most recent incremental update folded into this base generation. Active
    /// delta segments can carry a newer update time; manifest-only status reads combine both.</summary>
    public DateTimeOffset? LastIncrementalUpdateUtc { get; init; }

    public DateTimeOffset BuiltUtc { get; init; }

    /// <summary>Diagnostics/provenance only — never gates generation trust (plan §3.4).</summary>
    public int? PlannerSemanticsVersion { get; init; }

    /// <summary>The version dimensions that gate structural trust (format + representation).</summary>
    [JsonIgnore]
    public IndexVersionSet Versions => new(IndexFormatVersion, ContentRepresentationVersion);

    /// <summary>
    /// Composes the structural trust verdict for this manifest against the current build via the
    /// single trust surface (<see cref="IndexTrustDecision.EvaluateStructural"/>).
    /// </summary>
    public IndexStructuralVerdict EvaluateStructural()
        => IndexTrustDecision.EvaluateStructural(
            Versions,
            new IndexVersionSet(CurrentFormatVersion, ContentRepresentation.Version));

    /// <summary>Serializes the manifest to JSON using the AOT-safe source-generated context.</summary>
    public string Serialize()
        => JsonSerializer.Serialize(this, IndexManifestJsonContext.Default.IndexManifest);

    /// <summary>Deserializes a manifest, or null when the JSON is empty/invalid.</summary>
    public static IndexManifest? Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize(json, IndexManifestJsonContext.Default.IndexManifest);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

[JsonSerializable(typeof(IndexManifest))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal sealed partial class IndexManifestJsonContext : JsonSerializerContext
{
}
