using CommunityToolkit.Mvvm.ComponentModel;
using DokiCode.Control.Models;

namespace DokiCode.Control.ViewModels;

// The trust instrument. One 32 GB card attributed to the active group (per-process VRAM is
// [N/A] on this WDDM driver). Headroom is a first-class number; amber under 2 GB.
public partial class GpuViewModel : ObservableObject
{
    [ObservableProperty] private bool _available;
    [ObservableProperty] private int _usedMB;
    [ObservableProperty] private int _totalMB = 32607;
    [ObservableProperty] private int _util;
    [ObservableProperty] private int _temp;
    [ObservableProperty] private double _watts;
    [ObservableProperty] private int? _fan;
    [ObservableProperty] private string _activeGroup = "none";

    public double UsedGb => UsedMB / 1024.0;
    public double TotalGb => TotalMB / 1024.0;
    public double FreeGb => Math.Max(0, (TotalMB - UsedMB) / 1024.0);
    public double UsedPercent => TotalMB > 0 ? Math.Round(100.0 * UsedMB / TotalMB) : 0;
    public bool LowHeadroom => Available && FreeGb < 2.0;
    public bool HotTemp => Available && Temp >= 80;

    public string Headline => Available
        ? $"{UsedGb:0.0} / {TotalGb:0.0} GB  ·  {UsedPercent:0}%"
        : "GPU n/a";
    public string SubLine => Available
        ? $"{FreeGb:0.0} GB free  ·  {Temp}°C  ·  {Watts:0}W{(Fan is int f ? $"  ·  fan {f}%" : "")}"
        : "nvidia-smi unavailable";
    public string GroupLabel => Available ? $"RTX 5090 · 32 GB · {ActiveGroup.ToUpperInvariant()} group" : "RTX 5090";

    public void Update(GpuStatus? g)
    {
        if (g == null) { Available = false; NotifyComputed(); return; }
        Available = true;
        UsedMB = g.UsedMB; TotalMB = g.TotalMB; Util = g.Util; Temp = g.Temp; Watts = g.Watts; Fan = g.Fan;
        ActiveGroup = string.IsNullOrEmpty(g.ActiveGroup) ? "none" : g.ActiveGroup;
        NotifyComputed();
    }

    private void NotifyComputed()
    {
        OnPropertyChanged(nameof(UsedGb)); OnPropertyChanged(nameof(TotalGb)); OnPropertyChanged(nameof(FreeGb));
        OnPropertyChanged(nameof(UsedPercent)); OnPropertyChanged(nameof(LowHeadroom)); OnPropertyChanged(nameof(HotTemp));
        OnPropertyChanged(nameof(Headline)); OnPropertyChanged(nameof(SubLine)); OnPropertyChanged(nameof(GroupLabel));
    }
}
