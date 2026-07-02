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
            var running = EffectiveRunning(pid is int p && IsAlive(p), healthy);
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

    // 1.1 truthful status: a service whose health endpoint ANSWERS is up, whatever the pidfile says — stale or
    // missing pidfiles (launcher restarts, untracked starts) made Running=false over a live, loaded server, which
    // fed the audit's live-caught "GPU NONE · 16/32 GB" pill. The raw pidfile fact only ADDS (a tracked-but-
    // unhealthy process still shows running so the user can see it's wedged rather than absent).
    internal static bool EffectiveRunning(bool rawRunning, bool healthy) => rawRunning || healthy;

    // One group's aggregated evidence for the ActiveGroup decision. HasLoadedModel means a LIVE probed model
    // (llama-swap's /running state) — never a sidecar's static ServiceDef.Model fallback (2.6), which exists
    // even when everything is stopped.
    internal readonly record struct GroupSignal(string Group, bool Healthy, bool HasLoadedModel);

    // active group, derived from HEALTH (which endpoints answer) rather than pidfile order — the pidfile-based
    // "first running service" rule reported "none" over a healthy llama-swap with a resident model (the audit's
    // #5). Rules: no healthy group -> "none"; one healthy group -> that group; BOTH healthy (a transition, or
    // coexist experiments) -> the group with a live loaded model wins, and when neither/both have one, prefer
    // "llm" (documented arbitrary tie-break: the LLM group is the daily-driver default). NOTE: intentionally
    // diverges from doki.ps1's pidfile-order rule — the panel/web pill now reports what is actually reachable.
    internal static string ActiveGroup(IEnumerable<GroupSignal> signals)
    {
        var byGroup = signals
            .GroupBy(s => s.Group, StringComparer.OrdinalIgnoreCase)
            .Select(g => new GroupSignal(g.Key, g.Any(s => s.Healthy), g.Any(s => s.Healthy && s.HasLoadedModel)))
            .Where(g => g.Healthy)
            .ToList();
        if (byGroup.Count == 0) return "none";
        if (byGroup.Count == 1) return byGroup[0].Group;
        var withModel = byGroup.Where(g => g.HasLoadedModel).ToList();
        if (withModel.Count == 1) return withModel[0].Group;
        var pool = withModel.Count > 0 ? withModel : byGroup;
        return pool.FirstOrDefault(g => string.Equals(g.Group, "llm", StringComparison.OrdinalIgnoreCase)) is { Healthy: true } llm
            ? llm.Group
            : pool[0].Group;
    }

    // ServiceStatus adapter: HasLoadedModel = a healthy service with a LIVE ModelState (set only by the
    // llama-swap /running probe) — a sidecar's static Model string (ModelState null) never counts.
    internal static string ActiveGroup(IEnumerable<ServiceStatus> services)
        => ActiveGroup(services.Select(s => new GroupSignal(
            s.Group ?? "", s.Healthy, s.Healthy && !string.IsNullOrEmpty(s.ModelState))));

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
