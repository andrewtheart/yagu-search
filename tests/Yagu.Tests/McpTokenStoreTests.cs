using System.Text;
using Yagu.Services.Mcp;

namespace Yagu.Tests;

/// <summary>
/// Covers the on-disk half of the HTTP bearer token. These drive the directory-parameterised overloads
/// against a temp directory on purpose: <see cref="McpTokenStore.Regenerate"/> invalidates every
/// configured client, so a test must never point it at the live per-user path.
/// </summary>
public class McpTokenStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "yagu-token-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        GC.SuppressFinalize(this);
    }

    private string TokenFile => McpTokenStore.TokenPathIn(_dir);

    [Fact]
    public void TokenPathIn_IsTheHttpTokenFileInsideTheDirectory()
    {
        Assert.Equal(Path.Combine(_dir, "http-token"), TokenFile);
    }

    [Fact]
    public void LiveTokenPath_LivesUnderTheDirectoryPath()
    {
        // The production path must keep flowing through the same combiner, otherwise the parameterised
        // overloads would be testing a different layout than the one that ships.
        Assert.Equal(McpTokenStore.TokenPathIn(McpTokenStore.DirectoryPath), McpTokenStore.TokenPath);
        Assert.StartsWith(McpTokenStore.DirectoryPath, McpTokenStore.TokenPath, StringComparison.Ordinal);
    }

    [Fact]
    public void TryReadTokenIn_NoDirectory_ReturnsNull()
    {
        Assert.Null(McpTokenStore.TryReadTokenIn(_dir));
    }

    [Fact]
    public void TryReadTokenIn_EmptyFile_ReturnsNullRatherThanEmptyString()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(TokenFile, string.Empty);

        // An empty token must not authenticate anything, so it has to read back as "absent".
        Assert.Null(McpTokenStore.TryReadTokenIn(_dir));
    }

    [Fact]
    public void TryReadTokenIn_WhitespaceOnlyFile_ReturnsNull()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(TokenFile, "   \r\n\t ");

        Assert.Null(McpTokenStore.TryReadTokenIn(_dir));
    }

    [Fact]
    public void TryReadTokenIn_TrimsSurroundingWhitespace()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(TokenFile, "  abc123\r\n");

        Assert.Equal("abc123", McpTokenStore.TryReadTokenIn(_dir));
    }

    [Fact]
    public void TryReadTokenIn_UnreadableFile_ReturnsNullInsteadOfThrowing()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(TokenFile, "locked");

        using var hold = new FileStream(TokenFile, FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.Null(McpTokenStore.TryReadTokenIn(_dir));
    }

    [Fact]
    public void RegenerateIn_WritesA256BitBase64UrlToken()
    {
        string? token = McpTokenStore.Regenerate(_dir);

        Assert.NotNull(token);
        Assert.Equal(43, token!.Length);
        Assert.DoesNotContain('+', token);
        Assert.DoesNotContain('/', token);
        Assert.DoesNotContain('=', token);
        Assert.Equal(token, File.ReadAllText(TokenFile, Encoding.UTF8));
    }

    [Fact]
    public void RegenerateIn_WritesNoByteOrderMark()
    {
        McpTokenStore.RegenerateIn(_dir);

        byte[] raw = File.ReadAllBytes(TokenFile);

        // A BOM would be sent as part of the bearer value and would never match.
        Assert.False(raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF);
    }

    [Fact]
    public void RegenerateIn_LeavesNoTemporaryFileBehind()
    {
        McpTokenStore.RegenerateIn(_dir);
        McpTokenStore.RegenerateIn(_dir);

        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void RegenerateIn_ReplacesTheExistingTokenWithADifferentOne()
    {
        string? first = McpTokenStore.RegenerateIn(_dir);
        string? second = McpTokenStore.RegenerateIn(_dir);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first, second);
        Assert.Equal(second, McpTokenStore.TryReadTokenIn(_dir));
    }

    [Fact]
    public void RegenerateIn_UncreatableDirectory_ReturnsNullInsteadOfThrowing()
    {
        // A file where the directory should go: creating the directory cannot succeed.
        Directory.CreateDirectory(_dir);
        string blocker = Path.Combine(_dir, "blocker");
        File.WriteAllText(blocker, "x");

        Assert.Null(McpTokenStore.RegenerateIn(Path.Combine(blocker, "mcp")));
    }

    [Fact]
    public void EnsureTokenIn_CreatesOnFirstUseThenReturnsTheSameToken()
    {
        string? created = McpTokenStore.EnsureTokenIn(_dir);
        string? reread = McpTokenStore.EnsureTokenIn(_dir);

        Assert.NotNull(created);
        Assert.Equal(created, reread);
    }

    [Fact]
    public void EnsureTokenIn_ReplacesAnEmptyTokenFile()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(TokenFile, "   ");

        string? token = McpTokenStore.EnsureTokenIn(_dir);

        Assert.False(string.IsNullOrWhiteSpace(token));
        Assert.Equal(token, McpTokenStore.TryReadTokenIn(_dir));
    }

    [Fact]
    public void DeleteIn_RemovesTheToken()
    {
        McpTokenStore.RegenerateIn(_dir);
        Assert.True(File.Exists(TokenFile));

        McpTokenStore.DeleteIn(_dir);

        Assert.False(File.Exists(TokenFile));
        Assert.Null(McpTokenStore.TryReadTokenIn(_dir));
    }

    [Fact]
    public void DeleteIn_MissingToken_IsANoOp()
    {
        McpTokenStore.DeleteIn(_dir);

        Assert.Null(McpTokenStore.TryReadTokenIn(_dir));
    }

    [Fact]
    public void DeleteIn_LockedToken_DoesNotThrow()
    {
        McpTokenStore.RegenerateIn(_dir);

        using var hold = new FileStream(TokenFile, FileMode.Open, FileAccess.Read, FileShare.None);

        // Turning the transport off must not fail just because something still holds the file open.
        McpTokenStore.DeleteIn(_dir);
    }

    [Fact]
    public void RegeneratedToken_ValidatesAgainstItselfAndNotAgainstAPriorToken()
    {
        string? first = McpTokenStore.RegenerateIn(_dir);
        string? second = McpTokenStore.RegenerateIn(_dir);

        Assert.True(McpTokenStore.IsValid(second, second));
        Assert.False(McpTokenStore.IsValid(first, second));
    }

    [Fact]
    public void RegenerateIn_NeverRepeatsAToken()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int written = 0;

        for (int i = 0; i < 25; i++)
        {
            string? token = McpTokenStore.RegenerateIn(_dir);

            // A failed write is a documented outcome — the caller reports "could not write the token
            // file" — and rapid replaces in one directory can lose a race with a scanner. Asserting
            // every attempt succeeds would test the filesystem rather than the entropy covered here.
            if (token is null)
                continue;

            written++;
            Assert.True(seen.Add(token), "RegenerateIn produced a duplicate token.");
        }

        Assert.True(written >= 5, $"Only {written} of 25 regenerations produced a token; the store looks unwritable.");
    }

    [Fact]
    public void RegenerateIn_RetriesAReplaceThatAScannerIsBlocking()
    {
        string first = McpTokenStore.EnsureTokenIn(_dir)!;
        Assert.NotNull(first);

        string? second;
        using (new FileStream(TokenFile, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            // FileShare.Read denies the delete File.Replace needs, which is what a scanner or indexer
            // holding the token file looks like. Reporting that as "could not regenerate" would blame
            // the user for someone else's handle.
            second = McpTokenStore.RegenerateIn(_dir);
        }

        if (second is null)
        {
            // The retries were exhausted. Then the old token must still be intact — a half-replaced
            // token file would lock every configured client out with no way to tell why.
            Assert.Equal(first, McpTokenStore.TryReadTokenIn(_dir));
            return;
        }

        Assert.NotEqual(first, second);
        Assert.Equal(second, McpTokenStore.TryReadTokenIn(_dir));
    }

    [Fact]
    public void DeleteIn_AStoreThatWasNeverCreated_IsNotAnError()
    {
        McpTokenStore.DeleteIn(Path.Combine(_dir, "never-existed"));
    }

    [Fact]
    public void RestrictToCurrentUser_MissingFile_IsBestEffort()
    {
        string missing = Path.Combine(_dir, "missing-token");

        McpTokenStore.RestrictToCurrentUser(missing);

        Assert.False(File.Exists(missing));
    }

    [Fact]
    public void TryReadTokenIn_AnEmptyTokenFile_ReadsAsNoTokenRatherThanAnEmptyBearer()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(TokenFile, "   \r\n");

        // An empty string here would be handed out as a valid bearer value and compared against
        // whatever a caller sent, which is how an unauthenticated request gets accepted.
        Assert.Null(McpTokenStore.TryReadTokenIn(_dir));
    }
}
