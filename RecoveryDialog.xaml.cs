using System.Windows;
using System.Windows.Controls;

namespace LumaSearch;

public partial class RecoveryDialog : Window
{
    public RecoveryDialog(RecoveryEntry[] entries)
    {
        InitializeComponent();
        Entries.ItemsSource = entries;
        Loaded += (_, _) => CancelAction.Focus();
    }
    public RecoveryEntry[] SelectedEntries => Entries.SelectedItems.Cast<RecoveryEntry>()
        .Where(item => item.Record.State != RecoveryState.Unavailable && item.Record.Original.Identity is not null).ToArray();
    private void SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RestoreAction is null) return;
        var selected = SelectedEntries;
        bool incomplete = selected.Any(item => item.MayBeIncomplete);
        RestoreAction.IsEnabled = selected.Length > 0;
        RestoreAction.Content = incomplete ? "Restore remaining contents" : "Restore selected items";
        IncompleteWarning.Visibility = incomplete ? Visibility.Visible : Visibility.Collapsed;
    }
    private void Restore_Click(object sender, RoutedEventArgs e) { if (SelectedEntries.Length > 0) DialogResult = true; }
}
