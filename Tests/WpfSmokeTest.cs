using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LumaSearch;

internal static class WpfSmokeTest
{
    internal static void Run(string root, string? previewPath)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var app = new App();
        app.InitializeComponent();
        var window = new MainWindow();
        var path = (TextBox)window.FindName("DirectoryTextBox");
        var pattern = (TextBox)window.FindName("PatternTextBox");
        var search = (Button)window.FindName("SearchButton");
        var grid = (DataGrid)window.FindName("ResultsGrid");
        var delete = (Button)window.FindName("DeleteButton");
        var explorer = (Button)window.FindName("ExplorerButton");
        var explorerMenu = (MenuItem)window.FindName("ExplorerMenuItem");
        var matchMode = (ComboBox)window.FindName("NameMatchComboBox");
        var selection = (TextBlock)window.FindName("SelectionTextBlock");
        var deletionMode = (ComboBox)window.FindName("DeletionModeComboBox");
        if (!Equals(deletionMode.SelectedValue, DeletionMode.RecycleBin))
            throw new InvalidOperationException("Deletion did not default to the Recycle Bin.");
        path.Text = root;
        pattern.Text = "*.txt";
        matchMode.SelectedValue = NameMatchMode.Wildcard;
        RunSearch();
        matchMode.SelectedValue = NameMatchMode.Exact;
        pattern.Text = "unicode.txt";
        RunSearch();
        if (grid.Items.Count != 1 || ((SearchResult)grid.Items[0]).Name != "unicode.txt")
            throw new InvalidOperationException("The exact-name UI search returned incorrect results.");
        matchMode.SelectedValue = NameMatchMode.Contains;
        pattern.Text = "unicode";
        RunSearch();
        if (grid.Items.Count != 2) throw new InvalidOperationException("The contains-name UI search returned incorrect results.");
        grid.SelectedIndex = 0;
        if (!explorer.IsEnabled || !explorerMenu.IsEnabled)
            throw new InvalidOperationException("Selection did not enable Explorer navigation.");
        if (!delete.IsEnabled || !delete.Content.ToString()!.Contains("File") || !selection.Text.Contains("only this file"))
            throw new InvalidOperationException("Selection did not clarify file deletion.");
        if (!delete.Content.ToString()!.Contains("Recycle") || !selection.Text.Contains("Recycle Bin"))
            throw new InvalidOperationException("Recycle selection was not reflected in the controls.");
        deletionMode.SelectedValue = DeletionMode.Permanent;
        if (!delete.Content.ToString()!.Contains("Permanently") || !selection.Text.Contains("permanently deleted"))
            throw new InvalidOperationException("Permanent deletion selection was not reflected in the controls.");
        deletionMode.SelectedValue = DeletionMode.RecycleBin;
        grid.SelectedIndex = -1;
        if (explorer.IsEnabled || explorerMenu.IsEnabled)
            throw new InvalidOperationException("Explorer navigation stayed enabled without a selection.");
        if (delete.IsEnabled) throw new InvalidOperationException("Clearing selection did not disable deletion.");

