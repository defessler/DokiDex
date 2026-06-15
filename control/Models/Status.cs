namespace DokiCode.Control.Models;

// Mirrors the JSON emitted by `doki.ps1 status json`. One source of truth: every field
// here is produced by doki's StatusJson/GpuJson, so a new service in $Services appears in
// the panel automatically. Deserialized case-insensitively (see DokiService.JsonOpts).

public sealed class StatusDoc
{
    public List<ServiceStatus> Services { get; set; } = new();
    public Dictionary<string, List<string>> Profiles { get; set; } = new();
    public GpuStatus? Gpu { get; set; }
}

public sealed class ServiceStatus
{
    public string Name { get; set; } = "";
    public string Group { get; set; } = "";
    public string Desc { get; set; } = "";
    public int? Port { get; set; }
    public string? Ui { get; set; }
    public int? VramGb { get; set; }          // JSON: vramGB
    public string Health { get; set; } = "";
    public bool Healthy { get; set; }
    public bool Running { get; set; }
    public int? Pid { get; set; }
    public bool Installed { get; set; }
    public string? Model { get; set; }
    public string? ModelState { get; set; }
    public List<string> ConfiguredModels { get; set; } = new();
    public string Version { get; set; } = "";
    public string Update { get; set; } = "";
    public List<string> Profiles { get; set; } = new();
}

public sealed class GpuStatus
{
    public int UsedMB { get; set; }
    public int TotalMB { get; set; }
    public int Util { get; set; }
    public int Temp { get; set; }
    public double Watts { get; set; }
    public int? Fan { get; set; }
    public bool PerProcess { get; set; }      // false on this WDDM driver
    public string ActiveGroup { get; set; } = "none";
}

// Carried from a ViewModel to the View when a GPU-evicting mode switch needs confirmation.
public sealed record ConfirmInfo(
    string Title,
    string[] WillStop,
    string[] WillStart,
    string HeadroomText,
    bool Fits,
    Action OnConfirmed);
