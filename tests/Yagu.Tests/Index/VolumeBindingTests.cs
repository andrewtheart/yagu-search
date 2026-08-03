using System.Text;
using Yagu.Services.Index;

namespace Yagu.Tests.Index;

public sealed class VolumeBindingTests
{
    private static VolumeBinding Binding(string guid = @"\\?\Volume{ABC}\", ulong serial = 42, string fileSystem = "NTFS")
        => new(guid, serial, fileSystem, @"E:\", "folder");

    private static IndexManifest Manifest(ulong serial = 42, string fileSystem = "NTFS") => new()
    {
        VolumeSerialNumber = serial,
        VolumeGuidPath = @"\\?\Volume{ABC}\",
        FileSystemName = fileSystem,
    };

    private static VolumeBindingReader.VolumePathReader PathReader(string value, bool succeeds = true)
        => (_, result, _) =>
        {
            result.Append(value);
            return succeeds;
        };

    private static VolumeBindingReader.VolumeInformationReader InformationReader(
        bool succeeds = true,
        uint serial = 42,
        string fileSystem = "NTFS")
        => (string _, StringBuilder? _, int _, out uint actualSerial, out uint maximumComponentLength,
            out uint fileSystemFlags, StringBuilder fileSystemName, int _) =>
        {
            actualSerial = serial;
            maximumComponentLength = 255;
            fileSystemFlags = 0;
            fileSystemName.Append(fileSystem);
            return succeeds;
        };

    private static VolumeBinding? Capture(
        string path = @"C:\mount\folder",
        bool isWindows = true,
        VolumeBindingReader.VolumePathReader? getVolumePath = null,
        VolumeBindingReader.VolumePathReader? getVolumeName = null,
        VolumeBindingReader.VolumeInformationReader? getVolumeInformation = null,
        Func<string, FileIdentity?>? getFileIdentity = null)
        => VolumeBindingReader.TryCapture(
            path,
            isWindows,
            getVolumePath ?? PathReader(@"C:\mount\"),
            getVolumeName ?? PathReader(@"\\?\Volume{ABC}"),
            getVolumeInformation ?? InformationReader(),
            getFileIdentity ?? (_ => null));

    [Theory]
    [InlineData("NTFS", true)]
    [InlineData("ntfs", true)]
    [InlineData("ReFS", true)]
    [InlineData("FAT32", false)]
    public void SupportsChangeJournal_RequiresNtfsOrRefs(string fileSystem, bool expected)
        => Assert.Equal(expected, Binding(fileSystem: fileSystem).SupportsChangeJournal);

    [Fact]
    public void Matches_RequiresGuidSerialAndFilesystem()
    {
        Assert.True(VolumeBindingReader.Matches(Binding(), Binding(@"\\?\volume{abc}\")));
        Assert.False(VolumeBindingReader.Matches(Binding(serial: 0), Binding()));
        Assert.False(VolumeBindingReader.Matches(Binding(), Binding(serial: 0)));
        Assert.False(VolumeBindingReader.Matches(Binding(), Binding(serial: 43)));
        Assert.False(VolumeBindingReader.Matches(Binding(), Binding(@"\\?\Volume{DEF}\")));
        Assert.False(VolumeBindingReader.Matches(Binding(), Binding(fileSystem: "ReFS")));
    }

    [Fact]
    public void MatchesManifest_NewBindingRejectsDriveLetterReuse()
    {
        var manifest = new IndexManifest
        {
            VolumeSerialNumber = 42,
            VolumeGuidPath = @"\\?\Volume{ABC}\",
            FileSystemName = "NTFS",
        };

        Assert.True(VolumeBindingReader.MatchesManifest(manifest, Binding(), out _));
        Assert.False(VolumeBindingReader.MatchesManifest(manifest, Binding(@"\\?\Volume{OTHER}\"), out string reason));
        Assert.Contains("GUID", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MatchesManifest_LegacyRemainsCompatibleUntilRebuilt()
    {
        Assert.True(VolumeBindingReader.MatchesManifest(
            new IndexManifest { VolumeSerialNumber = 42 }, Binding(), out _));
        Assert.True(VolumeBindingReader.MatchesManifest(
            new IndexManifest { VolumeSerialNumber = 0 }, Binding(), out _));
    }

    [Fact]
    public void MatchesManifest_RejectsUnsupportedOrChangedVolumeMetadata()
    {
        IndexManifest manifest = Manifest();

        Assert.Throws<ArgumentNullException>(() =>
            VolumeBindingReader.MatchesManifest(null!, Binding(), out _));
        Assert.False(VolumeBindingReader.MatchesManifest(manifest, Binding(fileSystem: "FAT32"), out string unsupported));
        Assert.Contains("freshness", unsupported);

        Assert.False(VolumeBindingReader.MatchesManifest(Manifest(serial: 0), Binding(), out string missingExpectedSerial));
        Assert.Contains("serial", missingExpectedSerial);
        Assert.False(VolumeBindingReader.MatchesManifest(manifest, Binding(serial: 0), out string missingActualSerial));
        Assert.Contains("serial", missingActualSerial);
        Assert.False(VolumeBindingReader.MatchesManifest(manifest, Binding(serial: 43), out string changedSerial));
        Assert.Contains("serial", changedSerial);

        Assert.False(VolumeBindingReader.MatchesManifest(Manifest(fileSystem: "ReFS"), Binding(), out string changedFileSystem));
        Assert.Contains("filesystem", changedFileSystem);
        Assert.True(VolumeBindingReader.MatchesManifest(Manifest(fileSystem: " "), Binding(), out string matchedReason));
        Assert.Empty(matchedReason);
    }

    [Fact]
    public void TryCapture_CoreRejectsUnsupportedPlatformBlankPathAndNativeFailures()
    {
        Assert.Null(Capture(isWindows: false));
        Assert.Null(Capture(path: " "));
        Assert.Null(Capture(getVolumePath: PathReader(@"C:\mount\", succeeds: false)));
        Assert.Null(Capture(getVolumeName: PathReader(@"\\?\Volume{ABC}\", succeeds: false)));
        Assert.Null(Capture(getVolumeInformation: InformationReader(succeeds: false)));
    }

    [Fact]
    public void TryCapture_CoreBuildsNormalizedNestedBindingAndUsesLegacySerialFallback()
    {
        VolumeBinding binding = Assert.IsType<VolumeBinding>(Capture(
            getVolumeName: PathReader(@" \\?\volume{abc} "),
            getVolumeInformation: InformationReader(serial: 73, fileSystem: " NTFS ")));

        Assert.Equal(@"\\?\VOLUME{ABC}\", binding.VolumeGuidPath);
        Assert.Equal((ulong)73, binding.VolumeSerialNumber);
        Assert.Equal("NTFS", binding.FileSystemName);
        Assert.Equal(@"C:\mount\", binding.MountPoint);
        Assert.Equal("folder", binding.RootRelativePath);
    }

    [Fact]
    public void TryCapture_CoreUsesDurableIdentityAndEmptyRelativePathAtMountRoot()
    {
        var identity = new FileIdentity(99, new UsnFileIdentity(1, 2));
        VolumeBinding binding = Assert.IsType<VolumeBinding>(Capture(
            path: @"C:\mount",
            getFileIdentity: _ => identity));

        Assert.Equal((ulong)99, binding.VolumeSerialNumber);
        Assert.Empty(binding.RootRelativePath);
    }

    [Fact]
    public void TryCapture_CoreCatchesExpectedIoFailuresButPropagatesOutOfMemory()
    {
        Exception[] recoverable =
        [
            new IOException("io"),
            new UnauthorizedAccessException("denied"),
            new ArgumentException("bad path"),
            new NotSupportedException("unsupported"),
        ];
        foreach (Exception failure in recoverable)
        {
            Assert.Null(Capture(getVolumePath: (_, _, _) => throw failure));
        }

        Assert.Throws<OutOfMemoryException>(() =>
            Capture(getVolumePath: (_, _, _) => throw new OutOfMemoryException("pressure")));
    }

    [Fact]
    public void NormalizeGuidPath_HandlesNullWhitespaceAndTrailingSeparator()
    {
        Assert.Equal("\\", VolumeBindingReader.NormalizeGuidPath(null!));
        Assert.Equal(@"\\?\VOLUME{ABC}\", VolumeBindingReader.NormalizeGuidPath(@" \\?\volume{abc} "));
        Assert.Equal(@"\\?\VOLUME{ABC}\", VolumeBindingReader.NormalizeGuidPath(@"\\?\Volume{ABC}\"));
    }

    [Fact]
    public void IndexVolumeChangedException_PreservesMessage()
    {
        var exception = new IndexVolumeChangedException("volume changed");
        Assert.Equal("volume changed", exception.Message);
        Assert.IsAssignableFrom<IOException>(exception);
    }

    [Fact]
    public void TryCapture_CurrentTempVolume_ReturnsStableSupportedBinding()
    {
        VolumeBinding? first = VolumeBindingReader.TryCapture(Path.GetTempPath());
        if (first is null)
            return; // self-gate on unusual test environments
        VolumeBinding? second = VolumeBindingReader.TryCapture(Path.GetTempPath());
        Assert.NotNull(second);
        Assert.True(VolumeBindingReader.Matches(first.Value, second.Value));
        Assert.True(first.Value.SupportsChangeJournal);
    }
}