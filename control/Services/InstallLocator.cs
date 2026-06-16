using System.IO;

namespace DokiDex.Control.Services;

// First-run / recovery: point the app at a DokiDex home it can manage. Phase 1 adopts an EXISTING folder
// (the repo or a prior install) in place; Phase 2's Setup Wizard adds fresh-install. Best-effort + guarded.
public static class InstallLocator
{
    // A folder is a usable DokiDex home when it contains doki.ps1 (the script the manager + installer drive).
    public static bool IsValidHome(string? dir) =>
        !string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir!, "doki.ps1"));

    // Prompt for a DokiDex folder; on a valid pick, persist it as the adopted install root
    // (InstallManaged=false — managed in place, never overwritten) and refresh RepoPaths. Returns true if set.
    public static bool PromptAndAdopt()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select your DokiDex folder (the one containing doki.ps1)",
        };
        if (RepoPaths.HasValidRoot && Directory.Exists(RepoPaths.Root)) dlg.InitialDirectory = RepoPaths.Root;
        while (dlg.ShowDialog() == true)
        {
            if (IsValidHome(dlg.FolderName))
            {
                var s = AppSettings.Load();
                s.InstallRoot = dlg.FolderName;
                s.InstallManaged = false;   // adopted an existing repo/install — never overwrite it
                s.Save();
                RepoPaths.Refresh();
                return true;
            }
            System.Windows.MessageBox.Show(
                "That folder doesn't contain doki.ps1.\n\nPick the DokiDex folder itself (the repo / install root).",
                "DokiDex", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
        return false;
    }
}