        void RunSearch(bool cancelImmediately = false, Action? afterStart = null)
        {
        search.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        afterStart?.Invoke();
        if (cancelImmediately) ((Button)window.FindName("CancelButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var frame = new DispatcherFrame();
        var deadline = DateTime.UtcNow.AddSeconds(20);
        bool timedOut = false;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
        timer.Tick += (_, _) =>
        {
            if (search.IsEnabled) frame.Continue = false;
            else if (DateTime.UtcNow > deadline) { timedOut = true; frame.Continue = false; }
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
        if (timedOut || (!cancelImmediately && grid.Items.Count == 0)) throw new InvalidOperationException("The UI search did not produce results.");
        }

        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(1120, 720));
        content.Arrange(new Rect(0, 0, 1120, 720));
        content.UpdateLayout();
        var layoutFrame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            new Action(() => layoutFrame.Continue = false));
        Dispatcher.PushFrame(layoutFrame);
        content.UpdateLayout();
        if (grid.Columns[0].ActualWidth < 239 || grid.Columns[1].ActualWidth < 89)
            throw new InvalidOperationException("Result columns did not reach their configured widths.");
        void RefreshLayout()
        {
            content.Measure(new Size(1120, 720));
            content.Arrange(new Rect(0, 0, 1120, 720));
            content.UpdateLayout();
            var pendingLayout = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => pendingLayout.Continue = false));
            Dispatcher.PushFrame(pendingLayout);
            content.UpdateLayout();
        }
        IEnumerable<T> VisualChildren<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T match) yield return match;
                foreach (var nested in VisualChildren<T>(child)) yield return nested;
            }
        }
        var splitter = (GridSplitter)window.FindName("ResultsSplitter");
        var topRow = (RowDefinition)window.FindName("SearchControlsRow");
        double heightBeforeDrag = grid.ActualHeight;
        double topBeforeDrag = topRow.ActualHeight;
        object sourceBeforeDrag = grid.ItemsSource;
        splitter.RaiseEvent(new DragStartedEventArgs(0, 0));
        splitter.RaiseEvent(new DragDeltaEventArgs(0, -60));
        splitter.RaiseEvent(new DragCompletedEventArgs(0, -60, false));
        RefreshLayout();
        if (grid.ActualHeight < heightBeforeDrag + 40 || topRow.ActualHeight > topBeforeDrag - 40 || grid.ItemsSource != sourceBeforeDrag)
            throw new InvalidOperationException("Dragging the divider did not enlarge the results area.");
        var nameHeader = VisualChildren<DataGridColumnHeader>(grid).First(header => header.Column == grid.Columns[0]);
        nameHeader.ApplyTemplate();
        var gripper = (Thumb)nameHeader.Template.FindName("PART_RightHeaderGripper", nameHeader);
        double widthBeforeDrag = grid.Columns[0].ActualWidth;
        gripper.RaiseEvent(new DragStartedEventArgs(0, 0));
        gripper.RaiseEvent(new DragDeltaEventArgs(80, 0));
        gripper.RaiseEvent(new DragCompletedEventArgs(80, 0, false));
        RefreshLayout();
        if (grid.Columns[0].ActualWidth < widthBeforeDrag + 60)
            throw new InvalidOperationException("Dragging the column edge did not widen the column.");
        var resetSizes = (MenuItem)window.FindName("ResetResultsSizesMenuItem");
        resetSizes.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        RefreshLayout();
        if (Math.Abs(grid.Columns[0].ActualWidth - 240) > 1 || topRow.Height.Value != 190)
            throw new InvalidOperationException("Reset sizes did not restore the panel and column dimensions.");
        var expand = (MenuItem)window.FindName("ExpandResultsMenuItem");
        var restore = (Button)window.FindName("RestoreLayoutButton");
        var optionsCard = (Border)window.FindName("SearchOptionsCard");
        double normalHeight = grid.ActualHeight;
        object originalSource = grid.ItemsSource;
        grid.SelectedIndex = 0;
        object originalSelection = grid.SelectedItem;
        var deleteMenu = (MenuItem)window.FindName("DeleteMenuItem");
        void OpenResultsMenu(UIElement source, bool keyboard = false)
        {
            if (!keyboard)
                source.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right)
                    { RoutedEvent = Mouse.PreviewMouseDownEvent });
            // WPF exposes no public constructor; create its normal routed opening event for the real input handlers.
            var opening = (ContextMenuEventArgs)Activator.CreateInstance(typeof(ContextMenuEventArgs),
                BindingFlags.Instance | BindingFlags.NonPublic, null,
                new object[] { source, true, keyboard ? -1d : 0d, keyboard ? -1d : 0d }, null)!;
            source.RaiseEvent(opening);
            grid.ContextMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
        }
        void CloseResultsMenu() => grid.ContextMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.ClosedEvent));
        OpenResultsMenu(grid);
        if (grid.SelectedItem != originalSelection || explorerMenu.IsEnabled || deleteMenu.IsEnabled || !explorer.IsEnabled || !delete.IsEnabled)
            throw new InvalidOperationException("Empty-space right-click lost selection or enabled item-specific menu actions.");
        // Exercise WPF's close-before-click ordering for layout commands.
        CloseResultsMenu();
        expand.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        content.Measure(new Size(1120, 720));
        content.Arrange(new Rect(0, 0, 1120, 720));
        content.UpdateLayout();
        if (window.WindowState != WindowState.Maximized || optionsCard.Visibility != Visibility.Collapsed ||
            restore.Visibility != Visibility.Visible || grid.ActualHeight <= normalHeight + 150 ||
            grid.ItemsSource != originalSource || grid.SelectedItem != originalSelection || pattern.Text != "unicode")
            throw new InvalidOperationException("Expanding results did not enlarge the area while preserving its state.");
        restore.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        content.Measure(new Size(1120, 720));
        content.Arrange(new Rect(0, 0, 1120, 720));
        content.UpdateLayout();
        if (window.WindowState != WindowState.Normal || optionsCard.Visibility != Visibility.Visible ||
            restore.Visibility != Visibility.Collapsed || grid.ItemsSource != originalSource || grid.SelectedItem != originalSelection)
            throw new InvalidOperationException("Restoring the normal layout lost results or window state.");
        OpenResultsMenu(grid, keyboard: true);
        if (!explorerMenu.IsEnabled || !deleteMenu.IsEnabled || explorerMenu.Tag != originalSelection)
            throw new InvalidOperationException("Keyboard context menu did not use the selected row.");
        CloseResultsMenu();
        var secondRow = (DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(1);
        OpenResultsMenu(secondRow);
        if (grid.SelectedIndex != 1 || explorerMenu.Tag != grid.SelectedItem || !deleteMenu.IsEnabled)
            throw new InvalidOperationException("Right-clicking a row did not target that row.");
        CloseResultsMenu();
        window.WindowState = WindowState.Maximized;
        expand.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        window.WindowState = WindowState.Normal;
        restore.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (window.WindowState != WindowState.Normal)
            throw new InvalidOperationException("Restoring controls overwrote a manual restore from an initially maximized window.");
        expand.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        window.WindowState = WindowState.Normal;
        window.WindowState = WindowState.Maximized;
        restore.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (window.WindowState != WindowState.Maximized)
            throw new InvalidOperationException("Restoring controls overwrote a later manual maximize.");
        window.WindowState = WindowState.Normal;
        grid.SelectedIndex = -1;
        if (grid.SelectionMode != DataGridSelectionMode.Extended || grid.Columns[3].SortMemberPath != nameof(SearchResult.LastWriteTimeUtc))
            throw new InvalidOperationException("Extended selection or typed date sorting is not enabled.");
        var datedItems = new List<SearchResult>();
        foreach (var fixture in new[] { ("Zulu.txt", new DateTime(2024, 12, 30, 12, 0, 0, DateTimeKind.Utc)),
            ("Alpha.txt", new DateTime(2026, 1, 2, 12, 0, 0, DateTimeKind.Utc)),
            ("Middle.txt", new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc)) })
        {
            string filename = Path.Combine(root, fixture.Item1);
            File.WriteAllText(filename, "selection and sorting fixture");
            File.SetLastWriteTimeUtc(filename, fixture.Item2);
            datedItems.Add(SearchResult.Capture(filename));
        }
        ((ResultCollection)grid.ItemsSource).ReplaceAll(datedItems);
        RefreshLayout();
        void ClickColumn(int index)
        {
            var header = VisualChildren<DataGridColumnHeader>(grid).First(item => item.Column == grid.Columns[index]);
            var button = VisualChildren<Button>(header).Single(item => item.Name == "SortHeaderButton");
            if (!button.IsEnabled) throw new InvalidOperationException("A sort header is disabled.");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            RefreshLayout();
        }
        ClickColumn(0);
        if (!grid.Items.Cast<SearchResult>().Select(item => item.Name).SequenceEqual(new[] { "Alpha.txt", "Middle.txt", "Zulu.txt" }))
            throw new InvalidOperationException("Clicking Name did not sort alphabetically.");
        ClickColumn(3);
        if (!grid.Items.Cast<SearchResult>().Select(item => item.Name).SequenceEqual(new[] { "Zulu.txt", "Middle.txt", "Alpha.txt" }))
            throw new InvalidOperationException("Clicking Date Modified did not sort chronologically.");
        ClickColumn(3);
        if (!grid.Items.Cast<SearchResult>().Select(item => item.Name).SequenceEqual(new[] { "Alpha.txt", "Middle.txt", "Zulu.txt" }))
            throw new InvalidOperationException("Clicking Date Modified again did not reverse the sort.");
        ClickColumn(1);
        if (grid.Items.SortDescriptions[0].PropertyName != nameof(SearchResult.Type) || grid.Columns[1].SortDirection != System.ComponentModel.ListSortDirection.Ascending)
            throw new InvalidOperationException("Clicking Type did not sort by type.");
        ClickColumn(1);
        if (grid.Columns[1].SortDirection != System.ComponentModel.ListSortDirection.Descending)
            throw new InvalidOperationException("Clicking Type again did not reverse sorting.");
        ClickColumn(2);
        if (grid.Items.SortDescriptions[0].PropertyName != nameof(SearchResult.FullPath) || grid.Columns[2].SortDirection != System.ComponentModel.ListSortDirection.Ascending)
            throw new InvalidOperationException("Clicking Full Path did not sort by path.");
        ClickColumn(2);
        if (grid.Columns[2].SortDirection != System.ComponentModel.ListSortDirection.Descending)
            throw new InvalidOperationException("Clicking Full Path again did not reverse sorting.");
        using (var modifierKeyboard = new ModifierKeyboard())
        {
            void ClickRow(int index, ModifierKeys modifiers)
            {
                grid.ScrollIntoView(grid.Items[index]);
                RefreshLayout();
                var row = (DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(index);
                var cell = VisualChildren<DataGridCell>(row).First(item => item.Column == grid.Columns[0]);
                modifierKeyboard.Held = modifiers;
                if (Keyboard.Modifiers != modifiers) throw new InvalidOperationException("The test keyboard modifiers were not applied.");
                try
                {
                    cell.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
                    cell.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.MouseDownEvent });
                    cell.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.MouseUpEvent });
                }
                finally { modifierKeyboard.Held = ModifierKeys.None; }
                RefreshLayout();
            }
            ClickRow(0, ModifierKeys.None);
            ClickRow(2, ModifierKeys.Shift);
            if (grid.SelectedItems.Count != 3) throw new InvalidOperationException("Shift-click did not select the visible range after date sorting.");
            ClickRow(0, ModifierKeys.None);
            ClickRow(2, ModifierKeys.Control);
            if (grid.SelectedItems.Count != 2 || grid.SelectedItems.Contains(grid.Items[1]))
                throw new InvalidOperationException("Ctrl-click did not select separate rows.");
            ClickRow(0, ModifierKeys.Control);
            if (grid.SelectedItems.Count != 1 || !grid.SelectedItems.Contains(grid.Items[2]))
                throw new InvalidOperationException("Ctrl-click did not toggle an existing selection.");
        }
        grid.UnselectAll();
        grid.SelectedItems.Add(grid.Items[0]);
        grid.SelectedItems.Add(grid.Items[2]);
        if (grid.SelectedItems.Count != 2 || !delete.IsEnabled || explorer.IsEnabled || !delete.Content.ToString()!.Contains("2"))
            throw new InvalidOperationException("Non-adjacent selection did not enable the correct batch action.");
        grid.ScrollIntoView(grid.Items[2]);
        RefreshLayout();
        OpenResultsMenu((DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(2));
        if (grid.SelectedItems.Count != 2 || deleteMenu.Tag is not SearchResult[] { Length: 2 } || !deleteMenu.IsEnabled || explorerMenu.IsEnabled)
            throw new InvalidOperationException("Right-clicking a selected row lost or mis-targeted the multi-selection.");
        CloseResultsMenu();
        OpenResultsMenu(grid);
        if (grid.SelectedItems.Count != 2 || deleteMenu.IsEnabled)
            throw new InvalidOperationException("Empty-space context menu changed the multi-selection.");
        CloseResultsMenu();
        var selectedBeforeSort = grid.SelectedItems.Cast<SearchResult>().ToHashSet();
        ClickColumn(0);
        if (!selectedBeforeSort.SetEquals(grid.SelectedItems.Cast<SearchResult>()))
            throw new InvalidOperationException("Sorting lost the selected items.");
        var sortState = ResultsSortState.Capture(grid);
        ResultsSortState.Suspend(grid);
        if (grid.Items.SortDescriptions.Count != 0 || grid.Columns.Any(column => column.SortDirection is not null))
            throw new InvalidOperationException("Suspending a sort left a stale indicator.");
        sortState.Restore(grid);
        if (grid.Columns[0].SortDirection != System.ComponentModel.ListSortDirection.Ascending)
            throw new InvalidOperationException("Restoring a sort did not restore its indicator.");

        var actualGrid = (ResultsDataGrid)grid;
        var originals = grid.Items.Cast<SearchResult>().ToArray();
        var large = Enumerable.Range(0, 5000).Select(i => new SearchResult($"bulk-{i:D5}", Path.Combine(root, $"bulk-{i:D5}"), false)).ToArray();
        ((ResultCollection)grid.ItemsSource).ReplaceAll(large);
        int selectionNotifications = 0;
        SelectionChangedEventHandler counter = (_, _) => selectionNotifications++;
        grid.SelectionChanged += counter;
        actualGrid.ReplaceSelection(large);
        grid.SelectionChanged -= counter;
        if (grid.SelectedItems.Count != 5000 || selectionNotifications != 1)
            throw new InvalidOperationException("Large selection restoration was not batched.");
        ((ResultCollection)grid.ItemsSource).ReplaceAll(originals);
        actualGrid.ReplaceSelection(originals.Take(2));
        RefreshLayout();
        var dialog = new BatchDeleteDialog(new BatchPreflight([originals[0]],
            [new(originals[1], DeletionStatus.Failed, "Locked fixture; will be skipped.")]), 2, DeletionMode.RecycleBin);
        if (((DataGrid)dialog.FindName("Targets")).Items.Count != 2 || !((Button)dialog.FindName("CancelAction")).IsCancel)
            throw new InvalidOperationException("Batch confirmation does not show all targets or a cancel action.");
        var dialogContent = (FrameworkElement)dialog.Content;
        dialogContent.Measure(new Size(980, 480));
        dialogContent.Arrange(new Rect(0, 0, 980, 480));
        dialogContent.UpdateLayout();
        var dialogLayout = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => dialogLayout.Continue = false));
        Dispatcher.PushFrame(dialogLayout);
        dialogContent.UpdateLayout();
        if (previewPath is not null)
        {
            var dialogBitmap = new RenderTargetBitmap(980, 480, 96, 96, PixelFormats.Pbgra32);
            dialogBitmap.Render(dialogContent);
            var dialogEncoder = new PngBitmapEncoder();
            dialogEncoder.Frames.Add(BitmapFrame.Create(dialogBitmap));
            using var dialogOutput = File.Create(Path.ChangeExtension(previewPath, ".confirmation.png"));
            dialogEncoder.Save(dialogOutput);
        }
        dialog.Close();
        matchMode.SelectedValue = NameMatchMode.Wildcard;
        pattern.Text = "*.txt";
        RunSearch();
        if (grid.Columns[0].SortDirection != System.ComponentModel.ListSortDirection.Ascending ||
            grid.Items.SortDescriptions.Count == 0 || grid.Items.SortDescriptions[0].PropertyName != nameof(SearchResult.Name))
            throw new InvalidOperationException("A new search lost the selected Name sort.");
        ClickColumn(3);
        var expectedDateDirection = grid.Columns[3].SortDirection;
        RunSearch();
        if (grid.Columns[3].SortDirection != expectedDateDirection || grid.Items.SortDescriptions[0].PropertyName != nameof(SearchResult.LastWriteTimeUtc))
            throw new InvalidOperationException("A new search lost the selected date sort.");
        RunSearch(cancelImmediately: true);
        if (grid.Columns[3].SortDirection != expectedDateDirection || grid.Items.SortDescriptions[0].PropertyName != nameof(SearchResult.LastWriteTimeUtc))
            throw new InvalidOperationException("Cancelling a search lost the selected sort.");
        RunSearch();
        RefreshLayout();
        RunSearch(afterStart: () =>
        {
            if (search.IsEnabled || !grid.CanUserSortColumns)
                throw new InvalidOperationException("The live-sort test did not start during an active search.");
            ClickColumn(0);
        });
        if (grid.Items.SortDescriptions[0].PropertyName != nameof(SearchResult.Name) ||
            grid.Columns[0].SortDirection != System.ComponentModel.ListSortDirection.Ascending)
            throw new InvalidOperationException("A header sort selected during scanning was overwritten at completion.");
        ScrollAlignmentTests.Run(window, RefreshLayout, previewPath);
        if (previewPath is not null)
        {
            var bitmap = new RenderTargetBitmap(1120, 720, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(content);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(previewPath);
            encoder.Save(output);
        }
        // Closing during a started scan must not wait for that scan's next OS call.
        bool closeCancelled = false;
        window.Closing += (_, e) => closeCancelled = e.Cancel;
        search.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.Close();
        if (closeCancelled) throw new InvalidOperationException("Closing the window was blocked by a read-only scan.");
        app.Shutdown();
    }
}
