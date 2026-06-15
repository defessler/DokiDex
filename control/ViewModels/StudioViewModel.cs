using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DokiDex.Control.Services;

namespace DokiDex.Control.ViewModels;

// The "Studio" page — a visual surface over `doki gen`: describe -> pick a kind -> Generate -> preview inline
// -> Remix (the Sora-style loop, per docs/dokigen-studio-design.md). Generate/Remix shell DokiService.
// RunGenAsync; the arg-building is pure + unit-tested (GenCliTests) and the live run needs media mode (the
// guard gates it; it's smoke-checked end-to-end in a card session). --design renders it populated with no
// backend via LoadDesignSample.
public partial class StudioViewModel : ObservableObject
{
    private readonly DokiService _doki;
    public StudioViewModel(DokiService doki) => _doki = doki;

    // Set by MainViewModel: lets the media-mode guard flip the GPU to media with one click (reuses the
    // panel's SwitchMode + its eviction confirm). Null in unit tests / when hosted standalone.
    public Action? SwitchToMediaRequested { get; set; }

    // mirrors `doki gen`'s kinds (serving/doki-gen.ps1 Resolve-GenKind); the picker binds SelectedKind.
    public IReadOnlyList<string> Kinds { get; } = GenRequest.Kinds;

    [ObservableProperty][NotifyPropertyChangedFor(nameof(ShowInitImage))] private string _selectedKind = "image";
    [ObservableProperty] private string _promptText = "";
    [ObservableProperty] private bool _fast;
    [ObservableProperty] private bool _upscale;
    [ObservableProperty] private bool _raw;                 // skip the :8013 prompt rewriter
    [ObservableProperty] private string? _initImagePath;    // -InitImage (required for edit; optional for i2v)
    [ObservableProperty][NotifyPropertyChangedFor(nameof(CanGenerate))][NotifyPropertyChangedFor(nameof(ShowEmpty))] private bool _isGenerating;
    [ObservableProperty] private string _statusText = "describe something to generate";
    // GPU in media mode? gen needs it (the panel keeps LLM/media mutually exclusive); the guard banner shows
    // when false. Updated from MainViewModel each poll. Wiring the one-click switch is Phase 2.
    [ObservableProperty][NotifyPropertyChangedFor(nameof(CanGenerate))] private bool _mediaActive;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(HasResult))][NotifyPropertyChangedFor(nameof(ShowEmpty))] private string? _resultPath;
    [ObservableProperty] private string? _resultCaption;
    [ObservableProperty] private ImageSource? _resultPreview;   // the inline image (design sample now; live artifact in Phase 2)

    public bool HasResult => !string.IsNullOrEmpty(ResultPath);
    public bool CanGenerate => MediaActive && !IsGenerating;
    public bool ShowEmpty => !HasResult && !IsGenerating;           // the inviting canvas: idle + nothing yet
    public bool ShowInitImage => SelectedKind is "edit" or "i2v";   // these kinds can take a source still

    // Generate / Remix: shell `doki gen` and show the artifact inline. Remix just re-runs the same request —
    // doki re-seeds every call, so that's the Sora-style iterate-to-good loop. CanGenerate (media mode + not
    // already running) gates both; empty-prompt / missing-init-image cases report in the status line.
    [RelayCommand] private async Task Generate() => await RunAsync();
    [RelayCommand] private async Task Remix() => await RunAsync();

    [RelayCommand]
    private void PickInitImage()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "pick a source image",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.webp;*.gif|All files|*.*",
        };
        if (dlg.ShowDialog() == true) InitImagePath = dlg.FileName;
    }

    [RelayCommand]
    private void OpenResult()
    {
        if (HasResult && ResultPath != "(sample)") _doki.OpenLocalMedia(ResultPath!);
    }

    // the guard banner's one-click "Switch to MEDIA" — hands off to MainViewModel.SwitchMode("media").
    [RelayCommand] private void SwitchToMedia() => SwitchToMediaRequested?.Invoke();

    private async Task RunAsync()
    {
        if (!CanGenerate) return;
        var prompt = (PromptText ?? "").Trim();
        if (prompt.Length == 0) { StatusText = "describe something to generate first"; return; }
        var kind = SelectedKind;
        if (GenRequest.RequiresInitImage(kind) && string.IsNullOrWhiteSpace(InitImagePath))
        { StatusText = $"{kind} needs a source image — pick one first"; return; }

        var req = new GenRequest(prompt, kind, Fast, Upscale, Raw,
                                 string.IsNullOrWhiteSpace(InitImagePath) ? null : InitImagePath,
                                 _doki.NewGenOutPath(kind));
        IsGenerating = true;
        StatusText = $"generating {kind}…  (needs media mode)";
        try
        {
            var result = await _doki.RunGenAsync(req);   // resumes on the UI thread (command context)
            if (result.Ok)
            {
                ResultCaption = CaptionFor(kind);
                ResultPreview = GenRequest.IsInlineImageKind(kind) ? LoadFile(result.OutPath) : null;
                ResultPath = result.OutPath;             // set LAST so HasResult flips after the preview is ready
                StatusText = $"done · {ResultCaption}";
            }
            else { StatusText = $"⚠ {result.Message}"; }
        }
        finally { IsGenerating = false; }
    }

    private static string CaptionFor(string kind) => kind switch
    {
        "image" => "Z-Image Turbo · 1024²",
        "edit"  => "Qwen-Image-Edit",
        "video" => "Wan 2.2 · 832×480",
        "i2v"   => "Z-Image → Wan I2V",
        "music" => "ACE-Step",
        "foley" => "Wan + Foley",
        _       => kind,
    };

    // Load a file into a frozen BitmapImage (OnLoad = don't lock the file, so a Remix can overwrite it).
    private static ImageSource? LoadFile(string path)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    // design/sample state so `--design` / `--render --page studio[:variant]` shows the Studio in any of its
    // states with no backend/GPU. variant ∈ result (default) | empty | generating | guard — one per UI state
    // the design doc promises to snapshot. The prompt matches the bundled placeholder (studio-sample.png).
    internal void LoadDesignSample(string variant = "result")
    {
        SelectedKind = "image";
        Fast = Upscale = Raw = false;
        PromptText = "a coil of living light — teal to molten gold, drifting embers, volumetric haze through rain";
        ResultCaption = "Z-Image Turbo · 1024²";
        switch (variant)
        {
            case "empty":      // media on, nothing generated yet -> the inviting empty canvas
                MediaActive = true; IsGenerating = false; PromptText = "";
                StatusText = "describe something to generate";
                ResultPath = null; ResultPreview = null;
                break;
            case "generating": // a run in flight -> the generating overlay over the canvas
                MediaActive = true; IsGenerating = true;
                StatusText = "generating image…  (needs media mode)";
                ResultPath = null; ResultPreview = null;
                break;
            case "guard":      // GPU not in media mode -> the one-click switch banner
                MediaActive = false; IsGenerating = false;
                StatusText = "media mode is off";
                ResultPath = null; ResultPreview = null;
                break;
            default:           // "result": the happy path with an inline preview
                MediaActive = true; IsGenerating = false;
                StatusText = "done · Z-Image Turbo · 1024² in 4.2s";
                ResultPath = "(sample)";
                ResultPreview = LoadBundled("assets/studio-sample.png");
                break;
        }
    }

    // Load an embedded image resource (pack://). Guarded: returns null when no WPF Application is running
    // (e.g. headless unit tests) so the VM stays constructible — HasResult still holds via ResultPath.
    private static ImageSource? LoadBundled(string rel)
    {
        try { return new BitmapImage(new Uri($"pack://application:,,,/{rel}")); }
        catch { return null; }
    }
}
