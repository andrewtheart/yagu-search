using Yagu.Services.Mcp;

namespace Yagu.Tests;

/// <summary>
/// Covers the single authorization decision every MCP tool passes through. A gap here is a security
/// hole rather than a bug, so the escape shapes are enumerated explicitly: traversal, device paths,
/// alternate data streams, sibling-prefix roots, relative paths, link escapes, and outbound leakage.
/// </summary>
public class McpAuthorizationPolicyTests
{
    private const string Root = @"C:\allowed";
    private const McpCapability All =
        McpCapability.Read | McpCapability.Search | McpCapability.CloudHydration
        | McpCapability.ExportWrite | McpCapability.Replace | McpCapability.IndexMaintenance
        | McpCapability.DestructiveIndexOps;

    /// <summary>A policy whose link resolution is the identity, so containment is tested in isolation.</summary>
    private static McpAuthorizationPolicy Policy(
        McpCapability capabilities = All,
        string[]? roots = null,
        Func<string, string>? resolver = null)
        => new(roots ?? [Root], capabilities, resolver ?? (p => p));

    // ── Explicit root ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AuthorizeSearchRoot_OmittedRoot_IsRejectedRatherThanSearchingEveryDrive(string? requested)
    {
        var decision = Policy().AuthorizeSearchRoot(requested);

        Assert.False(decision.Allowed);
        Assert.Equal(McpDenialCode.RootRequired, decision.DenialCode);
    }

    [Fact]
    public void AuthorizeSearchRoot_RootItself_IsAllowed()
    {
        var decision = Policy().AuthorizeSearchRoot(Root);

        Assert.True(decision.Allowed);
        Assert.Equal(Root, decision.CanonicalPath);
    }

    [Fact]
    public void AuthorizeSearchRoot_PathInsideRoot_IsAllowed()
    {
        var decision = Policy().AuthorizeSearchRoot(@"C:\allowed\src\deep");

        Assert.True(decision.Allowed);
        Assert.Equal(@"C:\allowed\src\deep", decision.CanonicalPath);
    }

    // ── Containment ──────────────────────────────────────────────────────────

    [Fact]
    public void SiblingDirectoryWithTheRootAsAPrefix_IsDenied()
    {
        // C:\allowedButNot starts with the root string but is NOT inside it.
        var decision = Policy().AuthorizeRead(@"C:\allowedButNot\secret.txt");

        Assert.False(decision.Allowed);
        Assert.Equal(McpDenialCode.OutsideAllowedRoots, decision.DenialCode);
    }

    [Theory]
    [InlineData(@"C:\allowed\..\elsewhere\secret.txt")]
    [InlineData(@"C:\allowed\sub\..\..\secret.txt")]
    [InlineData(@"C:\allowed\.\..\..\Windows\System32\config\SAM")]
    public void TraversalOutOfTheRoot_IsDenied(string path)
    {
        var decision = Policy().AuthorizeRead(path);

        Assert.False(decision.Allowed);
        Assert.Equal(McpDenialCode.OutsideAllowedRoots, decision.DenialCode);
    }

    [Fact]
    public void TraversalThatStaysInsideTheRoot_IsAllowedAndNormalized()
    {
        var decision = Policy().AuthorizeRead(@"C:\allowed\sub\..\file.txt");

        Assert.True(decision.Allowed);
        Assert.Equal(@"C:\allowed\file.txt", decision.CanonicalPath);
    }

    [Fact]
    public void MixedSeparators_AreNormalizedBeforeContainmentIsChecked()
    {
        var decision = Policy().AuthorizeRead(@"C:/allowed/sub/file.txt");

        Assert.True(decision.Allowed);
        Assert.Equal(@"C:\allowed\sub\file.txt", decision.CanonicalPath);
    }

    [Fact]
    public void ContainmentIsCaseInsensitive_MatchingWindowsSemantics()
    {
        var decision = Policy().AuthorizeRead(@"c:\ALLOWED\Sub\File.TXT");

        Assert.True(decision.Allowed);
    }

