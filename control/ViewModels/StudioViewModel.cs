using System;
using System.Collections.Generic;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DokiDex.Control.Services;

namespace DokiDex.Control.ViewModels;

// The "Studio" page — a visual surface over `doki gen`: describe -> pick a kind -> Generate -> preview ->
// Remix (the Sora-style loop, per docs/dokigen-studio-design.md). PHASE 1 here is the shell: the bound
// state + a design-sample so --design renders it populated with no backend/GPU. The live Generate/Remix
// wiring (DokiService.RunGenAsync) is Phase 2; the command stubs keep the buttons bindable until then.
public partial class StudioViewModel : ObservableObject
{
    private readonly DokiService _doki;
    public StudioViewModel(DokiService doki) => _doki = doki;

    // mirrors `doki gen`'s kinds (serving/doki-gen.ps1 Resolve-GenKind); the picker binds SelectedKind.
    public List<string> Kinds { get; } = new() { "image", "video", "music", "edit", "i2v", "foley" };

    [ObservableProperty] private string _selectedKind = "image";
    [ObservableProperty] private string _promptText = "";
    [ObservableProperty] private bool _fast;
    [ObservableProperty] private bool _upscale;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(CanGenerate))] private bool _isGenerating;
    [ObservableProperty] private string _statusText = "describe something to generate";
    // GPU in media mode? gen needs it (the panel keeps LLM/media mutually exclusive); the guard banner shows
    // when false. Updated from MainViewModel each poll. Wiring the one-click switch is Phase 2.
    [ObservableProperty][NotifyPropertyChangedFor(nameof(CanGenerate))] private bool _mediaActive;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(HasResult))] private string? _resultPath;
    [ObservableProperty] private string? _resultCaption;
    [ObservableProperty] private ImageSource? _resultPreview;   // the inline image (design sample now; live artifact in Phase 2)

    public bool HasResult => !string.IsNullOrEmpty(ResultPath);
    public bool CanGenerate => MediaActive && !IsGenerating;

    // Phase 2 stubs (kept bindable so the shell renders + the buttons exist). The live path will shell
    // `doki gen "<prompt>" -<kind> [-Fast] [-Upscale] -Out <temp> -NoOpen` via DokiService.RunGenAsync.
    [RelayCommand] private void Generate() { }
    [RelayCommand] private void Remix() { }
    [RelayCommand] private void OpenResult() { if (HasResult && ResultPath != "(sample)") _doki.OpenArtifact(ResultPath!); }

    // design/sample state so `--design` / `--render` shows the Studio populated (no backend, no GPU).
    // The prompt is written to honestly match the bundled abstract placeholder (studio-sample.png).
    internal void LoadDesignSample()
    {
        PromptText = "a coil of living light — teal to molten gold, drifting embers, volumetric haze through rain";
        SelectedKind = "image";
        MediaActive = true;
        IsGenerating = false;
        StatusText = "done · Z-Image Turbo · 1024² in 4.2s";
        ResultPath = "(sample)";
        ResultCaption = "Z-Image Turbo · 1024²";
        ResultPreview = LoadBundled("assets/studio-sample.png");
    }

    // Load an embedded image resource (pack://). Guarded: returns null when no WPF Application is running
    // (e.g. headless unit tests) so the VM stays constructible — HasResult still holds via ResultPath.
    private static ImageSource? LoadBundled(string rel)
    {
        try { return new BitmapImage(new Uri($"pack://application:,,,/{rel}")); }
        catch { return null; }
    }
}
