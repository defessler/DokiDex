using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.AspNetCore.Builder;

namespace DokiDex.Control.Views;

// The web-studio launcher (the "app sets up a web server" entry, `--studio`). Hosts the studio web server
// IN-PROCESS (DokiDex.Web.StudioHost via Kestrel on 127.0.0.1), waits for /api/health, opens the browser,
// and shows a small status window with Open / Stop. Closing the window stops the server. Code-only (no XAML)
// to keep the surface minimal. In-process => a release ships ONE self-contained exe (no second process to
// co-publish, find, or kill).
public sealed class StudioLauncherWindow : Window
{
    private const int Port = DokiDex.Web.StudioHost.DefaultPort;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };
    private readonly TextBlock _status;
    private WebApplication? _host;

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
        Closed += (_, _) => StopHost();
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
        if (!await HealthyAsync())   // not already running (e.g. a standalone dev host on the same port)
        {
            try { _host = await DokiDex.Web.StudioHost.StartInProcessAsync(Port); }
            catch (Exception ex) { _status.Text = "could not start the web host: " + ex.Message; return; }
        }
        for (int i = 0; i < 40 && !await HealthyAsync(); i++) await Task.Delay(250);
        if (await HealthyAsync()) { _status.Text = $"running at {Url()}"; OpenBrowser(); }
        else _status.Text = "the web host did not come up — see %LocalAppData%\\dokidex.";
    }

    private static string Url() => $"http://127.0.0.1:{Port}";
    private static async Task<bool> HealthyAsync()
    { try { var r = await Http.GetAsync($"{Url()}/api/health"); return r.IsSuccessStatusCode; } catch { return false; } }
    private void OpenBrowser() { try { Process.Start(new ProcessStartInfo(Url()) { UseShellExecute = true })?.Dispose(); } catch { } }
    private void StopHost() { try { _ = _host?.DisposeAsync(); } catch { } }
}
