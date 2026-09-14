using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace LumaSearch;

public sealed class ResultCollection : ObservableCollection<SearchResult>
{
    public void ReplaceAll(IEnumerable<SearchResult> results)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var result in results) Items.Add(result);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
