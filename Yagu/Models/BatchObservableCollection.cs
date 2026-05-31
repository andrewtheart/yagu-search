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
    /// Atomically clears the collection and replaces it with <paramref name="items"/>,
    /// raising a single <see cref="NotifyCollectionChangedAction.Reset"/> notification.
    /// This avoids the double-Reset (Clear then AddRange) that can crash the native
    /// WinUI3 ItemsRepeater layout engine when the second mutation arrives while it's
    /// still processing the first.
    /// </summary>
    public void ReplaceAll(IReadOnlyList<T> items)
    {
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
}
