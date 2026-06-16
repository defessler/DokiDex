using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace DokiDex.Control.Services;

// A host prerequisite the installer checks for and (where it can) installs via winget. winget itself and the
// GPU driver can't be auto-installed — those carry a Url to open instead.
public sealed record Prereq(string Name, string Cmd, string? WingetId, string Note, string? Url = null);

public static class Prereqs
{
    public static readonly Prereq[] All =
    {
        new("winget (App Installer)", "winget", null, "installs the tools below", "https://aka.ms/getwinget"),
        new("git", "git", "Git.Git", "clone SwarmUI + fetch sources"),
        new("Python 3", "python", "Python.Python.3.12", "TTS / STT / memory + model downloads"),
        new("uv", "uv", "astral-sh.uv", "runs the MCP servers"),
        new("PowerShell 7", "pwsh", "Microsoft.PowerShell", "the control-plane shell setup.ps1 runs in"),
        new("NVIDIA driver", "nvidia-smi", null, "GPU + CUDA", "https://www.nvidia.com/download/index.aspx"),
    };

    // Is an executable resolvable on the given PATH? Pure + unit-tested (checks each dir for exe + .exe/.cmd/.bat).
    public static bool OnPath(string exe, string? pathEnv)
    {
        if (string.IsNullOrEmpty(pathEnv)) return false;
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            foreach (var ext in new[] { "", ".exe", ".cmd", ".bat" })
                try { if (File.Exists(Path.Combine(dir.Trim(), exe + ext))) return true; } catch { }
        }
        return false;
    }

    public static bool Present(Prereq p) => OnPath(p.Cmd, Environment.GetEnvironmentVariable("PATH"));

    // One-click winget install (blocking; call off the UI thread). Refreshes this process's PATH from the
    // registry afterward so a freshly-installed tool resolves immediately. Returns true if it's now present.
    public static bool TryWingetInstall(Prereq p)
    {
        if (p.WingetId == null) return false;
        try
        {
            var psi = new ProcessStartInfo("winget",
                $"install {p.WingetId} --silent --accept-package-agreements --accept-source-agreements --disable-interactivity")
            { UseShellExecute = false, CreateNoWindow = true };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(600_000);
        }
        catch { }
        try
        {
            var parts = new[]
            {
                Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine),
                Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User),
            }.Where(s => !string.IsNullOrEmpty(s));
            Environment.SetEnvironmentVariable("PATH", string.Join(";", parts));
        }
        catch { }
        return Present(p);
    }
}
