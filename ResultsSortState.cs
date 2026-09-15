using System.ComponentModel;
using System.Windows.Controls;

namespace LumaSearch;

public sealed class ResultsSortState
{
    private readonly SortDescription[] _descriptions;
    private readonly (DataGridColumn Column, ListSortDirection? Direction)[] _indicators;
    private ResultsSortState(DataGrid grid)
    {
        _descriptions = grid.Items.SortDescriptions.ToArray();
        _indicators = grid.Columns.Select(column => (column, column.SortDirection)).ToArray();
    }
    public static ResultsSortState Capture(DataGrid grid) => new(grid);
    public static void Suspend(DataGrid grid)
    {
        using (grid.Items.DeferRefresh()) grid.Items.SortDescriptions.Clear();
        foreach (var column in grid.Columns) column.SortDirection = null;
    }
    public void Restore(DataGrid grid)
    {
        using (grid.Items.DeferRefresh())
        {
            grid.Items.SortDescriptions.Clear();
            foreach (var description in _descriptions) grid.Items.SortDescriptions.Add(description);
        }
        foreach (var (column, direction) in _indicators) column.SortDirection = direction;
    }
}
