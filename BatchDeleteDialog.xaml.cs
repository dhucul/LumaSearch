using System.Windows;

namespace LumaSearch;

public sealed record BatchTargetPreview(string Name, string Type, string FullPath, string Action, string Detail);

public partial class BatchDeleteDialog : Window
{
    public BatchDeleteDialog(SearchResult[] targets, int selectedCount, DeletionMode mode)
        : this(new BatchPreflight(targets, []), selectedCount, mode) { }

    public BatchDeleteDialog(BatchPreflight preflight, int selectedCount, DeletionMode mode)
    {
        InitializeComponent();
        bool recycle = mode == DeletionMode.RecycleBin;
        Heading.Text = recycle ? $"Recycle {preflight.Eligible.Length:N0} verified targets?" : $"Permanently delete {preflight.Eligible.Length:N0} verified targets?";
        Scope.Text = $"{selectedCount:N0} rows selected: {preflight.Eligible.Length:N0} verified targets, {preflight.Unavailable.Length:N0} skipped, {preflight.CoveredCount:N0} included with a selected parent.\n"
            + "Folders include all their contents; links affect only the link. Parent folders and other items are left in place.\n"
            + (recycle ? "Successfully recycled items can be restored until the Recycle Bin is emptied." : "Permanent deletion cannot be undone.");
        Targets.ItemsSource = preflight.Eligible.Select(item => new BatchTargetPreview(item.Name, item.Type, item.FullPath,
            recycle ? "Recycle" : "Delete permanently", item.IsLink ? "Link only; target stays." : item.IsDirectory ? "Includes all folder contents." : "Only this file."))
            .Concat(preflight.Unavailable.Select(item => new BatchTargetPreview(item.Item.Name, item.Item.Type, item.Item.FullPath,
                item.Status == DeletionStatus.AlreadyMissing ? "Skip: missing" : "Skip: unavailable", item.Message))).ToArray();
        ConfirmAction.Content = recycle ? "Send to Recycle Bin" : "Delete permanently";
        ConfirmAction.IsEnabled = preflight.Eligible.Length > 0;
        Loaded += (_, _) => CancelAction.Focus();
    }
    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
