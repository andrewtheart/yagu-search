using Yagu.Services.Index;

namespace Yagu.Tests.Index;

public sealed class BoundedFileIdPathResolverTests
{
    private static readonly UsnFileIdentity Identity = new(42, 7);

    [Fact]
    public void ForRoot_WhitespaceRootReturnsNullThroughProductionFactory()
    {
        Assert.Null(BoundedFileIdPathResolver.ForRoot(
            " ", TimeSpan.FromSeconds(1), CancellationToken.None));
    }

    [Fact]
    public void ForRoot_ResolvesAndDisposesDisposableInner()
    {
        var inner = new DisposableResolver(identity => identity == Identity ? @"C:\resolved.txt" : null);
        BoundedFileIdPathResolver? resolver = BoundedFileIdPathResolver.ForRoot(
            @"C:\root",
            TimeSpan.FromSeconds(1),
            CancellationToken.None,
            root =>
            {
                Assert.Equal(@"C:\root", root);
                return inner;
            });

        Assert.NotNull(resolver);
        Assert.Equal(@"C:\resolved.txt", resolver.TryResolvePath(Identity));
        resolver.Dispose();
        Assert.Equal(1, inner.DisposeCount);
    }

    [Fact]
    public void ForRoot_NonDisposableInnerMayReturnNullPath()
    {
        BoundedFileIdPathResolver? resolver = BoundedFileIdPathResolver.ForRoot(
            "root",
            TimeSpan.FromSeconds(1),
            CancellationToken.None,
            _ => new PlainResolver());

        Assert.NotNull(resolver);
        Assert.Null(resolver.TryResolvePath(Identity));
        resolver.Dispose();
    }

    [Fact]
    public void ForRoot_NullFactoryResultReturnsNull()
    {
        Assert.Null(BoundedFileIdPathResolver.ForRoot(
            "root",
            TimeSpan.FromSeconds(1),
            CancellationToken.None,
            _ => null));
    }

    [Fact]
    public void ForRoot_FactoryTimeoutReturnsNull()
    {
        Assert.Null(BoundedFileIdPathResolver.ForRoot(
            "root",
            TimeSpan.FromMilliseconds(10),
            CancellationToken.None,
            _ =>
            {
                Thread.Sleep(50);
                return null;
            }));
    }

    [Fact]
    public void ForRoot_FactoryExceptionAndCancellationPropagate()
    {
        Assert.Throws<InvalidOperationException>(() => BoundedFileIdPathResolver.ForRoot(
            "root",
            TimeSpan.FromSeconds(1),
            CancellationToken.None,
            _ => throw new InvalidOperationException("factory failed")));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => BoundedFileIdPathResolver.ForRoot(
            "root",
            TimeSpan.FromSeconds(1),
            cancellation.Token,
            _ => new PlainResolver()));

        Assert.Throws<ArgumentNullException>(() => BoundedFileIdPathResolver.ForRoot(
            "root",
            TimeSpan.FromSeconds(1),
            CancellationToken.None,
            null!));
    }

    [Fact]
    public void TryResolvePath_TimeoutReturnsNull()
    {
        BoundedFileIdPathResolver? resolver = BoundedFileIdPathResolver.ForRoot(
            "root",
            TimeSpan.FromMilliseconds(10),
            CancellationToken.None,
            _ => new DisposableResolver(_ =>
            {
                Thread.Sleep(50);
                return "late";
            }));

        Assert.NotNull(resolver);
        Assert.Null(resolver.TryResolvePath(Identity));
        resolver.Dispose();
    }

    [Fact]
    public void TryResolvePath_CancelledOperationPropagates()
    {
        using var cancellation = new CancellationTokenSource();
        BoundedFileIdPathResolver? resolver = BoundedFileIdPathResolver.ForRoot(
            "root",
            TimeSpan.FromSeconds(1),
            cancellation.Token,
            _ => new PlainResolver());
        Assert.NotNull(resolver);

        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => resolver.TryResolvePath(Identity));
        resolver.Dispose();
    }

    private sealed class PlainResolver : IFileIdPathResolver
    {
        public string? TryResolvePath(UsnFileIdentity identity) => null;
    }

    private sealed class DisposableResolver(Func<UsnFileIdentity, string?> resolve)
        : IFileIdPathResolver, IDisposable
    {
        public int DisposeCount { get; private set; }

        public string? TryResolvePath(UsnFileIdentity identity) => resolve(identity);

        public void Dispose() => DisposeCount++;
    }
}