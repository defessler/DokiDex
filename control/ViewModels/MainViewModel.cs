using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DokiCode.Control.Models;
using DokiCode.Control.Services;

namespace DokiCode.Control.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly DokiService _doki = new();
    private readonly TestGenService _test = new();
    private readonly Dispatcher _ui;
    private readonly Dictionary<string, ServiceViewModel> _byName = new();
    private Dictionary<string, List<string>> _profiles = new();
    private CancellationTokenSource? _cts;
    private volatile bool _polling;

    public ObservableCollection<ServiceViewModel> LlmServices { get; } = new();
    public ObservableCollection<ServiceViewModel> MediaServices { get; } = new();
    public GpuViewModel Gpu { get; } = new();
    public LogsViewModel Logs { get; } = new();

    [ObservableProperty] private string _activeMode = "none";
    [ObservableProperty] private string _activeGroup = "none";
    [ObservableProperty][NotifyPropertyChangedFor(nameof(SwitchExplain))] private string _hoverMode = "";
    [ObservableProperty] private string _statusText = "ready";
    [ObservableProperty] private string _lastUpdated = "—";
    [ObservableProperty] private bool _llmActive = true;
    [ObservableProperty] private bool _mediaActive;

    // VM asks the View to confirm a GPU-evicting switch.
    public event Action<ConfirmInfo>? ConfirmRequested;

    public MainViewModel(Dispatcher ui) => _ui = ui;

    public string SwitchExplain => BuildExplain(string.IsNullOrEmpty(HoverMode) ? ActiveMode : HoverMode);

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _ = PollLoop(_cts.Token);
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
            if (doc == null) { _ui.Invoke(() => StatusText = "doki status unavailable"); return; }
            _ui.Invoke(() => Apply(doc));
        }
        finally { _polling = false; }
    }

    private void Apply(StatusDoc doc)
    {
        _profiles = doc.Profiles ?? new();
        foreach (var s in doc.Services)
        {
            if (_byName.TryGetValue(s.Name, out var vm)) vm.Update(s);
            else
            {
                vm = new ServiceViewModel(_doki, _test, s);
                _byName[s.Name] = vm;
                (s.Group == "media" ? MediaServices : LlmServices).Add(vm);
            }
        }
        Gpu.Update(doc.Gpu);
        ActiveGroup = doc.Gpu?.ActiveGroup ?? "none";
        ActiveMode = DeriveMode();
        LlmActive = ActiveGroup != "media";
        MediaActive = ActiveGroup == "media";
        Logs.SyncServices(doc.Services.Select(x => x.Name));
        LastUpdated = DateTime.Now.ToString("HH:mm:ss");
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
    }

    [RelayCommand]
    private void Verify()
    {
        _doki.RunVerifyConsole();
        StatusText = "running full-stack verify (console window)…";
    }
}
