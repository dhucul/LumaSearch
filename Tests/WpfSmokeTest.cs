using System.IO;
using System.Windows;
using System.Windows.Controls;
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

        void RunSearch()
        {
        search.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
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
        if (timedOut || grid.Items.Count == 0) throw new InvalidOperationException("The UI search did not produce results.");
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
