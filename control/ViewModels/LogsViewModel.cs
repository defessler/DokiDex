using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DokiCode.Control.ViewModels;

public sealed class LogLine
{
    public string Service { get; init; } = "";
    public string Time { get; init; } = "";
    public string Text { get; init; } = "";
    public string Severity { get; init; } = "info";   // error|warn|good|info
}

// Tails .run\<name>.log[.err] by tracking a byte offset per file (simple + reliable; no
// FileSystemWatcher races). Tabs + regex/substring filter + pause + stderr toggle.
public partial class LogsViewModel : ObservableObject, IDisposable
{
    private const int MaxLines = 2500;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<string, long> _offsets = new();   // file path -> bytes read
    private readonly List<string> _services = new();

    public ObservableCollection<LogLine> Lines { get; } = new();
    public ObservableCollection<string> Tabs { get; } = new() { "All" };
    public ICollectionView View { get; }

    [ObservableProperty] private string _selectedTab = "All";
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private bool _paused;
    [ObservableProperty] private bool _includeStderr = true;

    public event Action? LineAppended;

    public LogsViewModel()
    {
        View = CollectionViewSource.GetDefaultView(Lines);
        View.Filter = o => Matches((LogLine)o);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Poll();
        _timer.Start();
    }

    partial void OnSelectedTabChanged(string value) => View.Refresh();
    partial void OnFilterChanged(string value) => View.Refresh();
    partial void OnIncludeStderrChanged(bool value) => View.Refresh();

    public void SyncServices(IEnumerable<string> names)
    {
        foreach (var n in names)
        {
            if (_services.Contains(n)) continue;
            _services.Add(n);
            Tabs.Add(n);
        }
    }

    private bool Matches(LogLine l)
    {
        if (SelectedTab != "All" && l.Service != SelectedTab) return false;
        if (!IncludeStderr && l.Text.StartsWith("[stderr]")) return false;
        if (!string.IsNullOrWhiteSpace(Filter))
        {
            try { if (!System.Text.RegularExpressions.Regex.IsMatch(l.Text, Filter, System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return false; }
            catch { if (l.Text.IndexOf(Filter, StringComparison.OrdinalIgnoreCase) < 0) return false; }
        }
        return true;
    }

    private void Poll()
    {
        if (Paused) return;
        var dir = Services.RepoPaths.RunDir;
        if (!Directory.Exists(dir)) return;
        bool added = false;
        foreach (var svc in _services.ToArray())
        {
            added |= ReadNew(Path.Combine(dir, svc + ".log"), svc, isErr: false);
            if (IncludeStderr) added |= ReadNew(Path.Combine(dir, svc + ".log.err"), svc, isErr: true);
        }
        if (added)
        {
            while (Lines.Count > MaxLines) Lines.RemoveAt(0);
            LineAppended?.Invoke();
        }
    }

    private bool ReadNew(string path, string svc, bool isErr)
    {
        try
        {
            if (!File.Exists(path)) return false;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var start = _offsets.TryGetValue(path, out var off) ? off : 0;
            if (fs.Length < start) start = 0;                 // file rotated/truncated
            if (fs.Length == start) return false;
            fs.Seek(start, SeekOrigin.Begin);
            using var sr = new StreamReader(fs);
            var chunk = sr.ReadToEnd();
            _offsets[path] = fs.Length;
            bool any = false;
            foreach (var raw in chunk.Replace("\r", "").Split('\n'))
            {
                if (raw.Length == 0) continue;
                Lines.Add(new LogLine
                {
                    Service = svc,
                    Time = DateTime.Now.ToString("HH:mm:ss"),
                    Text = isErr ? "[stderr] " + raw : raw,
                    Severity = Classify(raw, isErr),
                });
                any = true;
            }
            return any;
        }
        catch { return false; }
    }

    private static string Classify(string text, bool isErr)
    {
        // Colour by CONTENT, not by stream: llama-server (and others) log normally to
        // stderr, so treating stderr as error painted everything red. The "[stderr]"
        // prefix already marks the stream; severity reflects what the line actually says.
        var t = text.ToLowerInvariant();
        if (t.Contains("error") || t.Contains("exception") || t.Contains("traceback") || t.Contains("failed")) return "error";
        if (t.Contains("warn")) return "warn";
        if (t.Contains("ready") || t.Contains("loaded") || t.Contains("swap") || t.Contains("healthy") || t.Contains(" 200 ")) return "good";
        return "info";
    }

    public void Dispose() => _timer.Stop();
}
