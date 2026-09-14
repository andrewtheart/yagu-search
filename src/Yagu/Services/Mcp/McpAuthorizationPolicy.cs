using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32.SafeHandles;

namespace Yagu.Services.Mcp;

/// <summary>
/// The single authorization decision every MCP tool passes through — inputs, outputs, and the paths
/// carried back in results.
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately not a per-tool check. Yagu's CLI treats an omitted <c>--directory</c> as
/// "search every drive", so a tool that simply forwarded its arguments would hand an MCP client the
/// whole machine by omission. Every MCP search therefore requires an explicit root, and every path —
/// search root, read target, write destination, index root, and every path on the way back out — is
/// checked against the configured allowed roots.
/// </para>
/// <para>
/// Every check <b>fails closed</b>. If a path cannot be canonicalized, or its final link target cannot
/// be resolved, the answer is deny. A policy with no configured roots authorizes nothing.
/// </para>
/// </remarks>
internal sealed class McpAuthorizationPolicy
{
    private readonly List<string> _roots;
    private readonly McpCapability _capabilities;
    private readonly Func<string, string> _finalPathResolver;

    /// <param name="allowedRoots">Directories the client may touch. Empty means "authorize nothing".</param>
    /// <param name="capabilities">The powers granted to this client.</param>
    /// <param name="finalPathResolver">
    /// Resolves a path to its final on-disk target, following junctions and symlinks. Injectable so the
    /// escape-through-a-link cases can be tested without creating real links (which needs privilege).
    /// </param>
    public McpAuthorizationPolicy(
        IEnumerable<string> allowedRoots,
        McpCapability capabilities,
        Func<string, string>? finalPathResolver = null)
    {
        ArgumentNullException.ThrowIfNull(allowedRoots);

        _capabilities = capabilities;
        _finalPathResolver = finalPathResolver ?? DefaultFinalPathResolver;
        _roots = [];

        // Callers re-add roots they already granted, so a repeat has to collapse instead of stacking.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in allowedRoots)
        {
            // A root is validated as strictly as a request. A relative root would resolve against
            // whatever working directory the server happens to have, silently widening the policy to
            // an arbitrary directory, and a device-path root would defeat normalization for everything
            // under it. Anything unusable is dropped rather than stored.
            if (!TryCanonicalizeRoot(root, out string canonical))
                continue;

            if (seen.Add(canonical))
                _roots.Add(canonical);

            // A root can itself be a junction or symlink: a redirected Known Folder, a Dev Drive mount,
            // a OneDrive-backed folder. CheckPath compares the FINAL target of a request against these
            // roots, so storing only the name the user typed would deny every path under such a root.
            // Both names denote the same directory, so accepting either does not widen the grant.
            if (TryResolveRootTarget(canonical, out string resolved) && seen.Add(resolved))
                _roots.Add(resolved);
        }
    }

    /// <summary>Resolves an already-canonical root to its final on-disk target.</summary>
    private bool TryResolveRootTarget(string canonicalRoot, out string resolved)
    {
        resolved = string.Empty;
        try
        {
            string target = _finalPathResolver(canonicalRoot);
            return !string.IsNullOrWhiteSpace(target) && TryCanonicalize(target, out resolved);
        }
        catch (Exception)
        {
            // A root that cannot be resolved right now keeps its literal form. Requests under it still
            // work whenever nothing along the path turns out to be a link.
            return false;
        }
    }

    /// <summary>The canonical allowed roots, for diagnostics and the settings UI.</summary>
    public IReadOnlyList<string> AllowedRoots => _roots;

    /// <summary>True when this client was granted <paramref name="capability"/>.</summary>
    public bool HasCapability(McpCapability capability) => (_capabilities & capability) == capability;

    /// <summary>
    /// Authorizes the root of a search. An omitted root is rejected outright — it must never fall
    /// through to the CLI's "search all drives" default.
    /// </summary>
    public McpAuthorizationDecision AuthorizeSearchRoot(string? requestedRoot)
    {
        if (string.IsNullOrWhiteSpace(requestedRoot))
        {
            return McpAuthorizationDecision.Deny(
                McpDenialCode.RootRequired,
                "an explicit search root is required; an omitted root would search every drive.");
        }

        return Authorize(requestedRoot, McpCapability.Search);
    }

