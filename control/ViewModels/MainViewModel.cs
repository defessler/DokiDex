using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DokiDex.Control.Models;
using DokiDex.Control.Services;

namespace DokiDex.Control.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly DokiService _doki = new();
    private readonly TestGenService _test = new();
    private readonly UpdateService _update = new();
    private readonly Dispatcher _ui;
    private readonly Dictionary<string, ServiceViewModel> _byName = new();
    private Dictionary<string, List<string>> _profiles = new();
    private readonly Dictionary<string, UpdateInfo> _lastUpdates = new();
    private CancellationTokenSource? _cts;
    private volatile bool _polling;

    public ObservableCollection<ServiceViewModel> LlmServices { get; } = new();
    public ObservableCollection<ServiceViewModel> MediaServices { get; } = new();
    // Gates the dashboard's "reading doki status …" empty-state on BOTH bands, not just LLM: a media-only
    // first poll would otherwise paint the loading sigil over live MEDIA cards. Notified in Apply().
    public int TotalServiceCount => LlmServices.Count + MediaServices.Count;
    public GpuViewModel Gpu { get; } = new();
    public LogsViewModel Logs { get; } = new();

    [ObservableProperty] private string _activeMode = "none";
    [ObservableProperty] private string _activeGroup = "none";
    [ObservableProperty][NotifyPropertyChangedFor(nameof(SwitchExplain))] private string _hoverMode = "";
    [ObservableProperty] private string _statusText = "ready";
    [ObservableProperty] private bool _statusUnavailable;   // a poll returned null -> show the error overlay, not the loading spinner
    [ObservableProperty] private string _lastUpdated = "—";
    [ObservableProperty] private bool _llmActive = true;
    [ObservableProperty] private bool _mediaActive;

    // ---- the control panel's own auto-updater (Services/Updater.cs -> github releases) ----
    [ObservableProperty] private string _appVersion = Updater.RunningVersion();
    [ObservableProperty] private bool _updateAvailable;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(UpdateHasMessage))] private string _updateText = "";
    [ObservableProperty][NotifyPropertyChangedFor(nameof(Busy))] private bool _updateBusy;
    [ObservableProperty] private double _updateProgress;
    private string? _updateTag;
    public bool UpdateHasMessage => !string.IsNullOrEmpty(UpdateText);

    // ---- "working" signal driving the animated caption sigil (loading / running an action) ----
    // True while: a just-fired command is still settling (_actionTicks counts down over polls), an update
    // check/download runs, or any service is warming up (degraded = running-but-not-yet-healthy). Self-
    // clearing via the 2s poll, so it can never spin forever on a healthy idle stack.
    [ObservableProperty][NotifyPropertyChangedFor(nameof(Busy))] private bool _checkingUpdates;
    private int _actionTicks;
    public bool Busy => _actionTicks > 0 || CheckingUpdates || UpdateBusy
                        || _byName.Values.Any(v => v.StateKind == "degraded");
    private void BeginAction() { _actionTicks = 3; OnPropertyChanged(nameof(Busy)); }

    // VM asks the View to confirm a GPU-evicting switch.
    public event Action<ConfirmInfo>? ConfirmRequested;

    public MainViewModel(Dispatcher ui) => _ui = ui;

    public string SwitchExplain => BuildExplain(string.IsNullOrEmpty(HoverMode) ? ActiveMode : HoverMode);

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _ = PollLoop(_cts.Token);
        _ = CheckSelfUpdate();   // best-effort, once per launch
    }

    public void Shutdown()
    {
        _cts?.Cancel();
        Logs.Dispose();
    }

    private async Task PollLoop(CancellationToken ct)
    {
        var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            await PollOnce(ct);
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (!_polling) await PollOnce(ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task PollOnce(CancellationToken ct)
    {
        _polling = true;
        try
        {
            var doc = await _doki.GetStatusAsync(ct).ConfigureAwait(false);
            if (doc == null) { _ui.Invoke(() => { StatusUnavailable = true; StatusText = "doki status unavailable"; }); return; }
            _ui.Invoke(() => Apply(doc));
        }
        finally { _polling = false; }
    }

    private void Apply(StatusDoc doc)
    {
        StatusUnavailable = false;   // a poll succeeded
        _profiles = doc.Profiles ?? new();
        foreach (var s in doc.Services)
        {
            if (_byName.TryGetValue(s.Name, out var vm)) vm.Sync(s);
            else
            {
                vm = new ServiceViewModel(_doki, _test, s);
                _byName[s.Name] = vm;
                (s.Group == "media" ? MediaServices : LlmServices).Add(vm);
            }
        }
        foreach (var kv in _lastUpdates)
            if (_byName.TryGetValue(kv.Key, out var uvm)) { uvm.Version = kv.Value.Version; uvm.Update = kv.Value.Update; }
        Gpu.Update(doc.Gpu);
        ActiveGroup = doc.Gpu?.ActiveGroup ?? "none";
        ActiveMode = DeriveMode();
        LlmActive = ActiveGroup != "media";
        MediaActive = ActiveGroup == "media";
        Logs.SyncServices(doc.Services.Select(x => x.Name));
        LastUpdated = DateTime.Now.ToString("HH:mm:ss");
        if (_actionTicks > 0) _actionTicks--;
        OnPropertyChanged(nameof(TotalServiceCount));   // collapse the empty-state once either band has cards
        OnPropertyChanged(nameof(Busy));                // service warmup (degraded) + action countdown drive the caption sigil
        OnPropertyChanged(nameof(SwitchExplain));
    }

    private string DeriveMode()
    {
        bool Run(string n) => _byName.TryGetValue(n, out var v) && v.Running;
        if (Run("media") || Run("prompt-rewriter")) return "media";
        if (Run("fim")) return "coexist";
        if (Run("llama-swap")) return "agent";
        return "none";
    }

    private int VramFor(string svc) => _byName.TryGetValue(svc, out var v) ? (v.VramGb ?? 0) : 0;

    private string BuildExplain(string mode)
    {
        if (!_profiles.TryGetValue(mode, out var svcs) || svcs.Count == 0)
            return "Pick a mode. The GPU runs one group at a time.";
        var sum = svcs.Sum(VramFor);
        var names = string.Join(" + ", svcs);
        var fit = sum <= 32 ? $"~{32 - sum} GB free" : "⚠ exceeds 32 GB";
        return $"{mode.ToUpperInvariant()}: {names}  (~{sum} GB · {fit})";
    }

    [RelayCommand]
    private void SwitchMode(string target)
    {
        var targetGroup = target == "media" ? "media" : "llm";
        var current = ActiveGroup;
        bool evicting = current is "llm" or "media" && current != targetGroup;

        void Go()
        {
            _doki.Up(target);
            StatusText = $"switching to {target.ToUpperInvariant()}…";
            BeginAction();
        }

        if (!evicting) { Go(); return; }

        var stop = _byName.Values.Where(v => v.Running && GroupOf(v.Name) == current).Select(v => v.Name).ToArray();
        var start = _profiles.TryGetValue(target, out var s) ? s.ToArray() : Array.Empty<string>();
        var sum = start.Sum(VramFor);
        var headroom = sum <= 32
            ? $"after switch: ~{sum} GB used · ~{32 - sum} GB free  ✓ fits in 32 GB"
            : $"after switch: ~{sum} GB  ⚠ exceeds 32 GB";
        ConfirmRequested?.Invoke(new ConfirmInfo(
            $"Switch to {target.ToUpperInvariant()} mode?",
            stop, start, headroom, sum <= 32, Go));
    }

    private string GroupOf(string svc)
    {
        if (svc is "media" or "prompt-rewriter") return "media";
        return "llm";
    }

    [RelayCommand]
    private void StopAll()
    {
        _doki.Down();
        StatusText = "stopping all services…";
        BeginAction();
    }

    [RelayCommand]
    private void Verify()
    {
        _doki.RunVerifyConsole();
        StatusText = "running full-stack verify (console window)…";
    }

    [RelayCommand]
    private async Task Retry() => await PollOnce(CancellationToken.None);   // re-probe `doki status` from the error overlay

    [RelayCommand]
    private async Task CheckUpdates()
    {
        CheckingUpdates = true;   // (UI thread here) drives the caption sigil; cleared in finally
        StatusText = "checking for updates (git / gh)…";
        try
        {
            var infos = await _update.CheckAsync().ConfigureAwait(false);
            _ui.Invoke(() =>
            {
                foreach (var info in infos)
                {
                    _lastUpdates[info.Service] = info;
                    if (_byName.TryGetValue(info.Service, out var vm)) { vm.Version = info.Version; vm.Update = info.Update; }
                }
                StatusText = infos.Count > 0 ? $"updates checked {DateTime.Now:HH:mm:ss}" : "update check returned nothing (git/gh unavailable?)";
            });
        }
        finally { _ui.Invoke(() => CheckingUpdates = false); }
    }

    // ---- self-update: check this panel's own GitHub releases, download + swap + relaunch ----
    // Self-update only makes sense for a real published apphost exe — never under `dotnet run`, where
    // Environment.ProcessPath is the SHARED dotnet host (swapping it would corrupt the SDK).
    private static bool CanSelfUpdate()
    {
        var p = Environment.ProcessPath;
        if (string.IsNullOrEmpty(p)) return false;
        return !System.IO.Path.GetFileNameWithoutExtension(p).Equals("dotnet", StringComparison.OrdinalIgnoreCase);
    }

    private async Task CheckSelfUpdate()
    {
        if (!CanSelfUpdate()) return;
        try
        {
            var info = await Updater.CheckForUpdateAsync().ConfigureAwait(false);
            if (info == null) return;
            _ui.Invoke(() => { _updateTag = info.Tag; UpdateAvailable = true; UpdateText = $"panel update available · {info.Tag}"; });
        }
        catch { }
    }

    [RelayCommand]
    private async Task UpdateAndRestart()
    {
        if (_updateTag == null || UpdateBusy || !CanSelfUpdate()) return;
        _ui.Invoke(() => { UpdateBusy = true; UpdateProgress = 0; UpdateText = $"downloading {_updateTag}…"; });
        var prog = new Progress<double>(p => _ui.Invoke(() => UpdateProgress = p));
        var ok = await Updater.DownloadUpdateAsync(_updateTag, prog).ConfigureAwait(false);
        if (!ok) { _ui.Invoke(() => { UpdateBusy = false; UpdateText = "update download failed"; }); return; }

        // Swap OFF the UI thread: the verified copy is tens of MB and crosses volumes (exe on the repo
        // drive, staging under %LocalAppData%), so doing it on the dispatcher would freeze the window.
        var exe = Environment.ProcessPath;
        var relaunch = exe != null ? await Task.Run(() => Updater.ApplyInPlaceNow(exe)).ConfigureAwait(false) : null;
        _ui.Invoke(() =>
        {
            if (relaunch == null) { UpdateBusy = false; UpdateText = "update apply failed"; return; }
            UpdateText = "restarting…";
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(relaunch) { UseShellExecute = true }); } catch { }
            System.Windows.Application.Current.Shutdown();
        });
    }
}