    [Fact]
    public void TrailingSeparatorOnTheRequest_DoesNotDefeatContainment()
    {
        var decision = Policy().AuthorizeSearchRoot(@"C:\allowed\sub\");

        Assert.True(decision.Allowed);
        Assert.Equal(@"C:\allowed\sub", decision.CanonicalPath);
    }

    [Fact]
    public void QuotedPath_IsAccepted()
    {
        var decision = Policy().AuthorizeRead("\"C:\\allowed\\file.txt\"");

        Assert.True(decision.Allowed);
    }

    // ── Shapes that defeat normalization ─────────────────────────────────────

    [Theory]
    [InlineData(@"\\?\C:\allowed\file.txt")]
    [InlineData(@"\\.\C:\allowed\file.txt")]
    [InlineData(@"//?/C:/allowed/file.txt")]
    [InlineData(@"\\?\UNC\server\share\file.txt")]
    public void DeviceAndExtendedLengthPaths_AreDenied(string path)
    {
        var decision = Policy().AuthorizeRead(path);

        Assert.False(decision.Allowed);
        Assert.Equal(McpDenialCode.DevicePath, decision.DenialCode);
    }

    [Theory]
    [InlineData(@"C:\allowed\file.txt:hidden")]
    [InlineData(@"C:\allowed\file.txt:hidden:$DATA")]
    [InlineData(@"C:\allowed\dir:$INDEX_ALLOCATION")]
    public void AlternateDataStreams_AreDenied(string path)
    {
        var decision = Policy().AuthorizeRead(path);

        Assert.False(decision.Allowed);
        Assert.Equal(McpDenialCode.AlternateDataStream, decision.DenialCode);
    }

    [Theory]
    [InlineData(@"relative\file.txt")]
    [InlineData(@"..\escape.txt")]
    [InlineData("file.txt")]
    [InlineData(@"\rooted-but-driveless\file.txt")]
    public void RelativePaths_AreDenied_BecauseTheWorkingDirectoryIsNotOurs(string path)
    {
        var decision = Policy().AuthorizeRead(path);

        Assert.False(decision.Allowed);
        Assert.Equal(McpDenialCode.NotFullyQualified, decision.DenialCode);
    }

    [Fact]
    public void UncPath_IsDeniedWhenNoUncRootIsConfigured()
    {
        var decision = Policy().AuthorizeRead(@"\\server\share\file.txt");

        Assert.False(decision.Allowed);
        Assert.Equal(McpDenialCode.OutsideAllowedRoots, decision.DenialCode);
    }

    [Fact]
    public void UncPath_IsAllowedWhenItsOwnUncRootIsConfigured()
    {
        var policy = Policy(roots: [@"\\server\share"]);

        var decision = policy.AuthorizeRead(@"\\server\share\sub\file.txt");

        Assert.True(decision.Allowed);
    }

    // ── Link escapes ─────────────────────────────────────────────────────────

    [Fact]
    public void LinkInsideTheRootPointingOutside_IsDenied()
    {
        // The requested path is textually contained, but its real target is not.
        var policy = Policy(resolver: p =>
            p.StartsWith(@"C:\allowed\link", StringComparison.OrdinalIgnoreCase)
                ? @"C:\elsewhere\secret.txt"
                : p);

        var decision = policy.AuthorizeRead(@"C:\allowed\link\secret.txt");

        Assert.False(decision.Allowed);
        Assert.Equal(McpDenialCode.OutsideAllowedRoots, decision.DenialCode);
        Assert.Contains("resolves to", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void LinkInsideTheRootPointingBackInside_IsAllowed_AndReportsTheFinalTarget()
    {
        var policy = Policy(resolver: p =>
            p.Equals(@"C:\allowed\link", StringComparison.OrdinalIgnoreCase) ? @"C:\allowed\real" : p);

        var decision = policy.AuthorizeRead(@"C:\allowed\link");

        Assert.True(decision.Allowed);
        Assert.Equal(@"C:\allowed\real", decision.CanonicalPath);
    }

    [Fact]
    public void FailsClosed_WhenTheFinalTargetCannotBeResolved()
    {
        var policy = Policy(resolver: _ => throw new IOException("device not ready"));

        var decision = policy.AuthorizeRead(@"C:\allowed\file.txt");

        Assert.False(decision.Allowed);
        Assert.Equal(McpDenialCode.UnresolvablePath, decision.DenialCode);
    }

    [Fact]
    public void FailsClosed_WhenTheResolverReturnsNothing()
    {
        var policy = Policy(resolver: _ => string.Empty);

        var decision = policy.AuthorizeRead(@"C:\allowed\file.txt");

        Assert.False(decision.Allowed);
        Assert.Equal(McpDenialCode.UnresolvablePath, decision.DenialCode);
    }

    // ── The REAL resolver against REAL reparse points ────────────────────────
    //
    // Every test above injects a fake resolver, which is exactly how a defect in the real one survived:
    // File.ResolveLinkTarget only resolves when the LEAF is a reparse point, so a junction part-way
    // through the path was never followed and an escaping read was authorized. These tests use the
    // shipped resolver against a junction created on disk. Junctions need no elevation.

    [Fact]
    public void RealResolver_JunctionEscapingTheRoot_IsDenied()
    {
        using var fixture = new JunctionFixture();
        Assert.True(fixture.Created, "a directory junction could not be created; this security check cannot be skipped.");

        var policy = new McpAuthorizationPolicy([fixture.AllowedRoot], All);

        var decision = policy.AuthorizeRead(Path.Combine(fixture.LinkPath, "secret.txt"));

        Assert.False(decision.Allowed);
        Assert.Equal(McpDenialCode.OutsideAllowedRoots, decision.DenialCode);
    }

    [Fact]
    public void RealResolver_JunctionDirectoryItself_IsDenied()
    {
        using var fixture = new JunctionFixture();
        Assert.True(fixture.Created, "a directory junction could not be created; this security check cannot be skipped.");

        var policy = new McpAuthorizationPolicy([fixture.AllowedRoot], All);

        Assert.False(policy.AuthorizeSearchRoot(fixture.LinkPath).Allowed);
    }

    [Fact]
    public void RealResolver_WriteTargetInsideAnEscapingJunction_IsDenied()
    {
        // The file does not exist yet, so the resolver must canonicalize the deepest existing ancestor.
        using var fixture = new JunctionFixture();
        Assert.True(fixture.Created, "a directory junction could not be created; this security check cannot be skipped.");

        var policy = new McpAuthorizationPolicy([fixture.AllowedRoot], All);

        var decision = policy.AuthorizeWrite(Path.Combine(fixture.LinkPath, "new-file.txt"));

        Assert.False(decision.Allowed);
        Assert.Equal(McpDenialCode.OutsideAllowedRoots, decision.DenialCode);
    }

    [Fact]
    public void RealResolver_OrdinaryFileInsideTheRoot_IsStillAllowed()
    {
        using var fixture = new JunctionFixture();
        Assert.True(fixture.Created, "a directory junction could not be created; this security check cannot be skipped.");

        var policy = new McpAuthorizationPolicy([fixture.AllowedRoot], All);

        var decision = policy.AuthorizeRead(fixture.InsideFile);

        Assert.True(decision.Allowed, decision.Reason);
        Assert.Equal(fixture.InsideFile, decision.CanonicalPath, ignoreCase: true);
    }

    [Fact]
    public void RealResolver_NonExistentPathInsideTheRoot_IsAllowed()
    {
        using var fixture = new JunctionFixture();
        Assert.True(fixture.Created, "a directory junction could not be created; this security check cannot be skipped.");

        var policy = new McpAuthorizationPolicy([fixture.AllowedRoot], All);

        Assert.True(policy.AuthorizeWrite(Path.Combine(fixture.AllowedRoot, "not-yet.txt")).Allowed);
    }

    [Fact]
    public void RealResolver_ARootThatIsItselfAJunction_StillAuthorizesPathsUnderIt()
    {
        // Requests are compared by their FINAL target, so a root stored only as the name the user typed
        // denies everything beneath it when that name is a link — which is the normal shape for a
        // redirected Known Folder, a Dev Drive mount, or a OneDrive-backed folder.
        using var fixture = new JunctionFixture();
        Assert.True(fixture.Created, "a directory junction could not be created; this security check cannot be skipped.");

        // fixture.LinkPath is a junction to the "outside" directory; granting the LINK must work.
        var policy = new McpAuthorizationPolicy([fixture.LinkPath], All);

        var decision = policy.AuthorizeRead(Path.Combine(fixture.LinkPath, "secret.txt"));

        Assert.True(decision.Allowed, decision.Reason);
    }

    [Fact]
    public void RealResolver_ARootThatIsItselfAJunction_DoesNotAuthorizeItsSiblings()
    {
        // Resolving the root must not widen it: the junction's PARENT is still out of bounds.
        using var fixture = new JunctionFixture();
        Assert.True(fixture.Created, "a directory junction could not be created; this security check cannot be skipped.");

        var policy = new McpAuthorizationPolicy([fixture.LinkPath], All);

        Assert.False(policy.AuthorizeRead(fixture.InsideFile).Allowed);
    }

    [Fact]
    public void DestructiveIndexWork_NeedsTheDestructiveGrantOnTopOfMaintenance()
    {
        // A rebuild discards the stored index and removing a root de-registers it. A user who grants
        // IndexMaintenance while deliberately withholding DestructiveIndexOps is asking for exactly that
        // distinction, so the capability has to gate something.
        var maintenanceOnly = Policy(McpCapability.IndexMaintenance);

        Assert.True(maintenanceOnly.AuthorizeIndexRoot(@"C:\allowed\project").Allowed);

        var decision = maintenanceOnly.AuthorizeDestructiveIndexRoot(@"C:\allowed\project");
        Assert.False(decision.Allowed);
        Assert.Equal(McpDenialCode.CapabilityDenied, decision.DenialCode);
    }

    [Fact]
    public void DestructiveIndexWork_IsAllowedWithBothGrants()
    {
        var both = Policy(McpCapability.IndexMaintenance | McpCapability.DestructiveIndexOps);

        Assert.True(both.AuthorizeDestructiveIndexRoot(@"C:\allowed\project").Allowed);
    }

    [Fact]
    public void DestructiveIndexWork_StillHonorsContainment()
    {
        var both = Policy(McpCapability.IndexMaintenance | McpCapability.DestructiveIndexOps);

        var decision = both.AuthorizeDestructiveIndexRoot(@"C:\elsewhere\project");
        Assert.False(decision.Allowed);
        Assert.Equal(McpDenialCode.OutsideAllowedRoots, decision.DenialCode);
    }

    [Theory]
    [InlineData(@"C:\allowed\file.txt", @"\\?\C:\allowed\file.txt")]
    [InlineData(@"\\server\share\file.txt", @"\\?\UNC\server\share\file.txt")]
    [InlineData(@"\\?\C:\already", @"\\?\C:\already")]
    [InlineData(@"\\?\UNC\server\share", @"\\?\UNC\server\share")]
    [InlineData(@"\\.\PhysicalDrive0", @"\\.\PhysicalDrive0")]
    [InlineData("", "")]
    public void ToExtendedLengthPath_ProducesTheFormCreateFileNeeds(string input, string expected)
    {
        // CreateFileW is capped at MAX_PATH without this prefix, while the .NET APIs that actually read
        // and write the file are not. That asymmetry made every path at or beyond 260 characters
        // unresolvable, so containment silently degraded to the string-prefix test.
        Assert.Equal(expected, McpAuthorizationPolicy.ToExtendedLengthPath(input));
    }

    [Fact]
    public void ToExtendedLengthPath_RoundTripsThroughStripExtendedPrefix()
    {
        foreach (string path in new[] { @"C:\allowed\file.txt", @"\\server\share\file.txt" })
            Assert.Equal(path, McpAuthorizationPolicy.StripExtendedPrefix(McpAuthorizationPolicy.ToExtendedLengthPath(path)));
    }

    [Fact]
    public void RealResolver_LongPathInsideTheRoot_IsStillResolvable()
    {
        // Guards the MAX_PATH regression from the resolver's side: a path well past 260 characters must
        // resolve to itself rather than failing and being waved through by the prefix test.
        using var fixture = new JunctionFixture();

        string deep = fixture.AllowedRoot;
        for (int i = 0; i < 12; i++)
            deep = Path.Combine(deep, new string((char)('a' + i), 24));

        Directory.CreateDirectory(deep);
        string file = Path.Combine(deep, "deep.txt");
        File.WriteAllText(file, "deep");
        Assert.True(file.Length > 300, $"the fixture path must exceed MAX_PATH to be meaningful; it was {file.Length}.");

        var policy = new McpAuthorizationPolicy([fixture.AllowedRoot], All);

        var decision = policy.AuthorizeRead(file);

        Assert.True(decision.Allowed, decision.Reason);
        Assert.Equal(file, decision.CanonicalPath, ignoreCase: true);
    }

    /// <summary>A temp tree with an allowed root containing a junction that points outside it.</summary>
    private sealed class JunctionFixture : IDisposable
    {
        private readonly string _base;

        public JunctionFixture()
        {
            _base = Path.Combine(Path.GetTempPath(), "yagu-junction-" + Guid.NewGuid().ToString("n")[..8]);
            AllowedRoot = Path.Combine(_base, "allowed");
            string outside = Path.Combine(_base, "outside");
            LinkPath = Path.Combine(AllowedRoot, "escape");
            InsideFile = Path.Combine(AllowedRoot, "ordinary.txt");

            Directory.CreateDirectory(AllowedRoot);
            Directory.CreateDirectory(outside);
            File.WriteAllText(InsideFile, "inside");
            File.WriteAllText(Path.Combine(outside, "secret.txt"), "secret");

            Created = TryCreateJunction(LinkPath, outside);
        }

        public string AllowedRoot { get; }
        public string LinkPath { get; }
        public string InsideFile { get; }
        public bool Created { get; }

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

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(LinkPath))
                    Directory.Delete(LinkPath);
                if (Directory.Exists(_base))
                    Directory.Delete(_base, recursive: true);
            }
            catch (Exception)
            {
                // Temp cleanup is best effort.
            }
        }
    }

