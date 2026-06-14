using System.Windows;
using DokiCode.Control.Views;

namespace DokiCode.Control;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // The boot sequence opens first and hands off to MainWindow (which becomes the app's
        // MainWindow + sets ShutdownMode=OnMainWindowClose). Until then, keep the app alive even
        // though only BootWindow is open.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        new BootWindow().Show();
    }
}
