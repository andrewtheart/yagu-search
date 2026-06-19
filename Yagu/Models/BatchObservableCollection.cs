using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Yagu.Models;

/// <summary>
/// An <see cref="ObservableCollection{T}"/> that supports adding multiple items
/// with a single <see cref="NotifyCollectionChangedAction.Reset"/> notification
/// instead of one notification per item.
/// </summary>
public class BatchObservableCollection<T> : ObservableCollection<T>
{
    private bool _suppressNotification;
    private bool _suppressChangingNotification;

    /// <summary>
    /// Raised before the collection mutates, giving UI code a chance to capture
    /// scroll anchors before bound controls process the later change event.
    /// </summary>
    public event EventHandler? CollectionChanging;

    /// <summary>
    /// Add multiple items and raise a single <see cref="NotifyCollectionChangedAction.Reset"/>
    /// notification at the end, instead of one per item.
    /// </summary>
    public void AddRange(IReadOnlyList<T> items)
    {
        if (items.Count == 0) return;

        if (items.Count == 1)
        {
            Add(items[0]);
            return;
        }

        OnCollectionChanging();
        _suppressNotification = true;
        try
        {
            for (int i = 0; i < items.Count; i++)
                Items.Add(items[i]);
        }
        finally
        {
            _suppressNotification = false;
        }

        OnPropertyChanged(new PropertyChangedEventArgs("Count"));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        // WinUI ItemsControl/ListView do not support multi-item Add notifications
        // (only the first NewItem is processed). Use Reset so bound controls
        // re-read the full collection.
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(
            NotifyCollectionChangedAction.Reset));
    }

    /// <summary>
    /// Add multiple items and raise a single multi-item Add notification. Use only for
    /// collections consumed by managed observers that support multi-item Add events.
    /// </summary>
    public void AddRangeWithAddNotification(IReadOnlyList<T> items)
    {
        if (items.Count == 0) return;

        if (items.Count == 1)
        {
            Add(items[0]);
            return;
        }

        int startIndex = Count;
        OnCollectionChanging();
        _suppressNotification = true;
        try
        {
            for (int i = 0; i < items.Count; i++)
                Items.Add(items[i]);
        }
        finally
        {
            _suppressNotification = false;
        }

        OnPropertyChanged(new PropertyChangedEventArgs("Count"));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(
            NotifyCollectionChangedAction.Add,
            items is System.Collections.IList itemList ? itemList : items.ToList(),
            startIndex));
    }

    /// <summary>
    /// Append items with normal per-item Add notifications. Use this when an existing
    /// bound list must not briefly clear/rebuild in response to a Reset notification.
    /// </summary>
    public void AppendRange(IReadOnlyList<T> items)
    {
        if (items.Count == 0) return;

        OnCollectionChanging();
        _suppressChangingNotification = true;
        try
        {
            for (int i = 0; i < items.Count; i++)
                Add(items[i]);
        }
        finally
        {
            _suppressChangingNotification = false;
        }
    }

    /// <summary>
    /// Atomically clears the collection and replaces it with <paramref name="items"/>,
    /// raising a single <see cref="NotifyCollectionChangedAction.Reset"/> notification.
    /// This avoids the double-Reset (Clear then AddRange) that can crash the native
    /// WinUI3 ItemsRepeater layout engine when the second mutation arrives while it's
    /// still processing the first.
    /// </summary>
    public void ReplaceAll(IReadOnlyList<T> items)
    {
        OnCollectionChanging();
        _suppressNotification = true;
        try
        {
            Items.Clear();
            for (int i = 0; i < items.Count; i++)
                Items.Add(items[i]);
        }
        finally
        {
            _suppressNotification = false;
        }

        OnPropertyChanged(new PropertyChangedEventArgs("Count"));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(
            NotifyCollectionChangedAction.Reset));
    }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (!_suppressNotification)
            base.OnCollectionChanged(e);
    }

    protected override void InsertItem(int index, T item)
    {
        if (!_suppressNotification && !_suppressChangingNotification)
            OnCollectionChanging();
        base.InsertItem(index, item);
    }

    protected override void SetItem(int index, T item)
    {
        if (!_suppressNotification && !_suppressChangingNotification)
            OnCollectionChanging();
        base.SetItem(index, item);
    }

    protected override void RemoveItem(int index)
    {
        if (!_suppressNotification && !_suppressChangingNotification)
            OnCollectionChanging();
        base.RemoveItem(index);
    }

    protected override void ClearItems()
    {
        if (!_suppressNotification && !_suppressChangingNotification)
            OnCollectionChanging();
        base.ClearItems();
    }

    private void OnCollectionChanging() => CollectionChanging?.Invoke(this, EventArgs.Empty);
}
