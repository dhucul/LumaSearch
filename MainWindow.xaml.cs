using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace LumaSearch;

public enum OperationState { Idle, Searching, PreparingDelete, Deleting, OpeningExplorer, Cancelling, Closing }

public partial class MainWindow : Window
{
    private readonly ResultCollection _results = [];
    private CancellationTokenSource? _operationCancellation;
    private DeletionJob? _deletionJob;
    private OperationState _state;
    private long _operationId;
    private bool _closed;
    private bool _resultsExpanded;
    private WindowState _previousWindowState;
    private bool _restoreWindowState;
    private bool _changingLayoutWindowState;
    private bool _resultsContextMenuActive;
    private readonly DependencyPropertyDescriptor _windowStateDescriptor =
        DependencyPropertyDescriptor.FromProperty(WindowStateProperty, typeof(Window));

    public MainWindow()
    {
        InitializeComponent();
        ResultsGrid.ItemsSource = _results;
        DirectoryTextBox.Text = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        UpdateNameHint();
        UpdateControls();
        Loaded += Window_Loaded;
        _windowStateDescriptor.AddValueChanged(this, WindowStatePropertyChanged);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        long version = _operationId;
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        Task<string?> previous = ReadOnlyWork.RunAsync(DeletionJob.GetPreviousOutcomeAsync, cancel.Token);
        try
        {
            string? message = await previous.WaitAsync(cancel.Token);
            if (!_closed && _state == OperationState.Idle && version == _operationId && message is not null)
                StatusTextBlock.Text = message;
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
        finally { ReadOnlyWork.Observe(previous); }
    }

    private NameMatchMode SelectedNameMatchMode => NameMatchComboBox.SelectedValue is NameMatchMode mode ? mode : NameMatchMode.Contains;
    private DeletionMode SelectedDeletionMode => DeletionModeComboBox.SelectedValue is DeletionMode mode ? mode : DeletionMode.RecycleBin;
    private void NameMatchComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateNameHint();
    private void DeletionModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (IsInitialized) UpdateControls(); }
    private void UpdateNameHint()
    {
        if (NameHintTextBlock is null) return;
        NameHintTextBlock.Text = SelectedNameMatchMode switch
        {
            NameMatchMode.Exact => "Full name, including extension: report.txt. Ignores case. Leave blank for all names.",
            NameMatchMode.Wildcard => "Use * for any text or ? for one character: *.txt, report-??.*. Leave blank for all names.",
            _ => "Any part of the name: report finds Annual Report.pdf. Ignores case. Leave blank for all names."
        };
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (_state != OperationState.Idle) return;
        // Let the OS folder picker choose its initial location; do not probe a network path on the dispatcher.
        var dialog = new OpenFolderDialog { Title = "Choose a starting directory", Multiselect = false };
        if (dialog.ShowDialog(this) == true) DirectoryTextBox.Text = dialog.FolderName;
    }

    private (long Id, CancellationTokenSource Cancellation) BeginOperation(OperationState state)
    {
        _state = state;
        var cancellation = new CancellationTokenSource();
        _operationCancellation = cancellation;
        long id = ++_operationId;
        UpdateControls();
        return (id, cancellation);
    }

    private void EndOperation(long id)
    {
        if (_closed || id != _operationId) return;
        _operationCancellation = null;
        _state = OperationState.Idle;
        CountTextBlock.Text = $"{_results.Count:N0} results";
        UpdateControls();
    }

