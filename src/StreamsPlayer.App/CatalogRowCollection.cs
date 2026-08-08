using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace StreamsPlayer.App;

/// <summary>
/// An <see cref="ObservableCollection{T}"/> that can be re-filled in one notification (SP-0067).
/// </summary>
/// <remarks>
/// <c>Clear()</c> followed by an <c>Add</c> per channel raises one change notification per item - 19 856
/// of them on the owner's catalog - and a bound <c>ListView</c> re-runs its container bookkeeping for
/// every one. That is work priced by the size of the catalog on a path the user triggers by typing a
/// single character. <see cref="ReplaceAll"/> mutates the backing list directly and raises exactly one
/// <see cref="NotifyCollectionChangedAction.Reset"/>, which the list answers with one re-realization of
/// the rows actually on screen.
/// <para>
/// The cost of a Reset is that it drops selection and scroll position, where the per-item form hid that.
/// Both are restored deliberately: the scroll offset by <c>RestoreScrollAnchorAsync</c>, the selection by
/// the row objects themselves, which are reused across filter changes rather than rebuilt.
/// </para>
/// </remarks>
public sealed class CatalogRowCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IReadOnlyList<T> items)
    {
        // Nothing changed and nothing was there: skip the Reset entirely rather than making every
        // caller test for it. An empty-to-empty filter change is common while typing a query that
        // matches nothing.
        if (Count == 0 && items.Count == 0)
        {
            return;
        }

        CheckReentrancy();
        Items.Clear();
        for (var index = 0; index < items.Count; index++)
        {
            Items.Add(items[index]);
        }

        // The same three notifications ObservableCollection.ClearItems raises, in the same order, so a
        // bound control cannot tell this apart from a Clear - only from 20 000 Adds.
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
