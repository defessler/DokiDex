using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace DokiDex.Control.Services;

// Native lifecycle orchestration (the "control" half of the hybrid): decides WHAT to start/stop — GPU-group
// mutual-exclusion, the not-installed skip — and shells the bundled per-service launcher (serving/start-*.ps1,
// the fiddly "heavy" part) only for the actual launch. Stops are native (taskkill by pidfile, else by port).
public static class Lifecycle
{
    public static void Up(string profile)
    {
        if (!ServiceRegistry.Profiles.TryGetValue(profile, out var members)) return;
        var wantGroup = ServiceRegistry.GroupForProfile(profile);
        foreach (var def in ServiceRegistry.Services)               // evict the opposite GPU group first
            if (def.Group != wantGroup) StopService(def);
        foreach (var name in members)
        {
            var def = ServiceRegistry.Find(name);
            if (def != null && IsInstalled(def)) StartLauncher(def); // skip not-installed cleanly
        }
    }

    public static void Down()
    {
        foreach (var def in ServiceRegistry.Services) StopService(def);
    }

    public static void Start(string name)
    {
        var def = ServiceRegistry.Find(name);
        if (def == null || !IsInstalled(def)) return;
        foreach (var other in ServiceRegistry.Services)             // respect group exclusion
            if (other.Group != def.Group) StopService(other);
        StartLauncher(def);
    }

    public static void Stop(string name)
    {
        var def = ServiceRegistry.Find(name);
        if (def != null) StopService(def);
    }

    public static void Restart(string name) { Stop(name); Start(name); }

    private static bool IsInstalled(ServiceDef def) =>
        def.RequiresRel == null || File.Exists(Path.Combine(RepoPaths.Root, def.RequiresRel));

    private static void StartLauncher(ServiceDef def)
    {
        var runDir = RepoPaths.RunDir;
        try { Directory.CreateDirectory(runDir); } catch { }
        var script = Path.Combine(RepoPaths.Root, "serving", def.LaunchScript);
        if (!File.Exists(script)) return;
        var psi = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoPaths.Root };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-File", script,
                                  "-Detach", "-PidFile", Path.Combine(runDir, def.Name + ".pid"),
                                  "-LogFile", Path.Combine(runDir, def.Name + ".log") })
            psi.ArgumentList.Add(a);
        try { Process.Start(psi)?.Dispose(); } catch { }
    }

    private static void StopService(ServiceDef def)
    {
        var pidFile = Path.Combine(RepoPaths.RunDir, def.Name + ".pid");
        int? pid = null;
        try { if (File.Exists(pidFile) && int.TryParse(File.ReadAllText(pidFile).Trim(), out var p)) pid = p; } catch { }
        if (pid is int id)
        {
            TaskKill(id);
            try { File.Delete(pidFile); } catch { }
            return;
        }
        // no pidfile (started outside the app): kill whatever still listens on its port, so a GPU-group
        // switch can actually evict it — otherwise an untracked opposite-group server OOMs 32 GB.
        foreach (var owner in OwnersOfPort(def.Port)) TaskKill(owner);
    }

    private static void TaskKill(int pid)
    {
        try
        {
            var psi = new ProcessStartInfo("taskkill", $"/PID {pid} /T /F")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi);
            p?.WaitForExit(5000);
        }
        catch { }
    }

    private static IEnumerable<int> OwnersOfPort(int port)
    {
        try
        {
            var psi = new ProcessStartInfo("netstat", "-ano -p tcp") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi);
            if (p == null) return System.Array.Empty<int>();
            var outp = p.StandardOutput.ReadToEnd();
            p.WaitForExit(4000);
            return ParsePortOwners(outp, port);
        }
        catch { return System.Array.Empty<int>(); }
    }

    // Pure: PIDs LISTENING on a TCP port, parsed from `netstat -ano -p tcp` output.
    internal static IReadOnlyList<int> ParsePortOwners(string netstatOutput, int port)
    {
        var pids = new HashSet<int>();
        foreach (var raw in netstatOutput.Split('\n'))
        {
            var t = raw.Trim();
            if (!t.StartsWith("TCP", System.StringComparison.OrdinalIgnoreCase)) continue;
            if (!t.Contains("LISTENING", System.StringComparison.OrdinalIgnoreCase)) continue;
            var parts = t.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5) continue;
            if (!parts[1].EndsWith(":" + port, System.StringComparison.Ordinal)) continue;   // local addr ends with :PORT
            if (int.TryParse(parts[^1], out var owner) && owner > 0) pids.Add(owner);
        }
        return pids.ToArray();
    }
}