    // ── Capabilities ─────────────────────────────────────────────────────────

    [Fact]
    public void ReadCapability_DoesNotGrantWriteOrReplace()
    {
        var policy = Policy(capabilities: McpCapability.Read);

        Assert.True(policy.AuthorizeRead(@"C:\allowed\a.txt").Allowed);

        var write = policy.AuthorizeWrite(@"C:\allowed\out.csv");
        var replace = policy.AuthorizeReplaceTarget(@"C:\allowed\a.txt");
        var search = policy.AuthorizeSearchRoot(Root);

        Assert.Equal(McpDenialCode.CapabilityDenied, write.DenialCode);
        Assert.Equal(McpDenialCode.CapabilityDenied, replace.DenialCode);
        Assert.Equal(McpDenialCode.CapabilityDenied, search.DenialCode);
    }

    [Fact]
    public void IndexMaintenance_DoesNotGrantDestructiveGlobalOperations()
    {
        var policy = Policy(capabilities: McpCapability.IndexMaintenance);

        Assert.True(policy.AuthorizeIndexRoot(@"C:\allowed\proj").Allowed);
        Assert.Equal(McpDenialCode.CapabilityDenied, policy.AuthorizeGlobalIndexOperation().DenialCode);
    }

    [Fact]
    public void DestructiveIndexOps_AreAllowedOnlyWithTheirOwnCapability()
    {
        var policy = Policy(capabilities: McpCapability.DestructiveIndexOps);

        Assert.True(policy.AuthorizeGlobalIndexOperation().Allowed);
    }

