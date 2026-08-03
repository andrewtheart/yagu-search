using System.Text;
using System.Threading;
using Yagu.Services;
using Yagu.Services.Index;
using Xunit;

namespace Yagu.Tests.Index;

/// <summary>
/// Tests the optional FileSystemWatcher latency-hint layer (plan §11.4, Phase 3): the pure enable-gate
/// (<see cref="ContentIndexWatcherHints.ShouldEnable"/>), the clock-injected coalescing
/// (<see cref="RootChangeDebouncer"/>), and the end-to-end <see cref="ContentIndexWatcherHintService"/>
/// (a real file change fires exactly one debounced callback). The hint never establishes freshness — these
/// tests assert only the debounce/dispatch behavior, not index correctness.
/// </summary>
public sealed class ContentIndexWatcherHintTests : IDisposable
{
    private readonly string _sandbox;

    public ContentIndexWatcherHintTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "yagu-watcher-hint", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_sandbox)) Directory.Delete(_sandbox, recursive: true); } catch { /* best effort */ }
    }

    // ── Enable gate (pure) ──

    [Fact]
    public void ShouldEnable_OffByDefault()
    {
        Assert.False(ContentIndexWatcherHints.ShouldEnable(new AppSettings()));
    }

    [Fact]
    public void ShouldEnable_RequiresMasterFeatureAndOptIn()
    {
        // Opt-in but master off → still off.
        Assert.False(ContentIndexWatcherHints.ShouldEnable(new AppSettings
        {
            EnableContentIndex = false,
            IndexUseWatcherHints = true,
            IndexUpdateMode = AppSettings.IndexUpdateModeAutomaticIncremental,
        }));

        // Master on + opt-in + automatic mode → on.
        Assert.True(ContentIndexWatcherHints.ShouldEnable(new AppSettings
        {
            EnableContentIndex = true,
            IndexUseWatcherHints = true,
            IndexUpdateMode = AppSettings.IndexUpdateModeAutomaticIncremental,
        }));
    }

    [Fact]
    public void ShouldEnable_ManualMode_IsOff()
    {
        // A watcher would do nothing useful when updates are manual.
        Assert.False(ContentIndexWatcherHints.ShouldEnable(new AppSettings
        {
            EnableContentIndex = true,
            IndexUseWatcherHints = true,
            IndexUpdateMode = AppSettings.DefaultIndexUpdateMode, // ManualFullRebuild
        }));
    }

    [Fact]
    public void ShouldEnable_FullRebuildWhenDirtyMode_IsOn()
    {
        Assert.True(ContentIndexWatcherHints.ShouldEnable(new AppSettings
        {
            EnableContentIndex = true,
            IndexUseWatcherHints = true,
            IndexUpdateMode = AppSettings.IndexUpdateModeAutomaticFullRebuildWhenDirty,
        }));
    }

    // ── Debouncer (pure, clock-injected) ──

    [Fact]
    public void Debouncer_CoalescesBurst_UntilQuiet()
    {
        var now = DateTimeOffset.UtcNow;
        var d = new RootChangeDebouncer(TimeSpan.FromSeconds(3));

        d.Signal(@"C:\r", now);
        d.Signal(@"C:\r", now + TimeSpan.FromMilliseconds(500)); // a burst resets the quiet timer
        Assert.True(d.HasPending);
        Assert.Empty(d.TakeDue(now + TimeSpan.FromSeconds(2)));  // not quiet yet

        // Quiet window elapsed since the LAST signal → one due root, then cleared.
        var due = d.TakeDue(now + TimeSpan.FromMilliseconds(500) + TimeSpan.FromSeconds(3));
        Assert.Equal(new[] { @"C:\r" }, due);
        Assert.False(d.HasPending);
        Assert.Empty(d.TakeDue(now + TimeSpan.FromSeconds(30))); // taken once only
    }

    [Fact]
    public void Debouncer_TracksRootsIndependently()
    {
        var now = DateTimeOffset.UtcNow;
        var d = new RootChangeDebouncer(TimeSpan.FromSeconds(1));
        d.Signal(@"C:\a", now);
        d.Signal(@"C:\b", now + TimeSpan.FromMilliseconds(900));

        // At now+1.1s, A settled (1.1s quiet) but B has not (0.2s quiet).
        var due = d.TakeDue(now + TimeSpan.FromMilliseconds(1100));
        Assert.Equal(new[] { @"C:\a" }, due);
        Assert.True(d.HasPending); // B still pending
    }

    [Fact]
    public void Debouncer_RejectsInvalidWindows()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RootChangeDebouncer(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RootChangeDebouncer(
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Debouncer_ContinuousActivity_FiresAtMaximumBatchAge()
    {
        var now = DateTimeOffset.UtcNow;
        var d = new RootChangeDebouncer(
            TimeSpan.FromSeconds(30),
            maximumBatchWindow: TimeSpan.FromMinutes(2));

        for (int seconds = 0; seconds < 120; seconds += 10)
        {
            d.Signal(@"C:\busy", now + TimeSpan.FromSeconds(seconds));
            Assert.Empty(d.TakeDue(now + TimeSpan.FromSeconds(seconds + 5)));
        }

        Assert.Equal(new[] { @"C:\busy" }, d.TakeDue(now + TimeSpan.FromMinutes(2)));
    }

    [Fact]
    public void Debouncer_PostponePending_RestartsQuietWindowWithoutDroppingSignal()
    {
        var now = DateTimeOffset.UtcNow;
        var d = new RootChangeDebouncer(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(30));
        d.Signal(@"C:\r", now);

        Assert.True(d.PostponePending(@"C:\r", now + TimeSpan.FromSeconds(2)));
        Assert.Empty(d.TakeDue(now + TimeSpan.FromSeconds(4)));
        Assert.Equal(new[] { @"C:\r" }, d.TakeDue(now + TimeSpan.FromSeconds(5)));
        Assert.False(d.PostponePending(@"C:\r", now + TimeSpan.FromSeconds(6)));
    }

    // ── Root watcher (real FSW) ──

    [Fact]
    public void RootWatcher_MissingDirectory_DegradesGracefully()
    {
        using var w = new ContentIndexRootWatcher(Path.Combine(_sandbox, "nope"));
        Assert.False(w.IsWatching); // no throw, just not watching
    }

    [Fact]
    public void RootWatcher_LiveDirectory_IsWatching()
    {
        using var w = new ContentIndexRootWatcher(_sandbox);
        Assert.True(w.IsWatching);
    }

    // ── End-to-end service (real FSW + timer) ──

    [Fact]
    public void Service_RejectsNullCallback()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ContentIndexWatcherHintService([], null!));
    }

    [Fact]
    public void Service_DisposedCallbacksNoOp_AndDisposeIsIdempotent()
    {
        var service = new ContentIndexWatcherHintService(
            [Path.Combine(_sandbox, "missing")],
            _ => { });
        service.Dispose();

        var onRootSignaled = typeof(ContentIndexWatcherHintService).GetMethod(
            "OnRootSignaled",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var drain = typeof(ContentIndexWatcherHintService).GetMethod(
            "Drain",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        onRootSignaled.Invoke(service, new object?[] { @"C:\r" });
        drain.Invoke(service, null);
        service.Dispose();
    }

    [Fact]
    public void Service_FileChange_FiresExactlyOneDebouncedCallback()
    {
        int callbacks = 0;
        var settled = new ManualResetEventSlim(false);
        using var service = new ContentIndexWatcherHintService(
            new[] { _sandbox },
            root => { Interlocked.Increment(ref callbacks); settled.Set(); },
            quietWindow: TimeSpan.FromMilliseconds(300),
            pollInterval: TimeSpan.FromMilliseconds(100));

        Assert.Equal(1, service.ActiveWatchCount);

        // A burst of writes should coalesce into a single settled callback.
        for (int i = 0; i < 5; i++)
        {
            File.WriteAllText(Path.Combine(_sandbox, $"f{i}.txt"), "x", new UTF8Encoding(false));
            Thread.Sleep(20);
        }

        Assert.True(settled.Wait(TimeSpan.FromSeconds(10)), "watcher-hint callback did not fire");
        Thread.Sleep(400); // allow any stray duplicate callbacks to land
        Assert.Equal(1, Volatile.Read(ref callbacks));
    }

    [Fact]
    public void Service_ChangeDuringInFlightRefresh_IsNotLost_AndWaitsForNewQuietWindow()
    {
        int callbacks = 0;
        var firstEntered = new ManualResetEventSlim(false);
        var releaseFirst = new ManualResetEventSlim(false);
        var secondEntered = new ManualResetEventSlim(false);
        using var service = new ContentIndexWatcherHintService(
            new[] { _sandbox },
            _ =>
            {
                int call = Interlocked.Increment(ref callbacks);
                if (call == 1)
                {
                    firstEntered.Set();
                    releaseFirst.Wait(TimeSpan.FromSeconds(10));
                }
                else if (call == 2)
                {
                    secondEntered.Set();
                }
            },
            quietWindow: TimeSpan.FromMilliseconds(300),
            maximumBatchWindow: TimeSpan.FromSeconds(2),
            pollInterval: TimeSpan.FromMilliseconds(50));

        File.WriteAllText(Path.Combine(_sandbox, "first.txt"), "one", new UTF8Encoding(false));
        Assert.True(firstEntered.Wait(TimeSpan.FromSeconds(10)), "first callback did not start");

        File.WriteAllText(Path.Combine(_sandbox, "during-refresh.txt"), "two", new UTF8Encoding(false));
        Thread.Sleep(500); // let the timer observe that the signal is due while callback #1 is in flight
        Assert.Equal(1, Volatile.Read(ref callbacks));
        releaseFirst.Set();

        Assert.False(secondEntered.Wait(TimeSpan.FromMilliseconds(150)), "follow-up ignored the post-refresh quiet window");
        Assert.True(secondEntered.Wait(TimeSpan.FromSeconds(10)), "signal received during refresh was lost");
        Assert.Equal(2, Volatile.Read(ref callbacks));
    }

    [Fact]
    public void Service_IndexStorageChanges_AreIgnored()
    {
        string storage = Path.Combine(_sandbox, "content-index");
        Directory.CreateDirectory(storage);
        int callbacks = 0;
        using var service = new ContentIndexWatcherHintService(
            new[] { _sandbox },
            _ => Interlocked.Increment(ref callbacks),
            excludedStorageRoot: storage,
            quietWindow: TimeSpan.FromMilliseconds(250),
            pollInterval: TimeSpan.FromMilliseconds(75));

        for (int i = 0; i < 5; i++)
        {
            File.WriteAllText(Path.Combine(storage, $"segment-{i}.bin"), "index data", new UTF8Encoding(false));
            Thread.Sleep(20);
        }

        Thread.Sleep(800);
        Assert.Equal(0, Volatile.Read(ref callbacks));
    }

    [Fact]
    public void Service_StorageSiblingPrefix_IsNotIgnored()
    {
        string storage = Path.Combine(_sandbox, "content-index");
        string sibling = Path.Combine(_sandbox, "content-index-backup");
        Directory.CreateDirectory(storage);
        Directory.CreateDirectory(sibling);
        var settled = new ManualResetEventSlim(false);
        using var service = new ContentIndexWatcherHintService(
            new[] { _sandbox },
            _ => settled.Set(),
            excludedStorageRoot: storage,
            quietWindow: TimeSpan.FromMilliseconds(250),
            pollInterval: TimeSpan.FromMilliseconds(75));

        File.WriteAllText(Path.Combine(sibling, "user-file.txt"), "x", new UTF8Encoding(false));

        Assert.True(settled.Wait(TimeSpan.FromSeconds(10)), "similarly-prefixed sibling was incorrectly excluded");
    }

    [Fact]
    public void Service_NoWatchableRoots_NeverThrows_AndDisposesClean()
    {
        using var service = new ContentIndexWatcherHintService(
            new[] { Path.Combine(_sandbox, "missing") },
            _ => { });
        Assert.Equal(0, service.ActiveWatchCount);
    }
}
