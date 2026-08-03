using System.Security.Cryptography;
using System.Text;

namespace Yagu.Services.Index;

/// <summary>
/// Canonical scope identity and path normalization for the content index (plan §3.6). A scope is
/// identified by <c>(volume identity, normalized root-relative path)</c>. Separators, drive-root
/// spelling, and trailing separators are normalized, but case is <b>not</b> folded (NTFS supports
/// per-directory case sensitivity), so every hash hit must still be resolved by a full stored-path
/// comparison. The scope id is a stable hash of the canonical identity, suitable as a directory
/// name under the index storage root.
/// </summary>
public static class IndexScopeIdentity
{
    /// <summary>
    /// Normalizes a root/directory path for scope identity and path-hash comparison: converts forward
    /// slashes to backslashes, strips a <c>\\?\</c> / <c>\\?\UNC\</c> long-path prefix, collapses
    /// repeated separators, and trims a trailing separator (keeping a bare drive root's separator).
    /// Case is preserved.
    /// </summary>
    public static string NormalizePath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string s = path.Trim();
        if (s.Length == 0)
            return string.Empty;

        // Strip long-path prefixes.
        if (s.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
            s = @"\\" + s[@"\\?\UNC\".Length..];
        else if (s.StartsWith(@"\\?\", StringComparison.Ordinal))
            s = s[@"\\?\".Length..];

        s = s.Replace('/', '\\');

        // Preserve a leading UNC "\\" then collapse remaining repeated separators.
        bool unc = s.StartsWith(@"\\", StringComparison.Ordinal);
        string body = unc ? s[2..] : s;
        body = CollapseSeparators(body);
        s = unc ? @"\\" + body : body;

        // Trim a trailing separator, but keep "C:\" (a bare drive root) intact.
        if (s.Length > 0 && s[^1] == '\\' && !IsBareDriveRoot(s))
            s = s.TrimEnd('\\');

        // A bare drive letter ("D:") denotes that drive's root — the same scope as "D:\". Canonicalize it
        // so a drive indexed or searched with or without the trailing separator maps to one scope id
        // (otherwise "D:" and "D:\" hash differently and an index built under one is invisible to the other).
        if (s.Length == 2 && char.IsLetter(s[0]) && s[1] == ':')
            s += "\\";

        return s;
    }

    private static string CollapseSeparators(string body)
    {
        if (!body.Contains(@"\\", StringComparison.Ordinal))
            return body;
        var sb = new StringBuilder(body.Length);
        bool prevSep = false;
        foreach (char c in body)
        {
            if (c == '\\')
            {
                if (!prevSep) sb.Append(c);
                prevSep = true;
            }
            else
            {
                sb.Append(c);
                prevSep = false;
            }
        }
        return sb.ToString();
    }

    private static bool IsBareDriveRoot(string s)
        => s.Length == 3 && char.IsLetter(s[0]) && s[1] == ':' && s[2] == '\\';

    /// <summary>
    /// Computes a stable, filesystem-safe scope id (lower-case hex SHA-256, 32 chars) from a volume
    /// identity and the normalized root path. The volume identity (e.g. a volume GUID) is folded
    /// case-insensitively; the normalized root path is hashed as-is (case preserved) per §3.6.
    /// </summary>
    public static string ComputeScopeId(string volumeIdentity, string normalizedRootPath)
    {
        ArgumentNullException.ThrowIfNull(volumeIdentity);
        ArgumentNullException.ThrowIfNull(normalizedRootPath);

        string canonical = volumeIdentity.Trim().ToLowerInvariant() + "|" + normalizedRootPath;
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));

        // 16 bytes (128 bits) of the digest is ample to avoid collisions across a user's scopes.
        return Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant();
    }
}