    [Fact]
    public void HasCapability_ReportsExactlyWhatWasGranted()
    {
        var policy = Policy(capabilities: McpCapability.Read | McpCapability.Search);

        Assert.True(policy.HasCapability(McpCapability.Read));
        Assert.True(policy.HasCapability(McpCapability.Search));
        Assert.False(policy.HasCapability(McpCapability.CloudHydration));
        Assert.False(policy.HasCapability(McpCapability.Replace));
    }

    // ── No roots configured ──────────────────────────────────────────────────

    [Fact]
    public void PolicyWithNoRoots_AuthorizesNothing()
    {
        var policy = Policy(roots: []);

        Assert.Equal(McpDenialCode.NoAllowedRoots, policy.AuthorizeRead(@"C:\anything").DenialCode);
        Assert.Equal(McpDenialCode.NoAllowedRoots, policy.AuthorizeWrite(@"C:\anything").DenialCode);
        Assert.Equal(McpDenialCode.NoAllowedRoots, policy.AuthorizeIndexRoot(@"C:\anything").DenialCode);
        Assert.Equal(McpDenialCode.NoAllowedRoots, policy.AuthorizeGlobalIndexOperation().DenialCode);
        Assert.Equal(McpDenialCode.NoAllowedRoots, policy.AuthorizeOutboundPath(@"C:\anything").DenialCode);
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("not-fully-qualified")]
    [InlineData(@"..\escape")]
    [InlineData(@"\\?\C:\allowed")]
    [InlineData(@"C:\allowed\x.txt:stream")]
    public void UnusableConfiguredRoot_IsDroppedRatherThanWideningThePolicy(string badRoot)
    {
        // A relative root is the dangerous one: Path.GetFullPath would happily resolve it against the
        // server's working directory, silently granting access to an arbitrary folder.
        var policy = Policy(roots: [badRoot, Root]);

        Assert.Single(policy.AllowedRoots);
        Assert.Equal(Root, policy.AllowedRoots[0]);
    }

