namespace Yagu.Services.Mcp;

/// <summary>
/// Keeps a CLI run's emitted and written results inside a set of roots, after links are resolved.
/// </summary>
/// <remarks>
/// <para>
/// Authorizing the search ROOT is not enough. The managed walker follows directory junctions, so a
/// junction inside an allowed folder yields matches whose real location is outside it. A plain search
/// filters those on the way out, but an export or a saved session is written by the CLI itself — the
/// out-of-root path and its matching TEXT land in the report before any consumer sees them, and a
/// replacement would rewrite the linked-away file.
/// </para>
/// <para>
/// Containment is decided per parent DIRECTORY and cached, because an escape is a property of the
/// directory rather than of each file in it; resolving every result would add a handle open per match.
/// This is off unless roots are supplied, so the GUI and ordinary CLI runs are unaffected.
/// </para>
/// </remarks>
internal sealed class PathConfinement
{
    private readonly McpAuthorizationPolicy? _policy;
    private readonly Dictionary<string, bool> _directories = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A guard that allows everything, for runs with no confinement.</summary>
    public static PathConfinement None { get; } = new([]);

    public PathConfinement(IReadOnlyList<string> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);

        // Capability is irrelevant here: AuthorizeOutboundPath is a pure containment check, and reusing
        // the policy keeps confinement on exactly the same link-resolution rules the MCP grant uses.
        _policy = roots.Count == 0 ? null : new McpAuthorizationPolicy(roots, McpCapability.Read);
    }

    /// <summary>True when this run is confined at all.</summary>
    public bool IsActive => _policy is not null;

    /// <summary>Files rejected so far, so a caller can say how many results were withheld.</summary>
    public int Rejected { get; private set; }

    /// <summary>True when <paramref name="path"/> resolves inside the configured roots.</summary>
    public bool Allows(string? path)
    {
        if (_policy is null)
            return true;

        if (string.IsNullOrWhiteSpace(path))
        {
            Rejected++;
            return false;
        }

        string? directory = Path.GetDirectoryName(path);

        if (string.IsNullOrEmpty(directory))
        {
            Rejected++;
            return false;
        }

        if (!_directories.TryGetValue(directory, out bool allowed))
        {
            allowed = _policy.AuthorizeOutboundPath(directory).Allowed;
            _directories[directory] = allowed;
        }

        if (!allowed)
        {
            Rejected++;
            return false;
        }

        // The directory cache cannot answer for a LEAF that is itself a link: a symlinked file in a
        // contained folder still points out of the roots. Only reparse points pay for the extra
        // resolution, and almost nothing is one.
        if (LeafNeedsResolution(path) && !_policy.AuthorizeOutboundPath(path).Allowed)
        {
            Rejected++;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Whether the leaf has to be resolved before it can be trusted.
    /// </summary>
    /// <remarks>
    /// Unreadable metadata resolves rather than assuming "not a link". Treating an unreadable leaf as an
    /// ordinary file would let a symlink whose attribute read fails transiently — a sharing violation, an
    /// AV filter, an offline cloud provider — carry its out-of-root target into an export or a rewrite.
    /// </remarks>
    private static bool LeafNeedsResolution(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return true;
        }
    }
}
