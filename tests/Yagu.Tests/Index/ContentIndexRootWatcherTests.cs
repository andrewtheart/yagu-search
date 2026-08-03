using Yagu.Services.Index;

namespace Yagu.Tests.Index;

public sealed class ContentIndexRootWatcherTests : IDisposable
{
    private readonly string _sandbox = Path.Combine(Path.GetTempPath(), "yagu-root-watcher", Guid.NewGuid().ToString("N"));

    public ContentIndexRootWatcherTests() => Directory.CreateDirectory(_sandbox);

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, recursive: true); } catch { }
    }

    [Fact]
    public void Constructor_RejectsNullAndIgnoresBlankOrMissingRoots()
    {
        Assert.Throws<ArgumentNullException>(() => new ContentIndexRootWatcher(null!));

        using var blank = new ContentIndexRootWatcher(" ");
        using var missing = new ContentIndexRootWatcher(Path.Combine(_sandbox, "missing"));
        Assert.False(blank.IsWatching);
        Assert.False(missing.IsWatching);
    }

    [Fact]
    public void Constructor_RegistersConfiguredWatcherAndDisposeIsIdempotent()
    {
        string? createdFor = null;
        bool enabled = false;
        bool disabled = false;
        var watcher = new FileSystemWatcher(_sandbox);
        var rootWatcher = new ContentIndexRootWatcher(
            _sandbox,
            excludedStorageRoot: " ",
            path =>
            {
                createdFor = path;
                return watcher;
            },
            _ => enabled = true,
            _ => disabled = true);

        Assert.True(rootWatcher.IsWatching);
        Assert.Equal(_sandbox, createdFor);
        Assert.True(enabled);
        Assert.True(watcher.IncludeSubdirectories);
        Assert.Equal(64 * 1024, watcher.InternalBufferSize);

        rootWatcher.Dispose();
        rootWatcher.Dispose();
        Assert.True(disabled);
    }

    [Fact]
    public void Constructor_RecoverableFactoryFailuresFallBackButOutOfMemoryPropagates()
    {
        Exception[] recoverable =
        [
            new IOException("limit"),
            new UnauthorizedAccessException("denied"),
            new ArgumentException("bad root"),
        ];
        foreach (Exception failure in recoverable)
        {
            using var watcher = Create(createWatcher: _ => throw failure);
            Assert.False(watcher.IsWatching);
        }

        Assert.Throws<OutOfMemoryException>(() =>
            Create(createWatcher: _ => throw new OutOfMemoryException("pressure")));
    }

    [Fact]
    public void Constructor_EnableFailureDisposesPartiallyConfiguredWatcher()
    {
        var fileWatcher = new FileSystemWatcher(_sandbox);
        using var watcher = Create(
            createWatcher: _ => fileWatcher,
            enableWatcher: _ => throw new IOException("enable failed"));

        Assert.False(watcher.IsWatching);
        Assert.Throws<ObjectDisposedException>(() => fileWatcher.EnableRaisingEvents = true);
    }

    [Fact]
    public void Changed_SuppressesStoragePathsAndRaisesForOtherPaths()
    {
        string storage = Path.Combine(_sandbox, "index-storage");
        using var watcher = Create(excludedStorageRoot: storage);
        var changes = new List<string>();
        watcher.Changed += changes.Add;

        watcher.OnChanged(this, Changed(storage, "segment.bin"));
        watcher.OnChanged(this, Changed(_sandbox, "source.txt"));

        Assert.Equal([_sandbox], changes);
    }

    [Fact]
    public void Changed_WithoutExclusionRaisesAndSwallowsHandlerFailure()
    {
        using var watcher = Create(excludedStorageRoot: null);
        int calls = 0;
        watcher.Changed += _ => calls++;
        watcher.Changed += _ => throw new InvalidOperationException("handler failed");

        watcher.OnChanged(this, Changed(_sandbox, "source.txt"));

        Assert.Equal(1, calls);
    }

    [Fact]
    public void Renamed_SuppressesOnlyMovesWhollyInsideStorage()
    {
        string storage = Path.Combine(_sandbox, "index-storage");
        using var watcher = Create(excludedStorageRoot: storage);
        int calls = 0;
        watcher.Changed += _ => calls++;

        watcher.OnRenamed(this, Renamed(storage, "new.bin", "old.bin"));
        watcher.OnRenamed(this, Renamed(_sandbox, "outside.txt", Path.Combine("index-storage", "old.bin")));
        watcher.OnRenamed(this, Renamed(_sandbox, Path.Combine("index-storage", "new.bin"), "outside.txt"));

        Assert.Equal(2, calls);
    }

    [Fact]
    public void ErrorRaisesHintAndDisposedWatcherSuppressesLaterHints()
    {
        using var watcher = Create();
        int calls = 0;
        watcher.OnError(this, new ErrorEventArgs(new InternalBufferOverflowException("unobserved overflow")));
        watcher.Changed += _ => calls++;

        watcher.OnError(this, new ErrorEventArgs(new InternalBufferOverflowException("overflow")));
        Assert.Equal(1, calls);

        watcher.Dispose();
        watcher.OnError(this, new ErrorEventArgs(new IOException("late")));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Dispose_DisableFailureIsBestEffort()
    {
        ContentIndexRootWatcher watcher = Create(
            disableWatcher: _ => throw new InvalidOperationException("disable failed"));

        watcher.Dispose();
        watcher.Dispose();
    }

    private ContentIndexRootWatcher Create(
        string? excludedStorageRoot = null,
        Func<string, FileSystemWatcher>? createWatcher = null,
        Action<FileSystemWatcher>? enableWatcher = null,
        Action<FileSystemWatcher>? disableWatcher = null)
        => new(
            _sandbox,
            excludedStorageRoot,
            createWatcher ?? (path => new FileSystemWatcher(path)),
            enableWatcher ?? (_ => { }),
            disableWatcher ?? (_ => { }));

    private static FileSystemEventArgs Changed(string directory, string name)
        => new(WatcherChangeTypes.Changed, directory, name);

    private static RenamedEventArgs Renamed(string directory, string name, string oldName)
        => new(WatcherChangeTypes.Renamed, directory, name, oldName);
}