    // ── Outbound re-authorization ────────────────────────────────────────────

    [Fact]
    public void OutboundPathOutsideTheRoots_IsDenied()
    {
        // A session file stored inside an allowed root can reference matches from anywhere, so results
        // are filtered on the way out too.
        var decision = Policy().AuthorizeOutboundPath(@"C:\elsewhere\leaked.txt");

        Assert.False(decision.Allowed);
        Assert.Equal(McpDenialCode.OutsideAllowedRoots, decision.DenialCode);
    }

    [Fact]
    public void OutboundPathInsideTheRoots_IsAllowed()
    {
        Assert.True(Policy().AuthorizeOutboundPath(@"C:\allowed\hit.txt").Allowed);
    }

    [Fact]
    public void OutboundPath_DoesNotRequireACapability_ButStillEnforcesContainment()
    {
        var policy = Policy(capabilities: McpCapability.None);

        Assert.True(policy.AuthorizeOutboundPath(@"C:\allowed\hit.txt").Allowed);
        Assert.Equal(McpDenialCode.OutsideAllowedRoots, policy.AuthorizeOutboundPath(@"C:\elsewhere\x").DenialCode);
    }

    // ── Write destinations ───────────────────────────────────────────────────

    [Fact]
    public void WriteTargetThatDoesNotExistYet_IsAllowedWhenItsParentIsInsideARoot()
    {
        var decision = Policy().AuthorizeWrite(@"C:\allowed\reports\new-report.csv");

        Assert.True(decision.Allowed);
    }

