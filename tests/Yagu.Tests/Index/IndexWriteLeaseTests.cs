using Yagu.Services.Index;

namespace Yagu.Tests.Index;

public sealed class IndexWriteLeaseTests : IDisposable
{
    private readonly string _sandbox = Path.Combine(Path.GetTempPath(), "yagu-index-lease", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, recursive: true); } catch { }
    }

    [Fact]
    public void Acquire_IsNonBlockingExclusive_AndReleaseAllowsTheNextOwner()
    {
        var paths = new DefaultContentIndexPathProvider(_sandbox, _sandbox);
        using IndexMutationContext first = IndexMutationContext.Acquire(paths);

        Assert.False(IndexMutationContext.TryAcquire(paths, out IndexMutationContext? second));
        Assert.Null(second);
        Assert.Throws<IndexWriteBusyException>(() => IndexMutationContext.Acquire(paths));

        first.Dispose();
        Assert.True(IndexMutationContext.TryAcquire(paths, out second));
        Assert.NotNull(second);
        second!.Dispose();
    }

    [Fact]
    public void EnsureOwns_RejectsWrongRootAndDisposedContext()
    {
        var paths = new DefaultContentIndexPathProvider(Path.Combine(_sandbox, "one"), _sandbox);
        var other = new DefaultContentIndexPathProvider(Path.Combine(_sandbox, "two"), _sandbox);
        IndexMutationContext mutation = IndexMutationContext.Acquire(paths);

        mutation.EnsureOwns(paths);
        Assert.Throws<InvalidOperationException>(() => mutation.EnsureOwns(other));
        mutation.Dispose();
        Assert.Throws<ObjectDisposedException>(() => mutation.EnsureOwns(paths));
    }

    [Fact]
    public void StoreMutators_RejectAContextForAnotherStorageRoot()
    {
        var paths = new DefaultContentIndexPathProvider(Path.Combine(_sandbox, "one"), _sandbox);
        var other = new DefaultContentIndexPathProvider(Path.Combine(_sandbox, "two"), _sandbox);
        using IndexMutationContext wrong = IndexMutationContext.Acquire(other);
        var store = new ContentIndexStore(paths, "scope");

        Assert.Throws<InvalidOperationException>(() => store.DeleteScopeUnderLease(wrong));
    }

    [Fact]
    public void TryAcquire_MapsIoAndAccessDeniedFailuresToBusy()
    {
        var paths = new DefaultContentIndexPathProvider(_sandbox, _sandbox);
        Assert.False(IndexMutationContext.TryAcquire(
            paths,
            _ => throw new IOException("busy"),
            out IndexMutationContext? ioContext));
        Assert.Null(ioContext);

        Assert.False(IndexMutationContext.TryAcquire(
            paths,
            _ => throw new UnauthorizedAccessException("denied"),
            out IndexMutationContext? deniedContext));
        Assert.Null(deniedContext);
    }

    [Fact]
    public void NormalizeRoot_PreservesDriveRootAndTrimsOrdinaryDirectories()
    {
        string driveRoot = Path.GetPathRoot(Path.GetFullPath(_sandbox))!;
        Assert.Equal(driveRoot, IndexMutationContext.NormalizeRoot(driveRoot));
        Assert.Equal(Path.GetFullPath(_sandbox), IndexMutationContext.NormalizeRoot(_sandbox + Path.DirectorySeparatorChar));
        Assert.Throws<ArgumentException>(() => IndexMutationContext.NormalizeRoot(" "));
    }

    [Fact]
    public void Acquire_AutomaticallyRemovesAbandonedBuildWorkspaces()
    {
        var paths = new DefaultContentIndexPathProvider(_sandbox, _sandbox);
        string abandoned = Path.Combine(paths.IndexRoot, ".build-crashed");
        Directory.CreateDirectory(abandoned);

        using IndexMutationContext mutation = IndexMutationContext.Acquire(paths);

        Assert.False(Directory.Exists(abandoned));
    }

    [Fact]
    public void TryAcquire_InitializerFailureDisposesThePartiallyOpenedLease()
    {
        var paths = new DefaultContentIndexPathProvider(_sandbox, _sandbox);
        string lockPath = Path.Combine(paths.IndexRoot, ".writer.lock");
        Assert.False(IndexMutationContext.TryAcquire(
            paths,
            path => new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None),
            (_, _) => throw new IOException("initialize failed"),
            recover: false,
            out IndexMutationContext? context));
        Assert.Null(context);

        using var reopened = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Assert.True(reopened.CanWrite);
    }

    [Fact]
    public void TryAcquire_FatalInitializerFailureReleasesLeaseBeforePropagating()
    {
        var paths = new DefaultContentIndexPathProvider(_sandbox, _sandbox);
        string lockPath = Path.Combine(paths.IndexRoot, ".writer.lock");
        Assert.Throws<OutOfMemoryException>(() => IndexMutationContext.TryAcquire(
            paths,
            path => new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None),
            (_, _) => throw new OutOfMemoryException("fatal"),
            recover: false,
            out _));

        using var reopened = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Assert.True(reopened.CanWrite);
    }

    [Fact]
    public void TryAcquire_FatalOpenerFailurePropagatesWithNoContextToDispose()
    {
        var paths = new DefaultContentIndexPathProvider(_sandbox, _sandbox);
        Assert.Throws<OutOfMemoryException>(() => IndexMutationContext.TryAcquire(
            paths,
            _ => throw new OutOfMemoryException("open failed"),
            (_, _) => { },
            recover: false,
            out _));
    }

    [Fact]
    public void Acquire_ContinuesWhenBestEffortRecoveryHasAnUnexpectedFailure()
    {
        Directory.CreateDirectory(Path.Combine(_sandbox, "scope"));
        var paths = new ThrowingRecoveryPathProvider(_sandbox);
        using IndexMutationContext mutation = IndexMutationContext.Acquire(paths);
        mutation.EnsureOwns(paths);
    }

    [Fact]
    public void Acquire_RecoveryOutOfMemory_ReleasesLeaseBeforePropagating()
    {
        Directory.CreateDirectory(Path.Combine(_sandbox, "scope"));
        var paths = new ThrowingRecoveryPathProvider(_sandbox, outOfMemory: true);
        Assert.Throws<OutOfMemoryException>(() => IndexMutationContext.Acquire(paths));
        using var reopened = new FileStream(
            Path.Combine(_sandbox, ".writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Assert.True(reopened.CanWrite);
    }

    private sealed class ThrowingRecoveryPathProvider(string root, bool outOfMemory = false) : IContentIndexPathProvider
    {
        public string IndexRoot { get; } = root;
        public string GetScopeDirectory(string scopeId)
            => throw (outOfMemory
                ? new OutOfMemoryException("recovery oom")
                : new NullReferenceException("recovery bug"));
    }
}
