using System.Windows;

namespace LumaSearch;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length == 1 && e.Args[0] == "--verify-release")
        {
            try { PackageVerification.Verify(AppContext.BaseDirectory); Shutdown(0); }
            catch { Shutdown(2); }
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--delete-worker")
        {
            try { await DeletionJob.RunWorkerAsync(e.Args[1]); Shutdown(0); }
            catch { Shutdown(3); }
            return;
        }
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        MainWindow = new MainWindow();
        MainWindow.Show();
    }
}
