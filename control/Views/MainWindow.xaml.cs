using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using DokiDex.Control.Models;
using DokiDex.Control.ViewModels;

namespace DokiDex.Control.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly DashboardView _dashboard;
    private readonly StudioView _studio;
    private readonly LogsView _logs;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel(Dispatcher);
        DataContext = _vm;
        _vm.ConfirmRequested += OnConfirmRequested;

        _dashboard = new DashboardView { DataContext = _vm };
        _studio = new StudioView { DataContext = _vm.Studio };   // the Studio page binds its own sub-VM
        _logs = new LogsView { DataContext = _vm };
        PageHost.Content = _dashboard;
        // --page <name> (paired with --design/--render) opens straight to a page so that surface can be
        // captured off-GPU without a click. Default stays Dashboard.
        if (App.StartPage == "studio") { PageHost.Content = _studio; SetNav(NavStudioBtn); }
        else if (App.StartPage == "logs") { PageHost.Content = _logs; SetNav(NavLogsBtn); }

        // WindowChrome + WindowStyle=None covers the taskbar and bleeds past the screen on maximize
        // unless WM_GETMINMAXINFO clamps to the monitor work area (DPI/multi-monitor exact).
        SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);
        StateChanged += (_, _) =>
        {
            bool max = WindowState == WindowState.Maximized;
            MaxBtn.Content = max ? "" : "";    // restore / maximize glyph
            MaxBtn.ToolTip = max ? "Restore" : "Maximize"; // keep the affordance in sync with the glyph
        };

        Loaded += (_, _) => { if (App.DesignMode) _vm.LoadDesignSample(); else _vm.Start(); };
        Closed += (_, _) => _vm.Shutdown();
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnMaxRestore(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void NavDashboard(object sender, RoutedEventArgs e) { PageHost.Content = _dashboard; SetNav(NavDashBtn); }
    private void NavStudio(object sender, RoutedEventArgs e) { PageHost.Content = _studio; SetNav(NavStudioBtn); }
    private void NavLogs(object sender, RoutedEventArgs e) { PageHost.Content = _logs; SetNav(NavLogsBtn); }
    private void SetNav(Button on) { NavDashBtn.Tag = null; NavStudioBtn.Tag = null; NavLogsBtn.Tag = null; on.Tag = "active"; }

    private void OnConfirmRequested(ConfirmInfo info)
    {
        var dlg = new ConfirmWindow(info) { Owner = this };
        if (dlg.ShowDialog() == true) info.OnConfirmed();
    }

    // ---- WM_GETMINMAXINFO: own a borderless maximize completely so it (a) fills only the work area
    //      (no taskbar cover / no edge bleed), (b) fills a LARGER secondary monitor (ptMaxTrackSize),
    //      and (c) still honors MinWidth/MinHeight (ptMinTrackSize — which short-circuiting WPF would drop).
    //      Instance method: it must read MinWidth/MinHeight + the window's per-monitor DPI. ----
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_GETMINMAXINFO = 0x0024;
        if (msg == WM_GETMINMAXINFO)
        {
            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            var mon = MonitorFromWindow(hwnd, 2 /*MONITOR_DEFAULTTONEAREST*/);
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(mon, ref info))
            {
                var work = info.rcWork; var full = info.rcMonitor;
                // maximize rect = the target monitor's work area, in monitor-relative coords (correct on a 2nd monitor)
                mmi.ptMaxPosition.X = work.left - full.left;
                mmi.ptMaxPosition.Y = work.top - full.top;
                mmi.ptMaxSize.X = work.right - work.left;
                mmi.ptMaxSize.Y = work.bottom - work.top;
                // raise the max-TRACK ceiling to match, else the OS caps the maximized size at the PRIMARY
                // monitor's default metrics and a physically larger secondary monitor is left with dead space.
                mmi.ptMaxTrackSize.X = work.right - work.left;
                mmi.ptMaxTrackSize.Y = work.bottom - work.top;
                // we fully own the message (handled=true below), which short-circuits WPF's own
                // MinWidth/MinHeight enforcement — so re-apply it here, converting DIPs -> physical px.
                var dpi = VisualTreeHelper.GetDpi(this);
                mmi.ptMinTrackSize.X = (int)Math.Ceiling(MinWidth * dpi.DpiScaleX);
                mmi.ptMinTrackSize.Y = (int)Math.Ceiling(MinHeight * dpi.DpiScaleY);
                Marshal.StructureToPtr(mmi, lParam, true);
                handled = true;
            }
            // else: GetMonitorInfo failed on a DEFAULTTONEAREST handle (extreme corner case, e.g. a monitor
            // invalidated mid-message during a hot-plug/dock). Leave handled=false and accept WPF's default
            // maximize for this one frame rather than write bad values — WPF then also restores ptMinTrackSize.
        }
        return IntPtr.Zero;
    }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr handle, int flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr handle, ref MONITORINFO info);

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MINMAXINFO { public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags; }
}
