using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LumaSearch;

internal static class ScrollAlignmentTests
{
    internal static void Run(MainWindow window, Action refresh, string? previewPath)
    {
        var grid = (ResultsDataGrid)window.FindName("ResultsGrid");
        var rows = (ResultCollection)grid.ItemsSource;
        var original = rows.ToArray();
        var selected = grid.SelectedItems.Cast<SearchResult>().ToArray();
        var widths = grid.Columns.Select(column => column.Width).ToArray();
        var sort = ResultsSortState.Capture(grid);
        var viewer = Descendants<ScrollViewer>(grid).First();
        double oldVertical = viewer.VerticalOffset, oldHorizontal = viewer.HorizontalOffset;
        try
        {
            ResultsSortState.Suspend(grid);
            rows.ReplaceAll(Enumerable.Range(0, 120).Select(i => new SearchResult($"Item-{i:D3}",
                "C:\\Long scrolling fixture\\" + new string('x', 150) + $"\\Item-{i:D3}", i % 2 == 0,
                LastWriteTimeUtc: new DateTime(2020, 1, 1, 12, 0, 0, DateTimeKind.Utc).AddDays(i))));
            grid.Columns[2].Width = new DataGridLength(900);
            refresh();
            viewer.ScrollToHorizontalOffset(viewer.ScrollableWidth);
            refresh();
            if (viewer.HorizontalOffset <= 0) throw new InvalidOperationException("The horizontal scrolling fixture did not overflow.");
            foreach (double vertical in new[] { 0d, grid.RowHeight * 30 + 13, grid.RowHeight * 70 + 9 })
            {
                viewer.ScrollToVerticalOffset(vertical);
                refresh();
                AssertAligned(grid);
            }
            grid.Columns[3].Width = new DataGridLength(225);
            viewer.ScrollToHorizontalOffset(viewer.ScrollableWidth);
            refresh();
            AssertAligned(grid);
            viewer.ScrollToVerticalOffset(0);
            viewer.ScrollToHorizontalOffset(0);
            grid.Columns[2].Width = new DataGridLength(1, DataGridLengthUnitType.Star);
            refresh();
            AssertAligned(grid);
            if (Descendants<DataGridRow>(grid).Count() >= 120)
                throw new InvalidOperationException("Scrolling unexpectedly disabled row virtualization.");
            if (previewPath is not null)
            {
                var content = (FrameworkElement)window.Content;
                var bitmap = new RenderTargetBitmap(1120, 720, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(content);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(Path.ChangeExtension(previewPath, ".scroll.png"));
                encoder.Save(output);
            }
        }
        finally
        {
            rows.ReplaceAll(original);
            grid.ReplaceSelection(selected);
            for (int i = 0; i < widths.Length; i++) grid.Columns[i].Width = widths[i];
            sort.Restore(grid);
            refresh();
            viewer.ScrollToVerticalOffset(oldVertical);
            viewer.ScrollToHorizontalOffset(oldHorizontal);
            refresh();
        }
    }

    private static void AssertAligned(DataGrid grid)
    {
        var dateColumn = grid.Columns[3];
        var header = Descendants<DataGridColumnHeader>(grid).Single(item => item.Column == dateColumn);
        var headerText = Descendants<TextBlock>(header).First(item => item.Text == "Date Modified");
        double headerLeft = header.TranslatePoint(new Point(), grid).X;
        double headerTextLeft = headerText.TranslatePoint(new Point(headerText.Padding.Left, 0), grid).X;
        int checkedRows = 0;
        foreach (var row in Descendants<DataGridRow>(grid))
        {
            if (row.Item is not SearchResult item) continue;
            double top = row.TranslatePoint(new Point(), grid).Y;
            if (top + row.ActualHeight <= grid.ColumnHeaderHeight || top >= grid.ActualHeight - 16) continue;
            var date = dateColumn.GetCellContent(item) as TextBlock;
            var path = grid.Columns[2].GetCellContent(item) as TextBlock;
            if (date is null || path is null) throw new InvalidOperationException("A visible row is missing its date or path cell.");
            var cell = Ancestor<DataGridCell>(date)!;
            if (Math.Abs(cell.TranslatePoint(new Point(), grid).X - headerLeft) > 1 || Math.Abs(cell.ActualWidth - header.ActualWidth) > 1)
                throw new InvalidOperationException("Date cells moved out of their header column while scrolling.");
            if (Math.Abs(date.TranslatePoint(new Point(date.Padding.Left, 0), grid).X - headerTextLeft) > 1.5)
                throw new InvalidOperationException("Date text is horizontally offset from its header.");
            double dateCenter = date.TranslatePoint(new Point(0, date.ActualHeight / 2), grid).Y;
            double pathCenter = path.TranslatePoint(new Point(0, path.ActualHeight / 2), grid).Y;
            if (Math.Abs(dateCenter - pathCenter) > 1)
                throw new InvalidOperationException("Date text does not line up vertically with its row.");
            if (date.Text != item.DateModified!.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture))
                throw new InvalidOperationException("A recycled row is displaying another item's date.");
            checkedRows++;
        }
        if (checkedRows == 0) throw new InvalidOperationException("No visible rows were checked for scroll alignment.");
    }

    private static T? Ancestor<T>(DependencyObject item) where T : DependencyObject
    {
        for (DependencyObject? current = item; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is T found) return found;
        return null;
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) yield return found;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
