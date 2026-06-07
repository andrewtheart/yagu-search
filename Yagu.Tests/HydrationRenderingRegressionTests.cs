using System.Collections.Specialized;
using System.ComponentModel;
using Yagu.Models;
using Yagu.Services;

namespace Yagu.Tests;

/// <summary>
/// Regression tests for the drawer-rendering and hydration-crash fixes:
/// 1. <see cref="BatchObservableCollection{T}.AddRange"/> must raise a single
///    <see cref="NotifyCollectionChangedAction.Reset"/> notification. WinUI
///    ItemsControl/ListView only honor the first NewItem in a multi-item
///    Add notification, which caused only one of many newly added match rows
///    to be rendered when a drawer was expanded.
/// 2. <see cref="SearchResult.HydrateFrom(string, IReadOnlyList{string}, IReadOnlyList{string})"/>
///    must restore payload AND raise PropertyChanged for the context-line
///    collections. Because PropertyChanged drives XAML bindings, hydration
///    must run on the UI thread; the disk-read phase is split out separately
///    so it can run on a worker.
/// </summary>
public class HydrationRenderingRegressionTests
{
    // -----------------------------------------------------------------
    // BatchObservableCollection.AddRange — must use Reset, not Add.
    // -----------------------------------------------------------------

    [Fact]
    public void BatchAddRange_MultipleItems_RaisesSingleResetNotification()
    {
        var collection = new BatchObservableCollection<int>();
        var notifications = new List<NotifyCollectionChangedEventArgs>();
        collection.CollectionChanged += (_, e) => notifications.Add(e);

        collection.AddRange(new[] { 1, 2, 3, 4, 5 });

        Assert.Single(notifications);
        Assert.Equal(NotifyCollectionChangedAction.Reset, notifications[0].Action);
        // Reset notifications must not carry NewItems — WinUI ignores them
        // when the action is Reset, which is what forces a full re-read.
        Assert.Null(notifications[0].NewItems);
    }

    [Fact]
    public void BatchAddRange_MultipleItems_AllItemsAreAddedToCollection()
    {
        var collection = new BatchObservableCollection<int>();
        collection.AddRange(new[] { 10, 20, 30, 40, 50 });

        Assert.Equal(5, collection.Count);
        Assert.Equal(new[] { 10, 20, 30, 40, 50 }, collection);
    }

    [Fact]
    public void BatchAddRange_MultipleItems_RaisesCountAndIndexerPropertyChanged()
    {
        var collection = new BatchObservableCollection<int>();
        var propertyNames = new List<string?>();
        ((INotifyPropertyChanged)collection).PropertyChanged += (_, e) => propertyNames.Add(e.PropertyName);

        collection.AddRange(new[] { 1, 2, 3 });

        Assert.Contains("Count", propertyNames);
        Assert.Contains("Item[]", propertyNames);
    }

    [Fact]
    public void BatchAddRange_EmptyList_RaisesNoNotifications()
    {
        var collection = new BatchObservableCollection<int>();
        int notificationCount = 0;
        collection.CollectionChanged += (_, _) => notificationCount++;

        collection.AddRange(Array.Empty<int>());

        Assert.Equal(0, notificationCount);
        Assert.Empty(collection);
    }

    [Fact]
    public void BatchAddRange_SingleItem_RaisesAddNotification()
    {
        // For a single item we fall through to base.Add which raises a
        // standard Add notification — WinUI handles single-item Add correctly.
        var collection = new BatchObservableCollection<int>();
        var notifications = new List<NotifyCollectionChangedEventArgs>();
        collection.CollectionChanged += (_, e) => notifications.Add(e);

        collection.AddRange(new[] { 99 });

        Assert.Single(notifications);
        Assert.Equal(NotifyCollectionChangedAction.Add, notifications[0].Action);
        Assert.Equal(99, Assert.Single(notifications[0].NewItems!.Cast<int>()));
        Assert.Equal(new[] { 99 }, collection);
    }

