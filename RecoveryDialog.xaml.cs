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
    { if (RestoreAction is not null) RestoreAction.IsEnabled = SelectedEntries.Length > 0; }
    private void Restore_Click(object sender, RoutedEventArgs e) { if (SelectedEntries.Length > 0) DialogResult = true; }
}
