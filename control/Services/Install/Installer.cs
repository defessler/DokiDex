using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DokiDex.Control.Services;

// The fresh-install runner: extract the embedded payload to the home, then run setup.ps1 (+ the optional
// coder-model download), streaming all output to `log`. Adopt-existing skips extraction. Returns true only if
// every step exits 0. The heavy lifting stays in the bundled scripts (the hybrid); this just drives them.
public static class Installer
{
    public static async Task<bool> RunAsync(string home, InstallChoice choice, bool fresh, Action<string> log, CancellationToken ct)
    {
        try { Directory.CreateDirectory(home); }
        catch (Exception ex) { log($"ERROR: cannot create {home}: {ex.Message}"); return false; }

        if (fresh)
        {
            log($"Extracting DokiDex runtime to {home} …");
            if (!Payload.ExtractBundledTo(home, overwriteExisting: true)) { log("ERROR: embedded payload missing."); return false; }
            log("Runtime extracted.");
        }

        var setupArgs = InstallPlan.SetupArgs(choice);
        log($"Running setup.ps1 {string.Join(" ", setupArgs)} …");
        var pwshArgs = new List<string> { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(home, "setup.ps1") };
        pwshArgs.AddRange(setupArgs);
        if (!await ShellStreamAsync(home, "pwsh", pwshArgs, log, ct).ConfigureAwait(false)) return false;

        if (choice.CoderModels)
        {
            log("Downloading coder models (serving\\download_models.py) …");
            var py = new List<string> { Path.Combine(home, "serving", "download_models.py") };
            if (!await ShellStreamAsync(home, "python", py, log, ct).ConfigureAwait(false)) return false;
        }

        log("Install complete.");
        return true;
    }

    private static async Task<bool> ShellStreamAsync(string home, string exe, List<string> args, Action<string> log, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = home,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) log(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) log(e.Data); };
        try
        {
            if (!p.Start()) { log($"ERROR: could not start {exe} (is it on PATH?)"); return false; }
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            return p.ExitCode == 0;
        }
        catch (OperationCanceledException) { try { if (!p.HasExited) p.Kill(true); } catch { } log("cancelled."); return false; }
        catch (Exception ex) { log($"ERROR: {ex.Message}"); return false; }
    }
}
