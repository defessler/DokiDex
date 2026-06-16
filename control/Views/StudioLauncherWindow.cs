using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DokiDex.Control.Views;

// The web-studio launcher (the "app sets up a web server" entry, `--studio`). Starts the DokiDex.Web host as
// a child process, waits for it to come up, opens the browser, and shows a small status window with
// Open / Stop. Closing the window stops the web host. Code-only (no XAML) to keep the surface minimal.
// NOTE: finds DokiDex.Web.exe next to the app (or a ./web subdir) — production packaging must co-publish it
// (or fold the host in-process); until then this works from a build/publish where both exes sit together.
public sealed class StudioLauncherWindow : Window
{
    private const int Port = 5111;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };
    private readonly TextBlock _status;
    private Process? _web;

    public StudioLauncherWindow()
    {
        Title = "DokiDex Studio";
        Width = 400; Height = 190;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.CanMinimize;
        Background = Rgb(0x0A, 0x0E, 0x14);

        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "DokiDex Studio", FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = Rgb(0xE8, 0xC7, 0x7A), Margin = new Thickness(0, 0, 0, 8) });
        _status = new TextBlock { Text = "starting…", Foreground = Rgb(0x7E, 0x8C, 0x99), FontSize = 12, Margin = new Thickness(0, 0, 0, 18), TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(_status);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(Button("Open in browser", (_, _) => OpenBrowser()));
        row.Children.Add(Button("Stop", (_, _) => Close()));
        panel.Children.Add(row);
        Content = panel;

        Loaded += async (_, _) => await StartAsync();
        Closed += (_, _) => StopWeb();
    }

    private static SolidColorBrush Rgb(byte r, byte g, byte b) => new(Color.FromRgb(r, g, b));

    private Button Button(string text, RoutedEventHandler onClick)
    {
        var b = new Button
        {
            Content = text, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 6, 12, 6),
            Background = Rgb(0x12, 0x18, 0x26), Foreground = Rgb(0xE6, 0xEE, 0xF6),
            BorderBrush = Rgb(0x24, 0x31, 0x40), Cursor = Cursors.Hand,
        };
        b.Click += onClick;
        return b;
    }

    private async Task StartAsync()
    {
        var exe = WebExePath();
        if (exe is null) { _status.Text = "DokiDex.Web.exe not found next to the app (packaging pending)."; return; }
        if (!await HealthyAsync())   // not already running from a prior launch
        {
            try { _web = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(exe)! }); }
            catch (Exception ex) { _status.Text = "could not start the web host: " + ex.Message; return; }
        }
        for (int i = 0; i < 40 && !await HealthyAsync(); i++) await Task.Delay(500);
        if (await HealthyAsync()) { _status.Text = $"running at {Url()}"; OpenBrowser(); }
        else _status.Text = "the web host did not come up — see %LocalAppData%\\dokidex.";
    }

    private static string? WebExePath()
    {
        var dir = Services.RepoPaths.ExeDir;
        foreach (var c in new[] { Path.Combine(dir, "DokiDex.Web.exe"), Path.Combine(dir, "web", "DokiDex.Web.exe") })
            if (File.Exists(c)) return c;
        return null;
    }

    private static string Url() => $"http://127.0.0.1:{Port}";
    private static async Task<bool> HealthyAsync()
    { try { var r = await Http.GetAsync($"{Url()}/api/health"); return r.IsSuccessStatusCode; } catch { return false; } }
    private void OpenBrowser() { try { Process.Start(new ProcessStartInfo(Url()) { UseShellExecute = true })?.Dispose(); } catch { } }
    private void StopWeb() { try { if (_web is { HasExited: false }) _web.Kill(true); } catch { } }
}
