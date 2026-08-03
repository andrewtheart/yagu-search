namespace Yagu.Services.Index;

/// <summary>
/// Resolves a durable file identity (a <c>FILE_ID_128</c> reported by the USN journal) to its <b>current</b>
/// path on disk, or null when the file no longer exists / is inaccessible (plan §3.5, "OpenFileById / final
/// path resolution"). This is the missing half that lets name-less USN change records be turned into the
/// created/modified/deleted paths an incremental update needs. Injected as an interface so the change
/// resolver is unit-testable without the real volume.
/// </summary>
public interface IFileIdPathResolver
{
    /// <summary>The current DOS path for <paramref name="identity"/>, or null when it cannot be resolved
    /// (deleted, moved to a volume this resolver doesn't cover, or unopenable).</summary>
    string? TryResolvePath(UsnFileIdentity identity);
}
