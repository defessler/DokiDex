using System.Diagnostics;
using System.Text.Json;
using DokiCode.Control.Models;

namespace DokiCode.Control.Services;

// The panel's only path to the control plane: shells out to doki.ps1. doki stays
// authoritative (group exclusion, .run\* lifecycle) — the panel never re-implements it.
public sealed class DokiService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public async Task<StatusDoc?> GetStatusAsync(CancellationToken ct = default)
    {
        var json = await CaptureAsync(new[] { "status", "json" }, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<StatusDoc>(json, JsonOpts); }
        catch { return null; }
    }

    // Control actions are fire-and-forget: doki runs in its own hidden pwsh and the 2s poll
    // reflects the new state. Mode switches can take a while (model loads) — we don't block.
    public void Up(string profile) => Spawn(new[] { "up", profile });
    public void Down() => Spawn(new[] { "down" });
    public void StartService(string svc) => Spawn(new[] { "start", svc });
    public void StopService(string svc) => Spawn(new[] { "stop", svc });
    public void RestartService(string svc) => Spawn(new[] { "restart", svc });

    public void OpenUi(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    public void RunVerifyConsole()
    {
        try
        {
            Process.Start(new ProcessStartInfo("pwsh",
                $"-NoProfile -NoExit -File \"{RepoPaths.DokiPs1}\" verify")
            { UseShellExecute = true, WorkingDirectory = RepoPaths.Root });
        }
        catch { }
    }

    private static void Spawn(string[] args)
    {
        var psi = NewPsi(args, capture: false);
        try { Process.Start(psi); } catch { }
    }

    private static async Task<string> CaptureAsync(string[] args, CancellationToken ct)
    {
        var psi = NewPsi(args, capture: true);
        try
        {
            using var p = Process.Start(psi);
            if (p == null) return "";
            var outTask = p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            return await outTask.ConfigureAwait(false);
        }
        catch { return ""; }
    }

    private static ProcessStartInfo NewPsi(string[] args, bool capture)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "pwsh",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = capture,
            RedirectStandardError = capture,
            WorkingDirectory = RepoPaths.Root,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(RepoPaths.DokiPs1);
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }
}
