namespace Yagu.Services.Mcp;

/// <summary>
/// The distinct powers an MCP client can be granted. They are separate because they carry very
/// different risk: reading a file the user can already read is not the same as rewriting it, and
/// deleting every content index is not the same as asking one for its status.
/// </summary>
[Flags]
internal enum McpCapability
{
    None = 0,

    /// <summary>Read a bounded window of a file.</summary>
    Read = 1 << 0,

    /// <summary>Search inside an allowed root.</summary>
    Search = 1 << 1,

    /// <summary>Touch online-only cloud placeholders, which forces the provider to download them.</summary>
    CloudHydration = 1 << 2,

    /// <summary>Write an export report or a saved session.</summary>
    ExportWrite = 1 << 3,

    /// <summary>
    /// Preview a find-and-replace, and commit a reviewed one. Committing additionally requires the user
    /// to have enabled replacement writes in Settings.
    /// </summary>
    Replace = 1 << 4,

    /// <summary>Build, compact or validate an index for a specific root.</summary>
    IndexMaintenance = 1 << 5,

    /// <summary>
    /// Index work that discards stored data: a from-scratch rebuild, de-registering a root, or clearing
    /// every index on the machine. Required on top of <see cref="IndexMaintenance"/>.
    /// </summary>
    DestructiveIndexOps = 1 << 6,
}

/// <summary>Stable denial reasons. These are protocol tokens, so they must not be reworded casually.</summary>
internal static class McpDenialCode
{
    public const string NoAllowedRoots = "no-allowed-roots";
    public const string CapabilityDenied = "capability-denied";
    public const string RootRequired = "root-required";
    public const string NotFullyQualified = "not-fully-qualified";
    public const string DevicePath = "device-path";
    public const string AlternateDataStream = "alternate-data-stream";
    public const string UnresolvablePath = "unresolvable-path";
    public const string OutsideAllowedRoots = "outside-allowed-roots";
}

/// <summary>The outcome of one authorization question.</summary>
/// <param name="Allowed">True only when every check passed.</param>
/// <param name="DenialCode">A <see cref="McpDenialCode"/> token, or null when allowed.</param>
/// <param name="Reason">Human-readable explanation for logs and tool errors.</param>
/// <param name="CanonicalPath">
/// The fully resolved path the caller must use. Callers pass THIS to <c>Yagu.exe</c>, never the raw
/// request — otherwise a path that resolved somewhere safe could be re-resolved differently downstream.
/// </param>
internal sealed record McpAuthorizationDecision(
    bool Allowed,
    string? DenialCode,
    string? Reason,
    string? CanonicalPath)
{
    public static McpAuthorizationDecision Allow(string canonicalPath) =>
        new(true, null, null, canonicalPath);

    public static McpAuthorizationDecision Deny(string code, string reason) =>
        new(false, code, reason, null);
}
