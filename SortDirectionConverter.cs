using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;

namespace LumaSearch;

public sealed class SortDirectionConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        ListSortDirection.Ascending => "▲",
        ListSortDirection.Descending => "▼",
        _ => ""
    };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
