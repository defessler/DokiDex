using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DokiDex.Control.Models;
using DokiDex.Control.Services;

namespace DokiDex.Control.ViewModels;

// One service card. Updated wholesale from each poll's ServiceStatus; commands shell doki.
public partial class ServiceViewModel : ObservableObject
{
    private readonly DokiService _doki;
    private readonly TestGenService _test;
    private readonly Func<DateTime> _now;
    private DateTime? _unhealthySince;   // first poll a running service was seen unhealthy -> grace before "crashed"

    public ServiceViewModel(DokiService doki, TestGenService test, ServiceStatus s)
        : this(doki, test, s, () => DateTime.UtcNow) { }

    // Test ctor: inject a clock so the degraded->crashed 90s escalation is unit-testable (InternalsVisibleTo).
    internal ServiceViewModel(DokiService doki, TestGenService test, ServiceStatus s, Func<DateTime> now)
    {
        _doki = doki; _test = test; _now = now;
        Name = s.Name;
        Sync(s);
    }

    public string Name { get; }

    [ObservableProperty] private string _group = "";
    [ObservableProperty] private int? _port;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(HasUi))] private string? _ui;
    [ObservableProperty] private int? _vramGb;
    [ObservableProperty][NotifyCanExecuteChangedFor(nameof(TestCommand))] private bool _healthy;
    [ObservableProperty] private bool _running;
    [ObservableProperty][NotifyCanExecuteChangedFor(nameof(StartCommand))][NotifyCanExecuteChangedFor(nameof(RestartCommand))] private bool _installed;
    [ObservableProperty] private int? _pid;
    [ObservableProperty] private string? _model;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(HasModelSwap))] private List<string> _configuredModels = new();
    [ObservableProperty] private string _stateKind = "down";   // healthy|starting|degraded|down|notinstalled
    [ObservableProperty] private string _stateLabel = "";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty][NotifyPropertyChangedFor(nameof(HasVersion))][NotifyPropertyChangedFor(nameof(VersionLine))] private string _version = "";
    [ObservableProperty][NotifyPropertyChangedFor(nameof(HasUpdate))][NotifyPropertyChangedFor(nameof(HasVersion))][NotifyPropertyChangedFor(nameof(VersionLine))] private string _update = "";

    [ObservableProperty] private bool _testRunning;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(HasTestResult))] private string _testResult = "";
    [ObservableProperty] private bool _testOk;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(HasTestFile))] private string? _testFile;

    public bool HasUi => !string.IsNullOrEmpty(Ui);
    public bool HasModelSwap => ConfiguredModels is { Count: > 1 };
    public bool HasTestResult => !string.IsNullOrEmpty(TestResult);
    public bool HasTestFile => !string.IsNullOrEmpty(TestFile);
    public bool HasVersion => !string.IsNullOrEmpty(Version) || (!string.IsNullOrEmpty(Update) && Update != "current");
    public bool HasUpdate => !string.IsNullOrEmpty(Update) && Update != "current";

    public string VersionLine => string.IsNullOrEmpty(Version) ? Update : (string.IsNullOrEmpty(Update) || Update == "current" ? $"{Version} · up to date" : $"{Version}  ▲ {Update}");

    public void Sync(ServiceStatus s)
    {
        Group = s.Group; Port = s.Port; Ui = s.Ui; VramGb = s.VramGb;
        Healthy = s.Healthy; Running = s.Running; Installed = s.Installed; Pid = s.Pid; Model = s.Model;
        ConfiguredModels = s.ConfiguredModels ?? new();

        if (!s.Installed)
        {
            StateKind = "notinstalled"; StateLabel = "not installed";
            _unhealthySince = null;
            Detail = Name switch
            {
                "tts" => "run  setup.ps1 -Tts",
                "stt" => "run  setup.ps1 -Stt",
                _     => "run  setup.ps1 -Media -Models full",
            };
            return;
        }
        // running-but-unhealthy is "warming up" (calm pulse) only for a grace window; past it the service
        // is genuinely stuck -> escalate to "crashed" (the red alarm), which also stops the busy sigil
        // spinning on it. Resets the moment it goes healthy or stops.
        if (s.Healthy) { StateKind = "healthy"; StateLabel = "healthy"; _unhealthySince = null; }
        else if (s.Running)
        {
            _unhealthySince ??= _now();
            if (_now() - _unhealthySince > TimeSpan.FromSeconds(90))
                { StateKind = "crashed"; StateLabel = "running · not responding"; }
            else
                { StateKind = "degraded"; StateLabel = "running · health failing"; }
        }
        else { StateKind = "down"; StateLabel = "stopped"; _unhealthySince = null; }

        var bits = new List<string>();
        if (!string.IsNullOrEmpty(s.Model)) bits.Add(s.Model!);
        if (s.VramGb is int v) bits.Add($"~{v} GB");
        if (s.Pid is int pid && s.Running) bits.Add($"pid {pid}");
        Detail = string.Join("   ·   ", bits);
    }

    private bool CanStart => Installed;
    [RelayCommand(CanExecute = nameof(CanStart))] private void Start() => _doki.StartService(Name);
    [RelayCommand] private void Stop() => _doki.StopService(Name);
    [RelayCommand(CanExecute = nameof(CanStart))] private void Restart() => _doki.RestartService(Name);
    [RelayCommand] private void OpenUi() { if (!string.IsNullOrEmpty(Ui)) _doki.OpenUi(Ui!); }
    [RelayCommand] private void SwapModel(string? model) { if (!string.IsNullOrEmpty(model) && model != Model) _doki.WarmLoadModel(model!); }
    [RelayCommand] private void OpenTestFile() { if (!string.IsNullOrEmpty(TestFile)) _doki.OpenArtifact(TestFile!); }

    private bool CanTest => Healthy && !TestRunning;

    [RelayCommand(CanExecute = nameof(CanTest))]
    private async Task Test()
    {
        TestRunning = true; TestResult = "testing…"; TestOk = false; TestFile = null;
        TestCommand.NotifyCanExecuteChanged();
        var r = await _test.RunAsync(Name);
        TestOk = r.Ok;
        TestResult = $"{(r.Ok ? "✓" : "✕")} {r.Summary}  ({r.Ms} ms)";
        TestFile = r.FilePath;
        TestRunning = false;
        TestCommand.NotifyCanExecuteChanged();
    }
}
