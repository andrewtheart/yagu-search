using System.Security.Cryptography;
using System.Text;

namespace Yagu.Services.Mcp;

/// <summary>
/// The bearer token the HTTP transport authenticates with, stored per user on disk.
/// </summary>
/// <remarks>
/// <para>
/// The token is <b>never passed on the command line</b>. A process command line is readable by any other
/// process on the machine — including other users' administrative tooling and anything that enumerates
/// processes — so a token in <c>argv</c> is a token you have published. The server reads it from this
/// file instead, and the file lives under <c>%LOCALAPPDATA%</c>, whose inherited ACL already restricts it
/// to this user (plus SYSTEM and administrators, who can read the user's files regardless).
/// </para>
/// <para>
/// <b>Threat model.</b> This defends the loopback endpoint against <i>other user accounts</i> and against
/// browser-based attacks (a page cannot read a local file to obtain the token). It deliberately does not
/// defend against a process already running <i>as this user</i>: such a process can read the token file,
/// and it could equally read every file the server would read for it. Same-user isolation is not a
/// property this design claims.
/// </para>
/// </remarks>
internal static class McpTokenStore
{
    /// <summary>Bytes of entropy per token. 256 bits, base64url-encoded to 43 characters.</summary>
    private const int TokenByteCount = 32;

    private const string TokenFileName = "http-token";

    public static string DirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Yagu",
        "mcp");

    public static string TokenPath => TokenPathIn(DirectoryPath);

    /// <summary>The token file inside <paramref name="directoryPath"/>.</summary>
    internal static string TokenPathIn(string directoryPath) => Path.Combine(directoryPath, TokenFileName);

    /// <summary>Reads the current token, creating one on first use. Returns null if it cannot be stored.</summary>
    public static string? EnsureToken() => EnsureTokenIn(DirectoryPath);

    /// <summary>Reads the current token, or null when none has been created yet or it is unreadable.</summary>
    public static string? TryReadToken() => TryReadTokenIn(DirectoryPath);

    /// <summary>
    /// Replaces the token with a freshly generated one and returns it, or null when it cannot be written.
    /// Every client configured with the old token stops working until it is re-issued, which is the point.
    /// </summary>
    internal static string? Regenerate(string directoryPath) => RegenerateIn(directoryPath);

    // The directory is a parameter below so tests can drive the real file handling against a temp
    // directory: regenerating invalidates every configured client, so it must never run against the
    // live per-user path from a test.

    internal static string? EnsureTokenIn(string directoryPath)
    {
        string? existing = TryReadTokenIn(directoryPath);
        if (!string.IsNullOrEmpty(existing))
            return existing;

        return RegenerateIn(directoryPath);
    }

    internal static string? TryReadTokenIn(string directoryPath)
    {
        try
        {
            string tokenPath = TokenPathIn(directoryPath);
            if (!File.Exists(tokenPath))
                return null;

            string token = File.ReadAllText(tokenPath, Encoding.UTF8).Trim();
            return token.Length == 0 ? null : token;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    internal static string? RegenerateIn(string directoryPath)
    {
        try
        {
            Directory.CreateDirectory(directoryPath);
            string tokenPath = TokenPathIn(directoryPath);
            string token = GenerateToken();

            // Write through a temp file in the same directory and replace, so a reader never observes a
            // half-written token and a failed write leaves the previous one intact.
            string temp = tokenPath + ".tmp";
            File.WriteAllText(temp, token, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            RestrictToCurrentUser(temp);

            if (File.Exists(tokenPath))
                ReplaceWithRetry(temp, tokenPath);
            else
                File.Move(temp, tokenPath);

            return token;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    internal static void DeleteIn(string directoryPath)
    {
        try
        {
            string tokenPath = TokenPathIn(directoryPath);
            if (File.Exists(tokenPath))
                File.Delete(tokenPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A token that cannot be deleted is still useless once the transport refuses connections.
        }
    }

    /// <summary>
    /// Compares a presented token against the stored one in constant time.
    /// </summary>
    /// <remarks>
    /// An ordinary string comparison returns as soon as two bytes differ, which leaks the length of the
    /// matching prefix and makes the token guessable one character at a time over enough requests.
    /// </remarks>
    public static bool IsValid(string? presented, string? expected)
    {
        if (string.IsNullOrEmpty(presented) || string.IsNullOrEmpty(expected))
            return false;

        byte[] presentedBytes = Encoding.UTF8.GetBytes(presented);
        byte[] expectedBytes = Encoding.UTF8.GetBytes(expected);

        // FixedTimeEquals short-circuits on a length mismatch, so hash first: both operands are then the
        // same size and the comparison time reveals nothing, not even the token's length.
        Span<byte> presentedHash = stackalloc byte[32];
        Span<byte> expectedHash = stackalloc byte[32];
        SHA256.HashData(presentedBytes, presentedHash);
        SHA256.HashData(expectedBytes, expectedHash);

        return CryptographicOperations.FixedTimeEquals(presentedHash, expectedHash);
    }

    /// <summary>
    /// Replaces the token file, retrying a scanner's transient hold on the destination.
    /// </summary>
    /// <remarks>
    /// File.Replace throws "Unable to remove the file to be replaced" when an indexer or antivirus has
    /// the destination open — around 1.5% of replaces on an ordinary Windows box, which is often enough
    /// to turn "regenerate the token" into an occasional unexplained failure.
    /// </remarks>
    private static void ReplaceWithRetry(string temp, string destination)
    {
        const int attempts = 5;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Replace(temp, destination, destinationBackupFileName: null, ignoreMetadataErrors: true);
                return;
            }
            catch (IOException) when (attempt < attempts)
            {
                Thread.Sleep(attempt * 20);
            }
        }
    }

    /// <summary>base64url so the token is safe in a header without escaping, and carries no padding.</summary>
    private static string GenerateToken()
    {
        Span<byte> bytes = stackalloc byte[TokenByteCount];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    /// <summary>
    /// Best-effort tightening of the file's ACL to this user only. <c>%LOCALAPPDATA%</c> already inherits
    /// a user-scoped ACL, so this is defence in depth rather than the primary control, and a machine that
    /// refuses the change is not treated as a failure.
    /// </summary>
    internal static void RestrictToCurrentUser(string path)
    {
        try
        {
            var info = new FileInfo(path);
            System.Security.AccessControl.FileSecurity security = info.GetAccessControl();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            security.SetAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                identity.User!,
                System.Security.AccessControl.FileSystemRights.FullControl,
                System.Security.AccessControl.AccessControlType.Allow));

            info.SetAccessControl(security);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException
                                      or System.Security.Principal.IdentityNotMappedException or InvalidOperationException)
        {
            // Keep the inherited profile ACL.
        }
    }
}
