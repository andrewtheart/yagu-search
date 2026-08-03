using Yagu.Services.Index;

namespace Yagu.Tests.Index;

/// <summary>
/// Shared synthetic durable-identity provider for content-index fixtures.
///
/// The missing-identity freshness fix makes an indexed non-member whose durable <c>FILE_ID_128</c> was
/// NOT captured at build time live-scan instead of prune (USN can never dirty it, so it could never be
/// proven fresh, so pruning it would risk a silent missed match). Fixtures that exercise the pruning path
/// must therefore give each admitted document a stable, distinct identity — mirroring a real build that
/// captures <c>FileIdentityReader</c> identities. Deterministic (FNV-1a over the normalized path) so tests
/// do not depend on runtime string-hash randomization; the value only needs to be non-null and distinct.
/// </summary>
internal static class IndexTestIdentities
{
    public static readonly System.Func<string, FileIdentity?> Provider = Capture;

    public static FileIdentity? Capture(string path)
    {
        string norm = IndexScopeIdentity.NormalizePath(path);
        ulong hash = 1469598103934665603UL; // FNV-1a 64-bit offset basis
        foreach (char c in norm)
        {
            hash ^= c;
            hash *= 1099511628211UL; // FNV-1a 64-bit prime
        }
        if (hash == 0)
            hash = 1; // never a degenerate all-zero id

        return new FileIdentity(0x5UL, new UsnFileIdentity(hash, 0));
    }
}
