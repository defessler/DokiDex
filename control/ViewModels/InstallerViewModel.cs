using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DokiDex.Control.Services;

namespace DokiDex.Control.ViewModels;

// One prerequisite row in the installer's checklist: live present/absent + a one-click action.
public partial class PrereqRow : ObservableObject
{
    public Prereq Def { get; }
    public PrereqRow(Prereq def) { Def = def; Refresh(); }
    public string Name => Def.Name;
    public string Note => Def.Note;
    public bool CanWinget => Def.WingetId != null;
    public bool HasUrl => Def.Url != null;
    public string ActionLabel => CanWinget ? "Install" : (HasUrl ? "Get…" : "");
    public bool Actionable => CanWinget || HasUrl;
    [ObservableProperty] private bool _present;
    [ObservableProperty] private bool _busy;
    public void Refresh() => Present = Prereqs.Present(Def);
}

// The Setup Wizard: pick fresh-install (location + components) or adopt an existing folder, one-click-each
// prereqs, then run the bundled setup with live log. On success it persists InstallRoot + refreshes RepoPaths
// and signals the window to close. Drives Services.Installer; the heavy work stays in the bundled scripts.
public partial class InstallerViewModel : ObservableObject
{
    private readonly Dispatcher _ui;
    public event Action<bool>? CloseRequested;   // the window subscribes: true = proceed to boot, false = exit

    public InstallerViewModel(Dispatcher ui)
    {
        _ui = ui;
        foreach (var p in Prereqs.All) PrereqRows.Add(new PrereqRow(p));
        _home = DefaultHome();
    }

    public ObservableCollection<PrereqRow> PrereqRows { get; } = new();

    [ObservableProperty][NotifyPropertyChangedFor(nameof(IsFresh))][NotifyPropertyChangedFor(nameof(IsAdopt))] private string _mode = "fresh";
    public bool IsFresh => Mode == "fresh";
    public bool IsAdopt => Mode == "adopt";

    [ObservableProperty][NotifyPropertyChangedFor(nameof(EstimateText))][NotifyPropertyChangedFor(nameof(FreeText))] private string _home = "";
    [ObservableProperty][NotifyPropertyChangedFor(nameof(EstimateText))] private bool _coderModels = true;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(EstimateText))] private bool _media = true;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(EstimateText))] private bool _modelsFull = true;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(EstimateText))] private bool _tts;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(EstimateText))] private bool _stt;

    [ObservableProperty][NotifyPropertyChangedFor(nameof(Idle))][NotifyPropertyChangedFor(nameof(ShowProgress))] private bool _installing;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(Idle))][NotifyPropertyChangedFor(nameof(ShowProgress))] private bool _done;
    public bool Idle => !Installing && !Done;
    public bool ShowProgress => Installing || Done;
    [ObservableProperty] private string _log = "";
    [ObservableProperty] private string _statusLine = "";

    private InstallChoice Choice => new(CoderModels, Media, Media && ModelsFull, Tts, Stt);
    public string EstimateText => $"≈ {InstallPlan.EstimateGb(Choice)} GB to download/install · {FreeText}";
    public string FreeText
    {
        get
        {
            try { var root = Path.GetPathRoot(Home); if (!string.IsNullOrEmpty(root)) { var di = new DriveInfo(root); return $"{di.AvailableFreeSpace / 1_000_000_000} GB free on {di.Name}"; } }
            catch { }
            return "";
        }
    }

    [RelayCommand] private void ChooseFresh() => Mode = "fresh";
    [RelayCommand] private void ChooseAdopt() => Mode = "adopt";

    [RelayCommand]
    private void PickFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = IsAdopt ? "Select your existing DokiDex folder (contains doki.ps1)" : "Choose where to install DokiDex",
        };
        if (Directory.Exists(Home)) dlg.InitialDirectory = Home;
        if (dlg.ShowDialog() == true) Home = dlg.FolderName;
    }

    [RelayCommand] private void RecheckPrereqs() { foreach (var r in PrereqRows) r.Refresh(); }

    [RelayCommand]
    private async Task InstallPrereq(PrereqRow? row)
    {
        if (row == null) return;
        if (!row.CanWinget) { if (row.HasUrl) OpenUrl(row.Def.Url!); return; }
        row.Busy = true;
        await Task.Run(() => Prereqs.TryWingetInstall(row.Def)).ConfigureAwait(true);
        row.Busy = false;
        row.Refresh();
    }

    [RelayCommand]
    private void Adopt()
    {
        if (!InstallLocator.IsValidHome(Home)) { StatusLine = "That folder has no doki.ps1 — pick the DokiDex folder itself."; return; }
        Persist(managed: false);
        CloseRequested?.Invoke(true);
    }

    [RelayCommand]
    private async Task Install()
    {
        if (string.IsNullOrWhiteSpace(Home)) { StatusLine = "Choose an install folder first."; return; }
        if (!HasEnoughSpace(out var spaceMsg)) { StatusLine = spaceMsg; return; }
        Installing = true; Log = ""; StatusLine = "installing…";
        void Append(string line) => _ui.Invoke(() => Log += line + "\n");
        var ok = await Installer.RunAsync(Home, Choice, fresh: true, Append, CancellationToken.None).ConfigureAwait(true);
        Installing = false;
        if (ok) { Persist(managed: true); Done = true; StatusLine = "install complete — launch DokiDex"; }
        else StatusLine = "install failed — see the log above";
    }

    [RelayCommand] private void Launch() => CloseRequested?.Invoke(true);
    [RelayCommand] private void Cancel() => CloseRequested?.Invoke(false);

    // Block a fresh install when the chosen drive measurably can't hold the selected components (+ headroom).
    // If free space can't be measured (UNC / unmounted path), don't false-block — setup would fail loudly anyway.
    private bool HasEnoughSpace(out string message)
    {
        message = "";
        try
        {
            var root = Path.GetPathRoot(Home);
            if (!string.IsNullOrEmpty(root))
            {
                var free = new DriveInfo(root).AvailableFreeSpace;
                if (!InstallPlan.FitsFreeSpace(Choice, free))
                {
                    message = $"Not enough space on {root} — need ≈{InstallPlan.RequiredGb(Choice)} GB, {free / 1_000_000_000} GB free. Free space or pick another drive.";
                    return false;
                }
            }
        }
        catch { }
        return true;
    }

    private void Persist(bool managed)
    {
        var s = AppSettings.Load();
        s.InstallRoot = Home;
        s.InstallManaged = managed;
        if (managed) s.InstalledVersion = Updater.RunningVersion();
        s.Save();
        RepoPaths.Refresh();
    }

    // Default fresh-install home = the fixed drive with the most free space + \DokiDex (the ~100 GB kit needs room).
    private static string DefaultHome()
    {
        try
        {
            DriveInfo? best = null;
            foreach (var d in DriveInfo.GetDrives())
                if (d.DriveType == DriveType.Fixed && d.IsReady && (best == null || d.AvailableFreeSpace > best.AvailableFreeSpace)) best = d;
            if (best != null) return Path.Combine(best.RootDirectory.FullName, "DokiDex");
        }
        catch { }
        return @"C:\DokiDex";
    }

    private static void OpenUrl(string url)
    { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { } }
}
