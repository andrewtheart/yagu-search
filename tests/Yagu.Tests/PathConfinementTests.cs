using Yagu.Services.Mcp;
using Xunit;

namespace Yagu.Tests;

/// <summary>
/// Keeps a CLI run's written results inside the allowed roots after links are resolved.
/// </summary>
/// <remarks>
/// Authorizing the search root is not enough. The managed walker follows directory junctions, so a link
/// inside an allowed folder produces matches whose real location is outside it. A plain search filters
/// those on the way out, but an export, a saved session, and a replacement are all written by the CLI
/// itself — the out-of-root path and its matching text land in the file before any consumer sees them.
/// </remarks>
public class PathConfinementTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "yagu-confine-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly string _allowed;
    private readonly string _outside;
    private readonly string _link;
    private readonly bool _linked;

    public PathConfinementTests()
    {
        _allowed = Path.Combine(_base, "allowed");
        _outside = Path.Combine(_base, "outside");
        _link = Path.Combine(_allowed, "escape");

        Directory.CreateDirectory(_allowed);
        Directory.CreateDirectory(_outside);
        File.WriteAllText(Path.Combine(_allowed, "inside.txt"), "inside");
        File.WriteAllText(Path.Combine(_outside, "secret.txt"), "secret");

        _linked = TryCreateJunction(_link, _outside);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_link))
                Directory.Delete(_link);
            if (Directory.Exists(_base))
                Directory.Delete(_base, recursive: true);
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void ALeafThatIsItselfAnEscapingLink_IsRejected()
    {
        // The directory cache cannot answer for the leaf: its PARENT is the allowed root and perfectly
        // contained, while the entry itself points out of the roots.
        Assert.True(_linked, "a directory junction could not be created; this security check cannot be skipped.");

        var confinement = new PathConfinement([_allowed]);

        Assert.False(confinement.Allows(_link));
        Assert.Equal(1, confinement.Rejected);
    }

    [Fact]
    public void AnOrdinaryFileBesideAnEscapingLink_IsStillAllowed()
    {
        // The leaf check must not spill over onto its neighbours.
        Assert.True(_linked, "a directory junction could not be created; this security check cannot be skipped.");

        var confinement = new PathConfinement([_allowed]);

        Assert.True(confinement.Allows(Path.Combine(_allowed, "inside.txt")));
        Assert.Equal(0, confinement.Rejected);
    }

    [Fact]
    public void NoRoots_AllowsEverything_SoOrdinaryRunsAreUnaffected()
    {
        var confinement = new PathConfinement([]);

        Assert.False(confinement.IsActive);
        Assert.True(confinement.Allows(@"C:\anywhere\at\all.txt"));
        Assert.True(PathConfinement.None.Allows(@"C:\anywhere\at\all.txt"));
    }

    [Fact]
    public void AFileInsideTheRoot_IsAllowed()
    {
        var confinement = new PathConfinement([_allowed]);

        Assert.True(confinement.IsActive);
        Assert.True(confinement.Allows(Path.Combine(_allowed, "inside.txt")));
        Assert.Equal(0, confinement.Rejected);
    }

    [Fact]
    public void AFileReachedThroughAnEscapingJunction_IsRejected()
    {
        Assert.True(_linked, "a directory junction could not be created; this security check cannot be skipped.");

        var confinement = new PathConfinement([_allowed]);

        Assert.False(confinement.Allows(Path.Combine(_link, "secret.txt")));
        Assert.Equal(1, confinement.Rejected);
    }

    [Fact]
    public void AFileOutsideTheRootEntirely_IsRejected()
    {
        var confinement = new PathConfinement([_allowed]);

        Assert.False(confinement.Allows(Path.Combine(_outside, "secret.txt")));
    }

    [Fact]
    public void RepeatedFilesInOneDirectory_ResolveTheDirectoryOnce()
    {
        // The cache is what keeps this affordable: an export can carry tens of thousands of matches, and
        // resolving each one would add a handle open per result.
        Assert.True(_linked, "a directory junction could not be created; this security check cannot be skipped.");

        var confinement = new PathConfinement([_allowed]);

        for (int i = 0; i < 50; i++)
        {
            Assert.True(confinement.Allows(Path.Combine(_allowed, $"inside-{i}.txt")));
            Assert.False(confinement.Allows(Path.Combine(_link, $"secret-{i}.txt")));
        }

        Assert.Equal(50, confinement.Rejected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnUnusablePath_IsRejectedRatherThanAllowed(string? path)
    {
        var confinement = new PathConfinement([_allowed]);

        Assert.False(confinement.Allows(path));
    }

    [Theory]
    [InlineData("bare-name.txt")]
    [InlineData("relative\\..\\escape.txt")]
    public void APathWithNoResolvableDirectory_IsRejectedAndCounted(string path)
    {
        // Without a directory component there is nothing to authorize, and a relative path would
        // resolve against whatever working directory the CLI happened to inherit. Neither may pass.
        var confinement = new PathConfinement([_allowed]);

        Assert.False(confinement.Allows(path));
        Assert.Equal(1, confinement.Rejected);
    }

    [Fact]
    public void NoRoots_DoesNotCountRejections()
    {
        var confinement = new PathConfinement([]);

        Assert.True(confinement.Allows("bare-name.txt"));
        Assert.True(confinement.Allows(null));
        Assert.Equal(0, confinement.Rejected);
    }

    private static bool TryCreateJunction(string link, string target)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add("mklink");
            psi.ArgumentList.Add("/J");
            psi.ArgumentList.Add(link);
            psi.ArgumentList.Add(target);

            using var process = System.Diagnostics.Process.Start(psi);
            process?.WaitForExit(10_000);
            return Directory.Exists(link);
        }
        catch (Exception)
        {
            return false;
        }
    }

    [Fact]
    public void APathTheOsCannotEvenStat_IsRejectedRatherThanAssumedSafe()
    {
        var confinement = new PathConfinement([_allowed]);

        // An embedded null makes File.GetAttributes throw, so the leaf cannot be proven to be an ordinary
        // file. Treating unreadable metadata as "not a link" is what would let an unresolved leaf carry an
        // out-of-root target into an export or a rewrite, so it has to fail closed.
        //
        // Path.GetDirectoryName no longer validates characters on .NET 10, so the leaf metadata check is
        // the boundary that must reject this input.
        Assert.False(confinement.Allows(Path.Combine(_allowed, "bad\0name.txt")));
        Assert.Equal(1, confinement.Rejected);
    }
}