    [Fact]
    public void WriteTargetWhoseParentResolvesOutsideARoot_IsDenied()
    {
        var policy = Policy(resolver: p =>
            p.StartsWith(@"C:\allowed\reports", StringComparison.OrdinalIgnoreCase)
                ? p.Replace(@"C:\allowed\reports", @"D:\exfil", StringComparison.OrdinalIgnoreCase)
                : p);

        var decision = policy.AuthorizeWrite(@"C:\allowed\reports\new-report.csv");

        Assert.False(decision.Allowed);
        Assert.Equal(McpDenialCode.OutsideAllowedRoots, decision.DenialCode);
    }

    // ── Multiple roots ───────────────────────────────────────────────────────

    [Fact]
    public void MultipleRoots_AreEachHonored()
    {
        var policy = Policy(roots: [@"C:\allowed", @"D:\projects"]);

        Assert.True(policy.AuthorizeRead(@"C:\allowed\a.txt").Allowed);
        Assert.True(policy.AuthorizeRead(@"D:\projects\b.txt").Allowed);
        Assert.False(policy.AuthorizeRead(@"E:\other\c.txt").Allowed);
    }

    [Fact]
    public void DriveRoot_IsHandledWithoutDoublingItsSeparator()
    {
        var policy = Policy(roots: [@"C:\"]);

        var decision = policy.AuthorizeRead(@"C:\anywhere\file.txt");

        Assert.True(decision.Allowed);
        Assert.Equal(@"C:\anywhere\file.txt", decision.CanonicalPath);
    }

    // ── Normalizing what GetFinalPathNameByHandle hands back ──
    //
    // It always returns an extended-length path. Containment is compared against ordinary roots, and
    // IsDevicePath rejects a \\?\ prefix outright, so a prefix that leaked through here would make every
    // resolved path fail containment against the very root it lives in.

    [Fact]
    public void AResolvedUncPath_IsRestoredToItsOrdinaryDoubleBackslashForm()
    {
        Assert.Equal(
            @"\\server\share\projects\a.txt",
            McpAuthorizationPolicy.StripExtendedPrefix(@"\\?\UNC\server\share\projects\a.txt"));
    }

    [Fact]
    public void AResolvedLocalPath_LosesOnlyItsDevicePrefix()
    {
        Assert.Equal(@"C:\projects\a.txt", McpAuthorizationPolicy.StripExtendedPrefix(@"\\?\C:\projects\a.txt"));
    }

    [Fact]
    public void APathWithNoExtendedPrefix_IsReturnedUnchanged()
    {
        Assert.Equal(@"C:\projects\a.txt", McpAuthorizationPolicy.StripExtendedPrefix(@"C:\projects\a.txt"));
        Assert.Equal(@"\\server\share\a.txt", McpAuthorizationPolicy.StripExtendedPrefix(@"\\server\share\a.txt"));
    }

    [Fact]
    public void TheUncFormIsRecognizedBeforeThePlainDeviceForm()
    {
        // Both prefixes start with \\?\, so checking the device form first would turn a UNC path into
        // "UNC\server\share" — a relative-looking path that matches no configured root.
        string stripped = McpAuthorizationPolicy.StripExtendedPrefix(@"\\?\UNC\server\share");

        Assert.DoesNotContain("UNC", stripped, StringComparison.Ordinal);
        Assert.StartsWith(@"\\", stripped, StringComparison.Ordinal);
    }
}