    /// <summary>Authorizes reading a bounded window of a file.</summary>
    public McpAuthorizationDecision AuthorizeRead(string? path) => Authorize(path, McpCapability.Read);

    /// <summary>
    /// Authorizes a write destination (an export report or a saved session). The target need not exist
    /// yet, but the directory it would land in must resolve inside an allowed root.
    /// </summary>
    public McpAuthorizationDecision AuthorizeWrite(string? path) => Authorize(path, McpCapability.ExportWrite);

    /// <summary>Authorizes modifying a file's contents.</summary>
    public McpAuthorizationDecision AuthorizeReplaceTarget(string? path) => Authorize(path, McpCapability.Replace);

    /// <summary>Authorizes index maintenance scoped to one root.</summary>
    public McpAuthorizationDecision AuthorizeIndexRoot(string? path) => Authorize(path, McpCapability.IndexMaintenance);

    /// <summary>
    /// Authorizes index maintenance that DISCARDS stored work — a from-scratch rebuild, or de-registering
    /// a root — which needs the destructive grant on top of ordinary maintenance.
    /// </summary>
    /// <remarks>
    /// Ordinary maintenance adds to the index; these throw it away. A user who grants
    /// <see cref="McpCapability.IndexMaintenance"/> while deliberately withholding
    /// <see cref="McpCapability.DestructiveIndexOps"/> is asking for exactly that distinction.
    /// </remarks>
    public McpAuthorizationDecision AuthorizeDestructiveIndexRoot(string? path)
    {
        McpAuthorizationDecision decision = Authorize(path, McpCapability.IndexMaintenance);
        if (!decision.Allowed)
            return decision;

        return HasCapability(McpCapability.DestructiveIndexOps)
            ? decision
            : McpAuthorizationDecision.Deny(
                McpDenialCode.CapabilityDenied,
                "destructive index operations are not enabled for this client.");
    }

    /// <summary>
    /// Authorizes an operation that affects every index on the machine and takes no path, such as
    /// clearing all indexes.
    /// </summary>
    public McpAuthorizationDecision AuthorizeGlobalIndexOperation()
    {
        if (_roots.Count == 0)
        {
            return McpAuthorizationDecision.Deny(
                McpDenialCode.NoAllowedRoots,
                "no allowed roots are configured, so nothing is authorized.");
        }

        return HasCapability(McpCapability.DestructiveIndexOps)
            ? McpAuthorizationDecision.Allow(string.Empty)
            : McpAuthorizationDecision.Deny(
                McpDenialCode.CapabilityDenied,
                "destructive index operations are not enabled for this client.");
    }

    /// <summary>
    /// Re-checks a path on the way OUT.
    /// </summary>
    /// <remarks>
    /// Results and loaded sessions can carry paths that live outside the allowed roots even when the
    /// request that produced them was itself legitimate — a session file stored inside an allowed root
    /// can reference matches from anywhere on the machine. Filtering only the inputs would leak them.
    /// This deliberately does not re-check a capability: the caller already passed that gate on the way
    /// in, and this is purely a containment guard.
    /// </remarks>
    public McpAuthorizationDecision AuthorizeOutboundPath(string? path)
    {
        if (_roots.Count == 0)
        {
            return McpAuthorizationDecision.Deny(
                McpDenialCode.NoAllowedRoots,
                "no allowed roots are configured, so nothing is authorized.");
        }

        return CheckPath(path);
    }

    private McpAuthorizationDecision Authorize(string? path, McpCapability required)
    {
        if (_roots.Count == 0)
        {
            return McpAuthorizationDecision.Deny(
                McpDenialCode.NoAllowedRoots,
                "no allowed roots are configured, so nothing is authorized.");
        }

        if (!HasCapability(required))
        {
            return McpAuthorizationDecision.Deny(
                McpDenialCode.CapabilityDenied,
                $"the '{required}' capability is not enabled for this client.");
        }

        return CheckPath(path);
    }

