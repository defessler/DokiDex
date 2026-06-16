using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DokiDex.Control.Views;

namespace DokiDex.Control;

public partial class App : Application
{
    private static Mutex? _single;   // held for the process lifetime; guards against duplicate launches

    /// <summary>Populated-panel sample mode: `dotnet run -- --design` (or DOKI_SAMPLE=1) renders the cockpit
    /// from canned data with no backend/boot/updater — for off-GPU UI/theme iteration + snapshots.</summary>
    public static bool DesignMode { get; private set; }

    /// <summary>Optional initial page for --design / --render (e.g. `--page studio`) so a specific surface
    /// can be opened or captured off-GPU without a click. Null → Dashboard (the default).</summary>
    public static string? StartPage { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // The boot sequence opens first and hands off to MainWindow (which becomes the app's MainWindow
        // + sets ShutdownMode=OnMainWindowClose). Until then, keep the app alive even though only the
        // BootWindow is open.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Never die to a raw "DokiDex has stopped working" WER dialog with no window: surface a UI-thread
        // throw (e.g. a future missing StaticResource in a ctor) as a message, log it, then exit cleanly.
        DispatcherUnhandledException += (_, a) =>
        {
            LogCrash(a.Exception);
            MessageBox.Show(a.Exception.Message, "DokiDex failed to start", MessageBoxButton.OK, MessageBoxImage.Error);
            a.Handled = true;
            Shutdown();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, a) => LogCrash(a.ExceptionObject);

        // DESIGN MODE: render the populated panel from canned sample data — no backend, no boot, no updater,
        // no single-instance lock — for off-GPU UI/theme iteration + snapshots.  (dotnet run -- --design)
        // --render <path>: the same canned panel, drawn ONCE fully off-screen to a PNG (honest hero preview +
        // snapshot capture), then exit — never shows a window on a monitor and loads no models.
        StartPage = ArgValue(e.Args, "--page")?.ToLowerInvariant();
        var renderPath = ArgValue(e.Args, "--render");
        if (renderPath != null) { DesignMode = true; RenderDesignToPng(renderPath); return; }

        if (e.Args.Any(a => a.Equals("--design", StringComparison.OrdinalIgnoreCase))
            || Environment.GetEnvironmentVariable("DOKI_SAMPLE") == "1")
        {
            DesignMode = true;
            var w = new Views.MainWindow();
            Current.MainWindow = w;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            w.Show();
            return;
        }

        // Single-instance: a self-contained single-file exe self-extracts for a few seconds on first run —
        // exactly when an impatient user double-clicks again. Don't open a second boot + poll loop + updater
        // (two in-place updater swaps racing the same exe is the worst case).
        _single = new Mutex(false, @"Local\DokiDex.Control.SingleInstance");
        bool owns;
        try { owns = _single.WaitOne(TimeSpan.FromSeconds(2)); }
        catch (AbandonedMutexException) { owns = true; }   // a prior instance died holding it -> we own it now
        if (!owns) { Shutdown(); return; }

        // Auto-update housekeeping (all guarded/best-effort): sweep superseded binaries, and if an update
        // was downloaded-but-not-applied, swap it in place and relaunch BEFORE any UI shows. The whole
        // block is wrapped so a storage hiccup (locked LocalAppData, etc.) can never strand the launch.
        var exe = Environment.ProcessPath;
        if (exe != null)
        {
            try
            {
                Services.Updater.CleanUpSuperseded(exe);
                var relaunch = Services.Updater.ApplyInPlaceNow(exe);
                if (relaunch != null)
                {
                    // hand the mutex to the replacement BEFORE spawning it, so the child acquires immediately
                    try { _single.ReleaseMutex(); } catch { }
                    _single.Dispose(); _single = null;
                    bool launched = false;
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(relaunch) { UseShellExecute = true }); launched = true; } catch { }
                    if (launched) { Shutdown(); return; }
                    // relaunch failed -> don't vanish the app; re-acquire (best-effort) and boot normally below
                    _single = new Mutex(false, @"Local\DokiDex.Control.SingleInstance");
                    try { _single.WaitOne(0); } catch { }
                }
            }
            catch { /* any updater failure must not block boot */ }
        }

        // The app is independent of any cloned repo: if no DokiDex home resolves (no saved InstallRoot AND not
        // launched from inside a repo), open the Setup Wizard (fresh install OR adopt an existing folder) before
        // booting. It persists InstallRoot + refreshes RepoPaths on success; cancel = exit.
        if (!Services.RepoPaths.HasValidRoot)
        {
            var installer = new Views.InstallerWindow();
            if (installer.ShowDialog() != true) { Shutdown(); return; }
        }

        new BootWindow().Show();
    }

    // value following `name` in argv (so `--render out.png` -> "out.png"), or null.
    private static string? ArgValue(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }

    // Draw the populated --design panel ONCE to a PNG, parked far off any monitor (never visible, no models
    // loaded), then shut down. Drives the same MainWindow + LoadDesignSample path the live panel uses, so the
    // committed hero shot can't drift from what the panel actually renders. (dotnet run -- --render <path>)
    private void RenderDesignToPng(string path)
    {
        const int W = 1280, H = 880;
        var win = new Views.MainWindow
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000, Top = -32000,    // off every monitor; ShowInTaskbar=false -> truly invisible
            ShowInTaskbar = false,
            Width = W, Height = H,
        };
        Current.MainWindow = win;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // ContentRendered drives the capture; a watchdog guarantees the process still EXITS if the off-screen
        // compositor never signals it (a headless / RDP / session-0 edge case) instead of hanging forever.
        var watchdog = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        bool done = false;
        void Finish(bool capture)
        {
            if (done) return;
            done = true;
            watchdog.Stop();
            try
            {
                if (capture)
                {
                    win.UpdateLayout();
                    var rtb = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
                    rtb.Render(win);
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(rtb));
                    var full = System.IO.Path.GetFullPath(path);
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
                    using (var fs = System.IO.File.Create(full)) enc.Save(fs);
                    Console.WriteLine($"rendered {full}  ({rtb.PixelWidth}x{rtb.PixelHeight})");
                }
                else { Console.Error.WriteLine("render: timed out waiting for ContentRendered"); }
            }
            catch (Exception ex) { LogCrash(ex); }
            finally { try { win.Close(); } catch { } Shutdown(); }
        }
        win.ContentRendered += (_, _) => Finish(true);
        watchdog.Tick += (_, _) => Finish(false);
        watchdog.Start();
        win.Show();   // realizes the tree off-screen -> Loaded loads the sample -> ContentRendered captures it
    }

    // Best-effort crash log next to the update staging dir (%LocalAppData%\dokidex\crash.log).
    private static void LogCrash(object? ex)
    {
        try
        {
            var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "dokidex");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "crash.log"), $"{DateTime.Now:o}  {ex}\n\n");
        }
        catch { }
    }
}
