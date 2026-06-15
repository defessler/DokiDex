using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using DokiDex.Control.Models;

namespace DokiDex.Control.ViewModels;

// Boot-sequence telemetry. Rows are seeded with dim "· · ·" placeholders the instant the window
// shows, then SNAP to real `doki status json` values the moment the background probe lands — so
// the cinematic never gates on the subprocess. Honesty rule: LIVE=cyan, STANDBY=gold, offline=grey;
// NEVER red/green (that's the panel's job). See docs spec: "THE SEAL IGNITES".
public static class BootInk
{
    public static readonly Brush Cyan = Freeze("#FF35E0F0");   // the only thing that emits light
    public static readonly Brush Gold = Freeze("#FFE8C77A");   // etched metal / structure — never glows
    public static readonly Brush Grey = Freeze("#FF5A6B78");   // honest standby/unreachable, never alarmist
    public static readonly Brush Dim = Freeze("#FF7E8C99");
    static Brush Freeze(string hex) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!); b.Freeze(); return b; }
}

public partial class BootRowVm : ObservableObject
{
    [ObservableProperty] private string _tick = "·";
    [ObservableProperty] private Brush _tickBrush = BootInk.Grey;
    [ObservableProperty] private string _label = "";
    [ObservableProperty] private string _value = "· · ·";
    [ObservableProperty] private Brush _valueBrush = BootInk.Dim;
    [ObservableProperty] private bool _shown;     // flipped on the reveal stagger -> DataTrigger animates it in

    public BootRowVm(string label) { _label = label; }
}

public partial class BootViewModel : ObservableObject
{
    public ObservableCollection<BootRowVm> Rows { get; } = new()
    {
        new BootRowVm("llama-swap   :8080"),
        new BootRowVm("fim          :8012"),
        new BootRowVm("tts · stt    :8004 · :8005"),
        new BootRowVm("media        :7801"),
    };

    [ObservableProperty] private string _gpuGroupLabel = "RTX 5090";
    [ObservableProperty] private string _gpuMetrics = "· · ·";
    [ObservableProperty] private string _coderModelLine = "coder model  ·  · · ·";
    [ObservableProperty] private string _resolveText = "STANDING BY";

    BootRowVm Row(int i) => Rows[i];

    // Snap placeholders to real telemetry. doc==null => honest "unreachable" everywhere; the boot
    // still completes regardless (the curtain is never a gate).
    public void Fill(StatusDoc? doc)
    {
        if (doc == null)
        {
            foreach (var r in Rows) { r.Tick = "○"; r.TickBrush = BootInk.Grey; r.Value = "unreachable"; r.ValueBrush = BootInk.Grey; }
            GpuGroupLabel = "RTX 5090"; GpuMetrics = "telemetry unreachable";
            CoderModelLine = "coder model  ·  —";
            ResolveText = "OFFLINE — LAUNCHING PANEL";
            return;
        }

        var gpu = new GpuViewModel();
        gpu.Update(doc.Gpu);
        GpuGroupLabel = gpu.GroupLabel;
        GpuMetrics = gpu.Available ? $"{gpu.Headline}  ·  {gpu.Temp}°C  ·  {gpu.Watts:0}W" : "GPU n/a";

        ServiceStatus? S(string name) => doc.Services?.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        var swap = S("llama-swap"); var fim = S("fim"); var tts = S("tts"); var stt = S("stt"); var media = S("media");

        SetRow(Row(0), swap?.Healthy == true, swap?.Running == true);
        SetRow(Row(1), fim?.Healthy == true, fim?.Running == true);
        SetVoiceRow(Row(2), tts, stt);
        SetRow(Row(3), media?.Healthy == true, media?.Running == true);

        var model = swap?.Healthy == true ? (swap.Model ?? "cold — swap on first call") : "cold — swap on first call";
        CoderModelLine = $"coder model  ·  {model}";

        // resolve: never fake a count it can't back
        var rows = new[] { Row(0), Row(1), Row(2), Row(3) };
        int live = rows.Count(r => ReferenceEquals(r.TickBrush, BootInk.Cyan));
        ResolveText = live == rows.Length ? "ALL SYSTEMS NOMINAL" : $"CORE READY  ·  {live} OF {rows.Length} LIVE";
    }

    static void SetRow(BootRowVm r, bool healthy, bool running)
    {
        if (healthy) { r.Tick = "◇"; r.TickBrush = BootInk.Cyan; r.Value = "LIVE"; r.ValueBrush = BootInk.Cyan; }
        else if (running) { r.Tick = "◇"; r.TickBrush = BootInk.Gold; r.Value = "standby"; r.ValueBrush = BootInk.Gold; }
        else { r.Tick = "○"; r.TickBrush = BootInk.Grey; r.Value = "standby"; r.ValueBrush = BootInk.Grey; }
    }

    static void SetVoiceRow(BootRowVm r, ServiceStatus? tts, ServiceStatus? stt)
    {
        bool a = tts?.Healthy == true, b = stt?.Healthy == true;
        if (a && b) { r.Tick = "◇"; r.TickBrush = BootInk.Cyan; r.Value = "LIVE"; r.ValueBrush = BootInk.Cyan; }
        else if (a || b) { r.Tick = "◇"; r.TickBrush = BootInk.Gold; r.Value = "partial"; r.ValueBrush = BootInk.Gold; }
        else { r.Tick = "○"; r.TickBrush = BootInk.Grey; r.Value = "standby"; r.ValueBrush = BootInk.Grey; }
    }
}
