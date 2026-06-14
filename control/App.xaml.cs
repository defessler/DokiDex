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

        // Auto-update housekeeping (all guarded/best-effort): sweep superseded binaries, and if an
        // update was downloaded-but-not-applied, swap it in place and relaunch BEFORE any UI shows.
        var exe = Environment.ProcessPath;
        if (exe != null)
        {
            Services.Updater.CleanUpSuperseded(exe);
            var relaunch = Services.Updater.ApplyInPlaceNow(exe);
            if (relaunch != null)
            {
                bool launched = false;
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(relaunch) { UseShellExecute = true }); launched = true; } catch { }
                if (launched) { Shutdown(); return; }
                // relaunch failed -> don't vanish the app; fall through to the normal boot below
            }
        }

        new BootWindow().Show();
    }
}
