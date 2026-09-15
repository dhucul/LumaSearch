using System.Windows.Controls;

namespace LumaSearch;

public sealed class ResultsDataGrid : DataGrid
{
    public void ReplaceSelection(IEnumerable<SearchResult> items)
    {
        // MultiSelector's transaction sends one selection notification instead of one per row.
        BeginUpdateSelectedItems();
        try
        {
            SelectedItems.Clear();
            foreach (var item in items) SelectedItems.Add(item);
        }
        finally { EndUpdateSelectedItems(); }
    }
}
