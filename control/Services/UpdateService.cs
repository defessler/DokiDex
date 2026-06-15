using System.Diagnostics;
using System.IO;

namespace DokiDex.Control.Services;

public sealed record UpdateInfo(string Service, string Version, string Update);

// Best-effort "is there a newer version?" check, ported from control.ps1's update job:
// SwarmUI via git (current tag + commits behind), llama-swap via the GitHub latest release.
// All shelled out and guarded — missing git/gh/network just yields fewer rows, never throws.
public sealed class UpdateService
{
    public async Task<List<UpdateInfo>> CheckAsync(CancellationToken ct = default)
    {
        var list = new List<UpdateInfo>();

        var sw = Path.Combine(RepoPaths.Root, "media", "SwarmUI");
        if (Directory.Exists(Path.Combine(sw, ".git")))
        {
            await Run("git", $"-C \"{sw}\" fetch -q", ct);
            var ver = (await Run("git", $"-C \"{sw}\" describe --tags --always", ct)).Trim();
            var behind = (await Run("git", $"-C \"{sw}\" rev-list --count HEAD..origin/HEAD", ct)).Trim();
            var upd = int.TryParse(behind, out var b) && b > 0 ? $"{b} behind" : "current";
            if (!string.IsNullOrEmpty(ver)) list.Add(new("media", ver, upd));
        }

        var latest = (await Run("gh", "api repos/mostlygeek/llama-swap/releases/latest --jq .tag_name", ct)).Trim();
        if (!string.IsNullOrEmpty(latest) && !latest.Contains("error", StringComparison.OrdinalIgnoreCase))
            list.Add(new("llama-swap", "", $"latest {latest}"));

        return list;
    }

    private static async Task<string> Run(string exe, string args, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return "";
            var outTask = p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            return await outTask.ConfigureAwait(false);
        }
        catch { return ""; }
    }
}
