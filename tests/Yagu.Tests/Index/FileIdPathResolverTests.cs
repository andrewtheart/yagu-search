using Microsoft.Win32.SafeHandles;
using Yagu.Services.Index;

namespace Yagu.Tests.Index;

public sealed class FileIdPathResolverTests
{
    [Fact]
    public void ForRoot_RejectsBlankOrNonWindowsRoot()
    {
        Assert.Null(ForRoot(" ", isWindows: true));
        Assert.Null(ForRoot(@"C:\data", isWindows: false));
    }

    [Fact]
    public void ForRoot_InvalidHintIsDisposedAndReturnsNull()
    {
        var invalid = new SafeFileHandle(IntPtr.Zero, ownsHandle: false);

        Assert.Null(ForRoot(@"C:\data", openVolumeHint: (_, _, _, _, _, _, _) => invalid));
        Assert.True(invalid.IsClosed);
    }

    [Fact]
    public void ForRoot_OpeningExceptionReturnsNull()
        => Assert.Null(ForRoot(
            @"C:\data",
            openVolumeHint: (_, _, _, _, _, _, _) => throw new IOException("open failed")));

    [Fact]
    public void ForRoot_ValidHintCreatesDisposableResolver()
    {
        var hint = ValidHandle();
        FileIdPathResolver resolver = Assert.IsType<FileIdPathResolver>(ForRoot(
            @"C:\data", openVolumeHint: (_, _, _, _, _, _, _) => hint));

        resolver.Dispose();
        resolver.Dispose();
        Assert.True(hint.IsClosed);
    }

    [Fact]
    public void TryResolvePath_UsesV2AndExtendedV3DescriptorsAndDisposesHandles()
    {
        var descriptorTypes = new List<int>();
        var openedHandles = new List<SafeFileHandle>();
        FileIdPathResolver.FileByIdOpener openById =
            (SafeFileHandle volumeHint, ref FileIdPathResolver.FILE_ID_DESCRIPTOR descriptor,
                uint access, FileShare share, IntPtr securityAttributes, uint flags) =>
            {
                descriptorTypes.Add(descriptor.Type);
                var handle = ValidHandle();
                openedHandles.Add(handle);
                return handle;
            };
        using FileIdPathResolver resolver = CreateResolver(openFileById: openById);

        Assert.Equal(@"C:\resolved.txt", resolver.TryResolvePath(new UsnFileIdentity(10, 0)));
        Assert.Equal(@"C:\resolved.txt", resolver.TryResolvePath(new UsnFileIdentity(10, 20)));
        Assert.Equal([0, 2], descriptorTypes);
        Assert.All(openedHandles, handle => Assert.True(handle.IsClosed));
    }

    [Fact]
    public void TryResolvePath_InvalidHandleOrNativeExceptionReturnsNull()
    {
        var invalid = new SafeFileHandle(IntPtr.Zero, ownsHandle: false);
        using FileIdPathResolver invalidResolver = CreateResolver(
            openFileById: (SafeFileHandle _, ref FileIdPathResolver.FILE_ID_DESCRIPTOR _, uint _, FileShare _, IntPtr _, uint _) => invalid);
        Assert.Null(invalidResolver.TryResolvePath(new UsnFileIdentity(1, 0)));
        Assert.True(invalid.IsClosed);

        using FileIdPathResolver throwingResolver = CreateResolver(
            openFileById: (SafeFileHandle _, ref FileIdPathResolver.FILE_ID_DESCRIPTOR _, uint _, FileShare _, IntPtr _, uint _) =>
                throw new InvalidOperationException("native failure"));
        Assert.Null(throwingResolver.TryResolvePath(new UsnFileIdentity(1, 0)));
    }

    [Fact]
    public void TryResolvePath_DisposedOrNonWindowsResolverReturnsNull()
    {
        FileIdPathResolver disposed = CreateResolver();
        disposed.Dispose();
        Assert.Null(disposed.TryResolvePath(new UsnFileIdentity(1, 0)));

        using FileIdPathResolver nonWindows = CreateResolver(isWindows: false);
        Assert.Null(nonWindows.TryResolvePath(new UsnFileIdentity(1, 0)));
    }

    [Fact]
    public void GetFinalPath_HandlesSizingAndWriteFailures()
    {
        using SafeFileHandle handle = ValidHandle();
        Assert.Null(FileIdPathResolver.GetFinalPath(handle, (_, _, _, _) => 0));
        Assert.Null(FileIdPathResolver.GetFinalPath(handle,
            (_, buffer, _, _) => buffer is null ? 3u : 0u));
        Assert.Null(FileIdPathResolver.GetFinalPath(handle,
            (_, buffer, _, _) => buffer is null ? 3u : 4u));
    }

    [Theory]
    [InlineData(@"\\?\C:\resolved.txt", @"C:\resolved.txt")]
    [InlineData(@"C:\resolved.txt", @"C:\resolved.txt")]
    public void GetFinalPath_StripsOnlyExtendedLengthPrefix(string nativePath, string expected)
    {
        using SafeFileHandle handle = ValidHandle();
        Assert.Equal(expected, FileIdPathResolver.GetFinalPath(handle, PathReader(nativePath)));
    }

    private static FileIdPathResolver? ForRoot(
        string root,
        bool isWindows = true,
        FileIdPathResolver.VolumeHintOpener? openVolumeHint = null)
        => FileIdPathResolver.ForRoot(
            root,
            isWindows,
            openVolumeHint ?? ((_, _, _, _, _, _, _) => ValidHandle()),
            OpenValidFile,
            PathReader(@"\\?\C:\resolved.txt"));

    private static FileIdPathResolver CreateResolver(
        bool isWindows = true,
        FileIdPathResolver.FileByIdOpener? openFileById = null)
        => new(
            ValidHandle(),
            isWindows,
            openFileById ?? OpenValidFile,
            PathReader(@"\\?\C:\resolved.txt"));

    private static SafeFileHandle OpenValidFile(
        SafeFileHandle volumeHint,
        ref FileIdPathResolver.FILE_ID_DESCRIPTOR descriptor,
        uint access,
        FileShare share,
        IntPtr securityAttributes,
        uint flags)
        => ValidHandle();

    private static FileIdPathResolver.FinalPathReader PathReader(string value)
        => (_, buffer, _, _) =>
        {
            if (buffer is null)
                return (uint)value.Length;
            value.CopyTo(0, buffer, 0, value.Length);
            return (uint)value.Length;
        };

    private static SafeFileHandle ValidHandle()
        => new(new IntPtr(123), ownsHandle: false);
}