    private McpAuthorizationDecision CheckPath(string? path)
    {
        string candidate = path?.Trim().Trim('"') ?? string.Empty;
        if (candidate.Length == 0)
            return McpAuthorizationDecision.Deny(McpDenialCode.RootRequired, "a path is required.");

        // Reject the shapes that defeat normalization BEFORE normalizing, because normalization is
        // exactly what they are designed to bypass.
        if (IsDevicePath(candidate))
        {
            return McpAuthorizationDecision.Deny(
                McpDenialCode.DevicePath,
                @"device and extended-length paths (\\?\, \\.\) are not accepted.");
        }

        if (HasAlternateDataStream(candidate))
        {
            return McpAuthorizationDecision.Deny(
                McpDenialCode.AlternateDataStream,
                "alternate data streams are not accepted.");
        }

        if (!Path.IsPathFullyQualified(candidate))
        {
            // A relative path would resolve against a working directory this policy does not control.
            return McpAuthorizationDecision.Deny(
                McpDenialCode.NotFullyQualified,
                $"'{candidate}' is not a fully qualified path.");
        }

        if (!TryCanonicalize(candidate, out string canonical))
        {
            return McpAuthorizationDecision.Deny(
                McpDenialCode.UnresolvablePath,
                $"'{candidate}' could not be canonicalized.");
        }

        // Containment is checked twice: once on the normalized path, and once on the final link target.
        // The first catches traversal; the second catches a junction or symlink INSIDE an allowed root
        // that points outside it.
        if (!IsInsideAllowedRoot(canonical))
        {
            return McpAuthorizationDecision.Deny(
                McpDenialCode.OutsideAllowedRoots,
                $"'{canonical}' is outside every allowed root.");
        }

        string finalPath;
        try
        {
            finalPath = _finalPathResolver(canonical);
        }
        catch (Exception ex)
        {
            return McpAuthorizationDecision.Deny(
                McpDenialCode.UnresolvablePath,
                $"the final target of '{canonical}' could not be resolved: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(finalPath) || !TryCanonicalize(finalPath, out string canonicalFinal))
        {
            return McpAuthorizationDecision.Deny(
                McpDenialCode.UnresolvablePath,
                $"the final target of '{canonical}' could not be canonicalized.");
        }

        if (!IsInsideAllowedRoot(canonicalFinal))
        {
            return McpAuthorizationDecision.Deny(
                McpDenialCode.OutsideAllowedRoots,
                $"'{canonical}' resolves to '{canonicalFinal}', which is outside every allowed root.");
        }

        return McpAuthorizationDecision.Allow(canonicalFinal);
    }

    private bool IsInsideAllowedRoot(string canonical)
    {
        foreach (string root in _roots)
        {
            if (canonical.Equals(root, StringComparison.OrdinalIgnoreCase))
                return true;

            // The separator is what stops C:\FooBar matching a C:\Foo root.
            string prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
            if (canonical.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool TryCanonicalizeRoot(string? root, out string canonical)
    {
        canonical = string.Empty;

        string candidate = root?.Trim().Trim('"') ?? string.Empty;
        if (candidate.Length == 0)
            return false;

        if (IsDevicePath(candidate) || HasAlternateDataStream(candidate) || !Path.IsPathFullyQualified(candidate))
            return false;

        return TryCanonicalize(candidate, out canonical);
    }

    private static bool TryCanonicalize(string path, out string canonical)
    {
        canonical = string.Empty;
        try
        {
            string full = Path.GetFullPath(path);

            // Trim a trailing separator so containment comparisons are consistent, but keep it for a
            // drive root ("C:\"), where it is part of the path rather than decoration.
            if (full.Length > 3 && full.EndsWith(Path.DirectorySeparatorChar))
                full = full.TrimEnd(Path.DirectorySeparatorChar);

            if (full.Length == 0)
                return false;

            canonical = full;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or SecurityException or IOException)
        {
            return false;
        }
    }

    private static bool IsDevicePath(string path)
    {
        if (path.Length < 4)
            return false;

        // Normalize the prefix only, so //?/ and \\?\ are treated alike.
        Span<char> head = stackalloc char[4];
        for (int i = 0; i < 4; i++)
            head[i] = path[i] == '/' ? '\\' : path[i];

        return head is ['\\', '\\', '?', '\\'] or ['\\', '\\', '.', '\\'];
    }

    private static bool HasAlternateDataStream(string path)
    {
        // A legitimate Windows path has exactly one colon, at index 1 (the drive letter). A UNC path has
        // none. Anything else is a stream reference such as C:\file.txt:hidden:$DATA.
        int first = path.IndexOf(':');
        if (first < 0)
            return false;
        if (first != 1)
            return true;

        return path.IndexOf(':', first + 1) >= 0;
    }

    /// <summary>
    /// Resolves a path to its real on-disk location, following junctions and symlinks <b>anywhere in the
    /// path</b>, not just at the leaf.
    /// </summary>
    /// <remarks>
    /// A plain <see cref="File.ResolveLinkTarget"/> on the whole path is not enough: it only resolves when
    /// the LEAF is itself a reparse point, so it reports <c>C:\allowed\link\secret.txt</c> unchanged when
    /// <c>link</c> is a junction pointing out of the root — the file is not a link, the directory above it
    /// is. That silently authorizes a path that physically escapes the allowed roots.
    /// <para>
    /// <c>GetFinalPathNameByHandle</c> is tried first because it also canonicalizes mounted volumes and
    /// 8.3 short names. It cannot be relied on alone, though: it has to OPEN the path, and opening
    /// traverses every junction along it. Windows refuses that traversal with
    /// <c>ERROR_UNTRUSTED_MOUNT_POINT</c> (448) when the junction was created by a less-trusted user —
    /// precisely the case this policy exists to judge — so on a hardened machine the handle-based
    /// resolver fails on exactly the paths that matter. The fallback therefore reads each link's own
    /// stored target, which requires no traversal.
    /// </para>
    /// For a path that does not exist yet (a write destination), the existing ancestors are still
    /// resolved and the remainder re-appended, so a write into a linked-away directory is still caught.
    /// </remarks>
    private static string DefaultFinalPathResolver(string path)
    {
        if (TryGetFinalPathName(path, out string direct))
            return direct;

        return ResolveByReadingLinkTargets(path);
    }

    /// <summary>
    /// Bounds link substitution so a junction cycle cannot spin forever. Each hop rewrites one reparse
    /// point, and real paths nest only a handful deep.
    /// </summary>
    private const int MaxLinkHops = 64;

    /// <summary>
    /// Repeatedly rewrites the first reparse point on the path with the target that link itself stores,
    /// never opening through it. Returns <see cref="string.Empty"/> when the path cannot be resolved,
    /// which the caller turns into an <see cref="McpDenialCode.UnresolvablePath"/> denial rather than
    /// falling back to the string-prefix test this resolver exists to backstop.
    /// </summary>
    private static string ResolveByReadingLinkTargets(string path)
    {
        string current = path;

        for (int hop = 0; hop < MaxLinkHops; hop++)
        {
            if (!TryRewriteFirstLink(current, out string rewritten))
                return current;

            if (rewritten.Length == 0)
                return string.Empty;

            current = rewritten;
        }

        return string.Empty;
    }

    /// <summary>
    /// Finds the shallowest segment of <paramref name="path"/> that is a reparse point and replaces it
    /// with its stored target, re-appending the untouched tail.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> when no segment needs rewriting. <see langword="true"/> with an empty
    /// <paramref name="rewritten"/> when a segment is present but its target cannot be read, so the
    /// caller must deny.
    /// </returns>
    private static bool TryRewriteFirstLink(string path, out string rewritten)
    {
        rewritten = string.Empty;

        string root = Path.GetPathRoot(path) ?? string.Empty;
        if (root.Length == 0)
            return false;

        string[] parts = path[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        string current = root;

        for (int i = 0; i < parts.Length; i++)
        {
            current = Path.Combine(current, parts[i]);

            SegmentKind kind = ClassifySegment(current, out bool isDirectory);

            // Once a segment is missing, nothing deeper can exist, so nothing deeper can be a link.
            if (kind == SegmentKind.Missing)
                return false;

            if (kind == SegmentKind.Plain)
                continue;

            if (kind == SegmentKind.Unreadable)
                return true;

            string? target = TryReadLinkTarget(current, isDirectory);
            if (string.IsNullOrEmpty(target))
                return true;

            string tail = string.Join(Path.DirectorySeparatorChar, parts[(i + 1)..]);
            rewritten = tail.Length == 0 ? target : Path.Combine(target, tail);
            return true;
        }

        return false;
    }

    private enum SegmentKind
    {
        Missing,
        Plain,
        Link,
        Unreadable,
    }

    /// <summary>
    /// Classifies one path segment from its own attributes. <see cref="File.GetAttributes(string)"/>
    /// queries by path without opening, so it reports the reparse point itself instead of traversing it.
    /// </summary>
    private static SegmentKind ClassifySegment(string path, out bool isDirectory)
    {
        isDirectory = false;

        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            isDirectory = (attributes & FileAttributes.Directory) != 0;
            return (attributes & FileAttributes.ReparsePoint) != 0 ? SegmentKind.Link : SegmentKind.Plain;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return SegmentKind.Missing;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return SegmentKind.Unreadable;
        }
    }

    /// <summary>
    /// Reads a link's own target. <c>returnFinalTarget: false</c> is required — the traversing form hits
    /// the same untrusted-mount-point refusal that makes the handle-based resolver unusable here.
    /// </summary>
    private static string? TryReadLinkTarget(string path, bool isDirectory)
    {
        try
        {
            FileSystemInfo? target = isDirectory
                ? Directory.ResolveLinkTarget(path, returnFinalTarget: false)
                : File.ResolveLinkTarget(path, returnFinalTarget: false);

            return target?.FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private const uint FileShareAll = 0x00000007;          // read | write | delete
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000; // required to open a DIRECTORY handle
    private const uint FileNameNormalized = 0x00000000;
    private const uint VolumeNameDos = 0x00000000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFinalPathNameByHandleW")]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle hFile, [Out] char[] lpszFilePath, uint cchFilePath, uint dwFlags);

    private static bool TryGetFinalPathName(string path, out string finalPath)
    {
        finalPath = string.Empty;

        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            // Zero desired access asks only for metadata, so a file the caller cannot READ can still be
            // canonicalized — the policy must be able to reject it by location either way. The path is
            // passed in extended-length form because CreateFileW is otherwise capped at MAX_PATH, while
            // .NET's own file APIs (which the CLI uses to read and write the file) transparently handle
            // long paths. That asymmetry meant any path at or beyond 260 characters failed to resolve
            // here and fell through to the string-prefix test, so a junction behind a long path escaped.
            using SafeFileHandle handle = CreateFile(
                ToExtendedLengthPath(path), 0, FileShareAll, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);

            if (handle.IsInvalid)
                return false;

            var buffer = new char[512];
            uint length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, FileNameNormalized | VolumeNameDos);

            if (length == 0)
                return false;

            // A return equal to the buffer size cannot be a successful write (the count excludes the
            // terminator), so it means "too small" just as a larger value does. The +1 guarantees the
            // retry buffer actually grows.
            if (length >= buffer.Length)
            {
                buffer = new char[length + 1];
                length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, FileNameNormalized | VolumeNameDos);
                if (length == 0 || length >= buffer.Length)
                    return false;
            }

            finalPath = StripExtendedPrefix(new string(buffer, 0, (int)length));
            return finalPath.Length > 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// Converts an already-canonical full path to its extended-length form so <c>CreateFileW</c> is not
    /// capped at <c>MAX_PATH</c>. This is the inverse of <see cref="StripExtendedPrefix"/>. The input must
    /// be fully qualified and free of relative segments — extended-length paths bypass Win32
    /// normalization — which holds here because callers canonicalize before resolving.
    /// </summary>
    internal static string ToExtendedLengthPath(string path)
    {
        const string uncPrefix = @"\\";
        const string devicePrefix = @"\\?\";
        const string dosDevicePrefix = @"\\.\";

        if (string.IsNullOrEmpty(path)
            || path.StartsWith(devicePrefix, StringComparison.Ordinal)
            || path.StartsWith(dosDevicePrefix, StringComparison.Ordinal))
        {
            return path;
        }

        if (path.StartsWith(uncPrefix, StringComparison.Ordinal))
            return @"\\?\UNC\" + path[uncPrefix.Length..];

        return devicePrefix + path;
    }

    /// <summary>
    /// <c>GetFinalPathNameByHandle</c> always returns an extended-length path. Containment is compared
    /// against ordinary roots, and a device prefix is itself rejected by <see cref="IsDevicePath"/>, so
    /// the prefix is removed here rather than leaking into the decision.
    /// </summary>
    internal static string StripExtendedPrefix(string path)
    {
        const string uncPrefix = @"\\?\UNC\";
        const string devicePrefix = @"\\?\";

        if (path.StartsWith(uncPrefix, StringComparison.Ordinal))
            return @"\\" + path[uncPrefix.Length..];

        if (path.StartsWith(devicePrefix, StringComparison.Ordinal))
            return path[devicePrefix.Length..];

        return path;
    }
}
