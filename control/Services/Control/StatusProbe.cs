using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using DokiDex.Control.Models;

namespace DokiDex.Control.Services;

// The NATIVE status poll: builds the same StatusDoc the panel parses, with no pwsh — HTTP health probes,
// pidfile reads from <root>\.run, an nvidia-smi parse, and llama-swap's /running + /v1/models. Everything is
// guarded so a single failed probe degrades one field instead of throwing; GetAsync always returns a doc.
public static class StatusProbe
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };

    public static async Task<StatusDoc> GetAsync(CancellationToken ct = default)
    {
        var services = new List<ServiceStatus>();
        foreach (var def in ServiceRegistry.Services)
        {
            var healthy = await ProbeAsync(def.Health, ct).ConfigureAwait(false);
            var pid = ReadPid(def.Name);
            var running = pid is int p && IsAlive(p);
            var installed = def.RequiresRel == null || File.Exists(Path.Combine(RepoPaths.Root, def.RequiresRel));
            string? model = null, modelState = null; var configured = new List<string>();
            if (def.Name == "llama-swap" && healthy)
                (model, modelState, configured) = await LlamaSwapInfoAsync(ct).ConfigureAwait(false);
            // Sidecars (fim/embed/tts/kokoro/stt) have no live "loaded model" to probe — they only ever serve
            // one hardcoded model — so fall back to the static ServiceDef.Model (2.6). llama-swap/media are
            // unaffected: ServiceDef.Model is null for them, so their live value (or lack of one) always wins.
            model ??= def.Model;
            services.Add(new ServiceStatus
            {
                Name = def.Name, Group = def.Group, Desc = def.Desc, Port = def.Port, Ui = def.Ui,
                VramGb = def.VramGb, Health = def.Health, Healthy = healthy, Running = running, Pid = pid,
                Installed = installed, Model = model, ModelState = modelState, ConfiguredModels = configured,
                Profiles = ServiceRegistry.Profiles.Where(kv => kv.Value.Contains(def.Name)).Select(kv => kv.Key).ToList(),
            });
        }
        var profiles = ServiceRegistry.Profiles.ToDictionary(kv => kv.Key, kv => new List<string>(kv.Value));
        return new StatusDoc { Services = services, Profiles = profiles, Gpu = ParseGpu(NvidiaSmiLine(), ActiveGroup(services)) };
    }

    // active group = the group of the first running service (registry order), else "none". Mirrors doki.ps1.
    internal static string ActiveGroup(IEnumerable<ServiceStatus> services)
    {
        foreach (var s in services) if (s.Running) return s.Group;
        return "none";
    }

    private static async Task<bool> ProbeAsync(string url, CancellationToken ct)
    {
        try { using var r = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false); return true; }
        catch { return false; }
    }

    private static int? ReadPid(string name)
    {
        try
        {
            var f = Path.Combine(RepoPaths.RunDir, name + ".pid");
            return File.Exists(f) && int.TryParse(File.ReadAllText(f).Trim(), out var p) ? p : null;
        }
        catch { return null; }
    }

    private static bool IsAlive(int pid)
    {
        try { using var _ = Process.GetProcessById(pid); return true; } catch { return false; }
    }

    private static string? NvidiaSmiLine()
    {
        try
        {
            var psi = new ProcessStartInfo("nvidia-smi",
                "--query-gpu=memory.used,memory.total,utilization.gpu,temperature.gpu,power.draw,fan.speed --format=csv,noheader,nounits")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi);
            if (p == null) return null;
            var outp = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);
            return outp.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim();
        }
        catch { return null; }
    }

    // Pure: parse one nvidia-smi CSV line. Per-field coercion so a single [N/A] degrades ONE number (the same
    // discipline doki.ps1's GpuJson uses), and a [N/A] fan stays null rather than zeroing the gauge.
    internal static GpuStatus? ParseGpu(string? line, string activeGroup)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        var p = line.Split(',').Select(x => x.Trim()).ToArray();
        int Int(int i) => i < p.Length && double.TryParse(p[i], out var v) ? (int)v : 0;
        double Dbl(int i) => i < p.Length && double.TryParse(p[i], out var v) ? v : 0;
        int? Fan(int i) => i < p.Length && double.TryParse(p[i], out var v) ? (int)v : null;
        return new GpuStatus
        {
            UsedMB = Int(0), TotalMB = Int(1), Util = Int(2), Temp = Int(3),
            Watts = Dbl(4), Fan = Fan(5), PerProcess = false, ActiveGroup = activeGroup,
        };
    }

    private static async Task<(string? model, string? state, List<string> configured)> LlamaSwapInfoAsync(CancellationToken ct)
    {
        string? model = null, state = null;
        var configured = new List<string>();
        try
        {
            var json = await Http.GetStringAsync("http://127.0.0.1:8080/running", ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var first = root.TryGetProperty("running", out var run) && run.ValueKind == JsonValueKind.Array && run.GetArrayLength() > 0 ? run[0] : root;
            if (first.ValueKind == JsonValueKind.Object)
            {
                if (first.TryGetProperty("model", out var m)) model = m.GetString();
                if (first.TryGetProperty("state", out var st)) state = st.GetString();
            }
        }
        catch { }
        try
        {
            var json = await Http.GetStringAsync("http://127.0.0.1:8080/v1/models", ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                foreach (var item in data.EnumerateArray())
                    if (item.TryGetProperty("id", out var id) && id.GetString() is string s) configured.Add(s);
        }
        catch { }
        return (model, state, configured);
    }
}