    [Fact]
    public void BatchAddRange_AfterExistingItems_AppendsAndRaisesReset()
    {
        var collection = new BatchObservableCollection<int> { 1, 2 };
        var notifications = new List<NotifyCollectionChangedEventArgs>();
        collection.CollectionChanged += (_, e) => notifications.Add(e);

        collection.AddRange(new[] { 3, 4, 5 });

        Assert.Single(notifications);
        Assert.Equal(NotifyCollectionChangedAction.Reset, notifications[0].Action);
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, collection);
    }

    [Fact]
    public void BatchAppendRange_AppendsWithPerItemAddNotifications()
    {
        var collection = new BatchObservableCollection<int> { 1, 2 };
        var notifications = new List<NotifyCollectionChangedEventArgs>();
        collection.CollectionChanged += (_, e) => notifications.Add(e);

        collection.AppendRange(new[] { 3, 4, 5 });

        Assert.Equal(3, notifications.Count);
        Assert.All(notifications, e => Assert.Equal(NotifyCollectionChangedAction.Add, e.Action));
        Assert.Equal(new[] { 3, 4, 5 }, notifications.Select(e => Assert.Single(e.NewItems!.Cast<int>())).ToArray());
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, collection);
    }

    [Fact]
    public void FileGroupShowMore_AppendsVisibleResultsWithoutResettingExistingRows()
    {
        var group = new FileGroup(@"C:\temp\alpha.txt");
        for (int i = 1; i <= 5; i++)
            group.Add(CreateResult(@"C:\temp\alpha.txt", i));

        group.ShowMore(2);
        var notifications = new List<NotifyCollectionChangedEventArgs>();
        group.VisibleResults.CollectionChanged += (_, e) => notifications.Add(e);

        int shown = group.ShowMore(2);

        Assert.Equal(2, shown);
        Assert.Equal(2, notifications.Count);
        Assert.All(notifications, e => Assert.Equal(NotifyCollectionChangedAction.Add, e.Action));
        Assert.DoesNotContain(notifications, e => e.Action == NotifyCollectionChangedAction.Reset);
        Assert.Equal(new[] { 1, 2, 3, 4 }, group.VisibleResults.Select(r => r.LineNumber).ToArray());
    }

    private static SearchResult CreateResult(string filePath, int lineNumber) =>
        new(filePath, lineNumber, $"line {lineNumber} test", 5, 4, Array.Empty<string>(), Array.Empty<string>());

    [Fact]
    public void BatchCollectionChanging_FiresBeforeResetMutationIsVisible()
    {
        var collection = new BatchObservableCollection<int> { 1, 2 };
        var observations = new List<string>();
        collection.CollectionChanging += (_, _) => observations.Add($"changing:{string.Join(",", collection)}");
        collection.CollectionChanged += (_, e) => observations.Add($"changed:{e.Action}:{string.Join(",", collection)}");

        collection.ReplaceAll(new[] { 3, 4 });

        Assert.Equal(new[]
        {
            "changing:1,2",
            "changed:Reset:3,4",
        }, observations);
    }

    [Fact]
    public void BatchCollectionChanging_EmptyAddRangeRaisesNoNotification()
    {
        var collection = new BatchObservableCollection<int>();
        int changingCount = 0;
        collection.CollectionChanging += (_, _) => changingCount++;

        collection.AddRange(Array.Empty<int>());

        Assert.Equal(0, changingCount);
    }

    // -----------------------------------------------------------------
    // SearchResult.HydrateFrom — restores payload and notifies UI bindings.
    // -----------------------------------------------------------------

    [Fact]
    public void HydrateFrom_RestoresPayloadAndClearsEvictedState()
    {
        var stub = SearchResult.CreatePreEvicted(
            filePath: @"C:\file.txt",
            lineNumber: 7,
            matchStartColumn: 4,
            matchLength: 6,
            diskOffset: 1234);

        Assert.True(stub.IsEvicted);
        Assert.Equal(string.Empty, stub.MatchLine);

        stub.HydrateFrom("the needle line", new[] { "ctx-before" }, new[] { "ctx-after" });

        Assert.False(stub.IsEvicted);
        Assert.Equal("the needle line", stub.MatchLine);
        Assert.Equal(new[] { "ctx-before" }, stub.ContextBefore);
        Assert.Equal(new[] { "ctx-after" }, stub.ContextAfter);
    }

    [Fact]
    public void HydrateFrom_RaisesPropertyChangedForContextBindings()
    {
        var stub = SearchResult.CreatePreEvicted(@"C:\f.txt", 1, 0, 1, diskOffset: 0);
        var raised = new List<string?>();
        ((INotifyPropertyChanged)stub).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        stub.HydrateFrom("x", Array.Empty<string>(), Array.Empty<string>());

        // These notifications drive the NumberedBefore/NumberedAfter XAML bindings;
        // they must fire so the context rows refresh after disk hydration.
        Assert.Contains(nameof(SearchResult.NumberedBefore), raised);
        Assert.Contains(nameof(SearchResult.NumberedAfter), raised);
    }

    [Fact]
    public void HydrateFrom_WhenNotEvicted_IsNoOpAndDoesNotNotify()
    {
        var result = new SearchResult(
            FilePath: @"C:\f.txt", LineNumber: 1, MatchLine: "original",
            MatchStartColumn: 0, MatchLength: 8,
            ContextBefore: Array.Empty<string>(), ContextAfter: Array.Empty<string>());

        int raised = 0;
        ((INotifyPropertyChanged)result).PropertyChanged += (_, _) => raised++;

        result.HydrateFrom("replacement", new[] { "before" }, new[] { "after" });

        Assert.Equal("original", result.MatchLine);
        Assert.Empty(result.ContextBefore);
        Assert.Empty(result.ContextAfter);
        Assert.Equal(0, raised);
    }

    // -----------------------------------------------------------------
    // End-to-end: ResultStore disk round-trip via HydrateFrom matches
    // what the ViewModel.ReadHydrationPayloads / ApplyHydrationPayloads
    // split pipeline produces.
    // -----------------------------------------------------------------

    [Fact]
    public void DiskRoundTripViaHydrateFrom_RestoresOriginalPayload()
    {
        using var store = new ResultStore();
        var result = new SearchResult(
            FilePath: @"C:\f.txt", LineNumber: 42,
            MatchLine: "needle in a giant haystack",
            MatchStartColumn: 0, MatchLength: 6,
            ContextBefore: new[] { "before-1", "before-2" },
            ContextAfter: new[] { "after-1" });

        store.WriteBatch(writeOne => result.EvictWith(writeOne));
        Assert.True(result.IsEvicted);
        long offset = result.DiskOffset;

        // Simulate the worker-thread phase: read payload from disk only.
        var payloads = store.ReadBatch(new[] { offset });
        Assert.NotNull(payloads[0]);
        var (ml, cb, ca) = payloads[0]!.Value;

        // Simulate the UI-thread apply phase.
        result.HydrateFrom(ml, cb, ca);

        Assert.False(result.IsEvicted);
        Assert.Equal("needle in a giant haystack", result.MatchLine);
        Assert.Equal(new[] { "before-1", "before-2" }, result.ContextBefore);
        Assert.Equal(new[] { "after-1" }, result.ContextAfter);
    }
}
