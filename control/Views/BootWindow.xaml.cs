using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DokiDex.Control.Models;
using DokiDex.Control.Services;
using DokiDex.Control.ViewModels;

namespace DokiDex.Control.Views;

// The boot sequence — "THE SEAL IGNITES". A magitek sigil = the arc-reactor faceplate = powers an
// LCARS rail booted from REAL `doki status json`. The status probe is fired BEFORE any pixel moves
// and the animation runs on its own clock, so the cinematic NEVER gates on the subprocess; a master
// curtain timer always rises (opens MainWindow) even if every probe failed. The boot is a curtain,
// never a gate.
public partial class BootWindow : Window
{
    private readonly BootViewModel _vm = new();
    private readonly Task<StatusDoc?> _statusTask;
    private readonly CancellationTokenSource _probeCts = new();
    private readonly List<DispatcherTimer> _rowTimers = new();
    private bool _handedOff;
    private DispatcherTimer? _curtain;

    public BootWindow()
    {
        InitializeComponent();
        // real pwsh probe in flight before pixels move; cancellable so a skip tears down the
        // (otherwise up-to-30s) subprocess + its async graph immediately at handoff.
        _statusTask = new DokiService().GetStatusAsync(_probeCts.Token);
        DataContext = _vm;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Focus();
        _ = FillWhenReady();   // snap "· · ·" placeholders -> real telemetry whenever it lands

        if (!SystemParameters.ClientAreaAnimation) { ReducedMotion(); return; }

        ((Storyboard)Resources["RotOuterSb"]).Begin(this);   // continuous rings (Forever)
        ((Storyboard)Resources["RotInnerSb"]).Begin(this);
        ((Storyboard)Resources["IntroSb"]).Begin(this);      // the master timeline

        ScheduleRowReveals(1470, 1620, 1770, 1920);          // 4 service rows, fixed stagger

        StartCurtain(2900);   // the always-fires path: opens the panel even if everything is down
    }

    private async Task FillWhenReady()
    {
        try { _vm.Fill(await _statusTask); }
        catch { _vm.Fill(null); }
    }

    private void ScheduleRowReveals(params int[] delaysMs)
    {
        for (int i = 0; i < _vm.Rows.Count && i < delaysMs.Length; i++)
        {
            var row = _vm.Rows[i];
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delaysMs[i]) };
            t.Tick += (s, _) => { ((DispatcherTimer)s!).Stop(); row.Shown = true; };
            _rowTimers.Add(t);
            t.Start();
        }
    }

    private void StartCurtain(int ms)
    {
        _curtain = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        _curtain.Tick += (_, _) => HandOff();
        _curtain.Start();
    }

    // OS "show animations" off: no animated beats — set the final composed frame, fade it up, hand off.
    private void ReducedMotion()
    {
        SigilStar.Opacity = 0.85; RingOuter.Opacity = 1; RingInner.Opacity = 1;
        IrisScale.ScaleX = 1; IrisScale.ScaleY = 1; SigilFlood.Opacity = 0.32;
        Wordmark.Opacity = 1; SkipHint.Opacity = 0.4; Reticle.Opacity = 1;
        BaselineScale.ScaleX = 1; SpineScale.ScaleY = 1; GpuBlock.Opacity = 1;
        CoderLine.Opacity = 1; Resolve.Opacity = 1;
        foreach (var r in _vm.Rows) r.Shown = true;
        Root.Opacity = 0;
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(250)));
        StartCurtain(1300);
    }

    // Only intended skip keys hand off — DON'T swallow Alt+F4 (a real quit gesture) into a launch.
    private void OnKeySkip(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Escape or Key.Space or Key.Enter) HandOff();
    }
    private void OnMouseSkip(object sender, MouseButtonEventArgs e) => HandOff();

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        // If Boot closes for any reason OTHER than the handoff (e.g. Alt+F4 to quit on the splash), shut
        // the app down: under OnExplicitShutdown, closing the sole window does NOT terminate the process,
        // so we'd otherwise leak a zombie.
        if (!_handedOff) Application.Current.Shutdown();
    }

    // Idempotent — skip, the curtain timer, and any completion all converge on exactly one clean
    // "engage" dissolve (never an abort/hard cut). Shows MainWindow FIRST so it is already lit under
    // the wash (Boot is Topmost) -> zero black gap -> then closes Boot when the fade completes.
    private void HandOff()
    {
        if (_handedOff) return;
        _handedOff = true;
        _probeCts.Cancel();                          // tear down a still-running pwsh probe + its graph
        _curtain?.Stop();
        foreach (var t in _rowTimers) t.Stop();      // stop any unfired row-reveal timers

        MainWindow main;
        try { main = new MainWindow(); main.Show(); }
        catch (Exception ex)
        {
            // A MainWindow ctor failure must not strand the Topmost splash forever (curtain already
            // stopped, _handedOff blocks retry). Surface it and quit explicitly — ShutdownMode is still
            // OnExplicitShutdown here, so a bare Close() would not terminate the process.
            MessageBox.Show(ex.Message, "DokiDex failed to open", MessageBoxButton.OK, MessageBoxImage.Error);
            Application.Current.Shutdown();
            return;
        }
        Application.Current.MainWindow = main;
        Application.Current.ShutdownMode = ShutdownMode.OnMainWindowClose;

        CrestScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.06, TimeSpan.FromMilliseconds(320)));
        CrestScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.06, TimeSpan.FromMilliseconds(320)));
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(320)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }
}