    private async void Search_Click(object sender, RoutedEventArgs e)
    {
        if (_state != OperationState.Idle) return;
        SearchOptions options;
        try
        {
            string input = Environment.ExpandEnvironmentVariables(DirectoryTextBox.Text.Trim().Trim('"'));
            if (string.IsNullOrWhiteSpace(input) || !Path.IsPathFullyQualified(input)) throw new ArgumentException("Enter a full starting directory path.");
            bool files = SearchFilesCheckBox.IsChecked == true, folders = SearchFoldersCheckBox.IsChecked == true;
            if (!files && !folders) throw new ArgumentException("Select Search Files, Search Folders, or both.");
            if (TargetTextBox.Text.Contains('\r') || TargetTextBox.Text.Contains('\n')) throw new ArgumentException("Search text must fit on a single line.");
            options = new(Path.GetFullPath(input), PatternTextBox.Text, TargetTextBox.Text,
                files, folders, IncludeHiddenCheckBox.IsChecked == true, SelectedNameMatchMode);
        }
        catch (Exception ex) when (ex is ArgumentException || FileSearchService.IsFileSystemException(ex))
        { MessageBox.Show(this, ex.Message, "Cannot start search", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

        var (id, cancellation) = BeginOperation(OperationState.Searching);
        _results.Clear();
        ResultsGrid.Items.SortDescriptions.Clear();
        var statistics = new ScanStatistics();
        var stopwatch = Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) => SetProgress(statistics, stopwatch.Elapsed, "Searching");
        var channel = Channel.CreateBounded<SearchResult>(new BoundedChannelOptions(512)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        Task worker = ReadOnlyWork.RunAsync(async () =>
        {
            try { await FileSearchService.ScanAsync(options, channel.Writer, statistics, cancellation.Token); }
            finally { channel.Writer.TryComplete(); }
            return true;
        }, cancellation.Token);
        timer.Start();
        string outcome = "Complete";
        try
        {
            while (await channel.Reader.WaitToReadAsync(cancellation.Token))
            {
                if (_closed || id != _operationId) break;
                for (int count = 0; count < 100 && channel.Reader.TryRead(out SearchResult? result); count++) _results.Add(result);
                await Dispatcher.Yield(DispatcherPriority.Background);
            }
            await worker.WaitAsync(cancellation.Token);
            if (statistics.LimitReached) outcome = $"Stopped at {options.MaxResults:N0} results — incomplete; narrow the search";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { outcome = "Cancelled — partial results"; }
        catch (Exception ex)
        {
            outcome = "Search stopped";
            if (!_closed) MessageBox.Show(this, ex.Message, "Search error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            timer.Stop();
            cancellation.Cancel();
            // Never wait on an uninterruptible read to release the UI. Its owner disposes the token when it exits.
            ReadOnlyWork.Observe(worker, cancellation);
            EndOperation(id);
            if (!_closed && id == _operationId) SetProgress(statistics, stopwatch.Elapsed, outcome);
        }
    }

    public static string ProgressPhase(OperationState state, string fallback) => state switch
    { OperationState.Cancelling => "Cancelling", OperationState.Closing => "Closing", _ => fallback };
    private void SetProgress(ScanStatistics stats, TimeSpan elapsed, string phase)
    {
        if (_closed) return;
        CountTextBlock.Text = $"{_results.Count:N0} results";
        StatusTextBlock.Text = $"{ProgressPhase(_state, phase)} · {stats.Visited:N0} items examined · {stats.Skipped:N0} inaccessible or unreadable items skipped · {elapsed.TotalSeconds:N1}s";
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_state is OperationState.Idle or OperationState.Closing) return;
        _state = OperationState.Cancelling;
        if (_deletionJob is not null)
        {
            _deletionJob.RequestCancel();
            StatusTextBlock.Text = "Stopping deletion after the current filesystem operation. You may close this window; its outcome will be saved.";
        }
        else { _operationCancellation?.Cancel(); StatusTextBlock.Text = "Cancelling…"; }
        UpdateControls();
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_state != OperationState.Idle || GetActionTarget(sender) is not SearchResult item || item.Identity is null) return;
        DeletionMode mode = SelectedDeletionMode;
        var (id, cancel) = BeginOperation(OperationState.PreparingDelete);
        Task<SearchResult?> validation = ReadOnlyWork.RunAsync(() => FileDeletionService.ValidateTarget(item), cancel.Token);
        DeletionOutcome? outcome = null;
        try
        {
            var validated = await validation.WaitAsync(TimeSpan.FromSeconds(10), cancel.Token);
            if (_closed) return;
            if (validated is null) outcome = FileDeletionService.Missing();
            else
            {
                if (MessageBox.Show(this, FileDeletionService.GetConfirmationMessage(validated, mode),
                    mode == DeletionMode.RecycleBin ? "Send selected item to Recycle Bin?" : "Permanently delete selected item?",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
                _state = OperationState.Deleting;
                UpdateControls();
                StatusTextBlock.Text = mode == DeletionMode.RecycleBin ? "Sending selected item to the Recycle Bin…" : "Permanently deleting selected item…";
                _deletionJob = await DeletionJob.StartAsync(item, mode, cancel.Token);
                if (_closed) { _deletionJob.RequestCancel(); return; }
                outcome = await _deletionJob.WaitAsync();
            }
            if (_closed) return;
            bool removed = outcome.Status is DeletionStatus.Recycled or DeletionStatus.PermanentlyDeleted or DeletionStatus.AlreadyMissing;
            bool reconciled = await ReconcileAsync(item, removed, id, cancel.Token);
            if (_closed) return;
            StatusTextBlock.Text = outcome.Message + (reconciled ? "" : " Refresh the search to confirm which items remain.");
            if (outcome.Status is DeletionStatus.Failed or DeletionStatus.Unknown)
                MessageBox.Show(this, outcome.Message, "Operation did not complete", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (OperationCanceledException) { if (!_closed) StatusTextBlock.Text = "Operation cancelled before deletion started."; }
        catch (Exception ex) { if (!_closed) { StatusTextBlock.Text = "Operation did not complete."; MessageBox.Show(this, ex.Message, "Deletion unavailable", MessageBoxButton.OK, MessageBoxImage.Warning); } }
        finally
        {
            ReadOnlyWork.Observe(validation, cancel);
            _deletionJob?.Dispose();
            _deletionJob = null;
            EndOperation(id);
        }
    }

    private async Task<bool> ReconcileAsync(SearchResult item, bool removed, long id, CancellationToken token)
    {
        SearchResult[] snapshot = _results.ToArray();
        SearchResult? selected = ResultsGrid.SelectedItem as SearchResult;
        var work = ReadOnlyWork.RunAsync(() => FileDeletionService.Reconcile(snapshot, item, removed), token);
        try
        {
            var survivors = await work.WaitAsync(TimeSpan.FromSeconds(3), token);
            if (_closed || id != _operationId) return false;
            var scroll = FindScrollViewer(ResultsGrid);
            double offset = scroll?.VerticalOffset ?? 0;
            _results.ReplaceAll(survivors);
            if (selected is not null && survivors.Contains(selected)) ResultsGrid.SelectedItem = selected;
            await Dispatcher.Yield(DispatcherPriority.Background);
            if (!_closed && id == _operationId) scroll?.ScrollToVerticalOffset(offset);
            return true;
        }
        catch (TimeoutException) { return false; }
        finally { ReadOnlyWork.Observe(work); }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        if (parent is ScrollViewer viewer) return viewer;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(parent, i)) is { } found) return found;
        return null;
    }

    private async void Explorer_Click(object sender, RoutedEventArgs e)
    {
        if (_state != OperationState.Idle || GetActionTarget(sender) is not SearchResult item) return;
        var (id, cancel) = BeginOperation(OperationState.OpeningExplorer);
        Task work = ReadOnlyWork.RunAsync(async () => { await ExplorerService.ShowAsync(item, cancel.Token); return true; }, cancel.Token);
        StatusTextBlock.Text = "Opening Explorer for: " + item.Name;
        try
        {
            await work.WaitAsync(TimeSpan.FromSeconds(10), cancel.Token);
            if (!_closed) StatusTextBlock.Text = "Shown in Explorer: " + item.Name;
        }
        catch (OperationCanceledException) { if (!_closed) StatusTextBlock.Text = "Explorer navigation cancelled."; }
        catch (Exception ex) { if (!_closed) { StatusTextBlock.Text = "Explorer navigation did not complete."; MessageBox.Show(this, ex.Message, "Cannot open Explorer", MessageBoxButton.OK, MessageBoxImage.Warning); } }
        finally { cancel.Cancel(); ReadOnlyWork.Observe(work, cancel); EndOperation(id); }
    }

    private void UpdateControls()
    {
        bool busy = _state != OperationState.Idle;
        SearchOptionsPanel.IsEnabled = !busy;
        DeletionModeComboBox.IsEnabled = !busy;
        SearchButton.IsEnabled = !busy;
        CancelButton.IsEnabled = busy && _state is not OperationState.Cancelling and not OperationState.Closing;
        bool selected = ResultsGrid.SelectedItem is SearchResult;
        ExplorerButton.IsEnabled = !busy && selected;
        ExplorerMenuItem.IsEnabled = ExplorerButton.IsEnabled &&
            (!_resultsContextMenuActive || ReferenceEquals(ExplorerMenuItem.Tag, ResultsGrid.SelectedItem));
        DeleteButton.IsEnabled = !busy && ResultsGrid.SelectedItem is SearchResult { Identity: not null };
        DeleteMenuItem.IsEnabled = DeleteButton.IsEnabled &&
            (!_resultsContextMenuActive || ReferenceEquals(DeleteMenuItem.Tag, ResultsGrid.SelectedItem));
        bool recycle = SelectedDeletionMode == DeletionMode.RecycleBin;
        string label = recycle ? "Send to Recycle Bin…" : "Delete Permanently…";
        if (ResultsGrid.SelectedItem is SearchResult item)
        {
            label = recycle ? $"Recycle {item.Type}…" : $"Delete {item.Type} Permanently…";
            string action = recycle ? "sent to the Recycle Bin" : "permanently deleted";
            SelectionTextBlock.Text = item.Identity is null ? $"Selected: {item.Name}. Deletion is disabled because its identity could not be verified."
                : item.IsLink ? $"Selected link: {item.Name} — only the link will be {action}. Its target stays."
                : item.IsDirectory ? $"Selected folder: {item.Name} — this folder and its contents will be {action}. Its parent folder stays."
                : $"Selected file: {item.Name} — only this file will be {action}. Its containing folder stays.";
        }
        else SelectionTextBlock.Text = "Select a result to recycle or permanently delete it. Searches stop at 20,000 results.";
        DeleteButton.Content = label;
        DeleteMenuItem.Header = label;
        SearchProgress.IsIndeterminate = busy;
        ResultsGrid.CanUserSortColumns = !busy;
    }

    private void ResultsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateControls();
    private void ToggleResultsLayout_Click(object sender, RoutedEventArgs e)
    {
        if (_closed) return;
        _resultsExpanded = !_resultsExpanded;
        if (_resultsExpanded)
        {
            _previousWindowState = WindowState;
            _restoreWindowState = WindowState != WindowState.Maximized;
            HeaderPanel.Visibility = Visibility.Collapsed;
            SearchOptionsCard.Visibility = Visibility.Collapsed;
            RestoreLayoutButton.Visibility = Visibility.Visible;
            ExpandResultsMenuItem.Header = "Restore normal layout";
            if (_restoreWindowState) SetLayoutWindowState(WindowState.Maximized);
        }
        else
        {
            HeaderPanel.Visibility = Visibility.Visible;
            SearchOptionsCard.Visibility = Visibility.Visible;
            RestoreLayoutButton.Visibility = Visibility.Collapsed;
            ExpandResultsMenuItem.Header = "Expand results area";
            if (_restoreWindowState) SetLayoutWindowState(_previousWindowState);
            _restoreWindowState = false;
        }
        // Reuse the same grid and bound collection, preserving results, selection and search inputs.
    }
    private void SetLayoutWindowState(WindowState state)
    {
        _changingLayoutWindowState = true;
        try { WindowState = state; }
        finally { _changingLayoutWindowState = false; }
    }
    private void WindowStatePropertyChanged(object? sender, EventArgs e)
    {
        if (_resultsExpanded && !_changingLayoutWindowState) _restoreWindowState = false;
    }
    private SearchResult? GetActionTarget(object sender)
    {
        if (sender is MenuItem menu)
            return menu.Tag is SearchResult target && ReferenceEquals(target, ResultsGrid.SelectedItem) ? target : null;
        return ResultsGrid.SelectedItem as SearchResult;
    }
    private void ResultsGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        SearchResult? target = e.CursorLeft < 0 && e.CursorTop < 0
            ? ResultsGrid.SelectedItem as SearchResult
            : e.OriginalSource is DependencyObject source &&
                ItemsControl.ContainerFromElement(ResultsGrid, source) is DataGridRow row ? row.Item as SearchResult : null;
        ExplorerMenuItem.Tag = target;
        DeleteMenuItem.Tag = target;
        _resultsContextMenuActive = true;
        UpdateControls();
    }
    private void ResultsContextMenu_Opened(object sender, RoutedEventArgs e) => UpdateControls();
    private void ResultsContextMenu_Closed(object sender, RoutedEventArgs e)
    {
        _resultsContextMenuActive = false;
        // Keep the command target until the next opening: WPF may close a menu before delivering Click.
        UpdateControls();
    }
    private void ResultsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.OriginalSource is not DependencyObject source ||
            ItemsControl.ContainerFromElement(ResultsGrid, source) is not DataGridRow row) return;
        ResultsGrid.SelectedItem = row.Item; e.Handled = true; Explorer_Click(sender, e);
    }
    private void ResultsGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(ResultsGrid, source) is DataGridRow row)
        { row.IsSelected = true; row.Focus(); }
        // Empty-space layout commands must preserve selection. Their item actions are disabled on opening.
    }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _closed = true;
        _windowStateDescriptor.RemoveValueChanged(this, WindowStatePropertyChanged);
        _state = OperationState.Closing;
        ++_operationId;
        _operationCancellation?.Cancel();
        _deletionJob?.RequestCancel();
        // Read-only workers are bounded background tasks. A deletion helper survives long enough
        // to finish its current atomic OS step, honor cancellation, and record its final result.
    }
}
