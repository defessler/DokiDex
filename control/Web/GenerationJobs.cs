using System.Collections.Concurrent;
using System.IO;
using System.Text;
using DokiDex.Control.Services;
using Microsoft.AspNetCore.SignalR;

namespace DokiDex.Web;

// A browser-submitted generation request. Maps onto the tested GenRequest/GenCli recipe contract so the
// web path and the CLI path stay 1:1 (single source of truth).
public sealed record GenSubmit(
    string Prompt, string? Kind = "image",
    bool Fast = false, bool Upscale = false, bool Refine = false,
    bool Face = false, bool Realism = false, bool Raw = false, string? InitImage = null,
    int Seed = -1, int Count = 1, double Strength = -1, string? MaskImage = null, string? Aspect = null,
    string? Lyrics = null, int Duration = 0, int Bpm = 0, string? Lora = null, string? Negative = null,
    string? Upscaler = null);

// One generation job, tracked in memory for the session.
public sealed class GenJob
{
    public required string Id { get; init; }
    public required string Prompt { get; init; }
    public required string Kind { get; init; }
    public string Status { get; set; } = "queued";   // queued | running | done | failed
    public double Progress { get; set; }              // 0..1, fed by the GenerateText2ImageWS bridge
    public string? Preview { get; set; }              // in-flight preview (data: URL) while running
    public string? Message { get; set; }
    public string? ArtifactPath { get; set; }
    public bool HasArtifact => !string.IsNullOrEmpty(ArtifactPath) && File.Exists(ArtifactPath);

    public object ToDto() => new
    {
        id = Id, prompt = Prompt, kind = Kind, status = Status, progress = Progress, message = Message,
        // scoped media URL by job id only (never a client-supplied path) -> no traversal
        mediaUrl = Status == "done" && HasArtifact ? $"/api/media/{Id}" : null,
        // only the running job carries a preview (keeps the /api/jobs payload small)
        preview = Status == "running" ? Preview : null,
    };
}

// In-memory generation queue. Submissions are accepted immediately (queued) so the UI stays responsive; a
// single-flight gate serializes GPU EXECUTION (the 32 GB media group runs one gen at a time). The recipe stays
// single-sourced in doki-gen.ps1 (fetched via `doki gen -BodyOnly`); this host drives SwarmUI's
// GenerateText2ImageWS for live %/preview and downloads the artifact. Cancel = InterruptAll + token cancel.
public sealed class GenerationJobs
{
    private readonly DokiService _doki;
    private readonly IHubContext<StudioHub> _hub;
    private readonly ConcurrentDictionary<string, GenJob> _jobs = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cts = new();
    private readonly SemaphoreSlim _gpu = new(1, 1);
    private int _seq;

    public GenerationJobs(DokiService doki, IHubContext<StudioHub> hub) { _doki = doki; _hub = hub; }

    public GenJob Submit(GenRequest req)
    {
        var job = new GenJob { Id = $"g{Interlocked.Increment(ref _seq):D4}", Prompt = req.Prompt, Kind = req.Kind };
        _jobs[job.Id] = job;
        var cts = new CancellationTokenSource();
        _cts[job.Id] = cts;
        _ = Task.Run(() => RunAsync(job, req, cts.Token));
        return job;
    }

    public GenJob? Get(string id) => _jobs.TryGetValue(id, out var j) ? j : null;
    public IEnumerable<GenJob> Recent(int n = 60) => _jobs.Values.OrderByDescending(j => j.Id).Take(n);

    // Cancel: interrupt SwarmUI's current gen AND cancel our token (stops the WS receive + the gate wait).
    public async Task Cancel(string id)
    {
        if (_cts.TryGetValue(id, out var cts)) { try { cts.Cancel(); } catch { } }
        await SwarmGen.InterruptAsync().ConfigureAwait(false);
    }

    private async Task RunAsync(GenJob job, GenRequest req, CancellationToken ct)
    {
        try
        {
            await _gpu.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                job.Status = "running";
                await Push(job).ConfigureAwait(false);

                var body = await _doki.GetGenBodyAsync(req, ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(body)) { job.Status = "failed"; job.Message = "could not build the generation request"; return; }

                var outPath = _doki.NewGenOutPath(req.Kind);
                var outcome = await SwarmGen.RunAsync(body!, outPath, p =>
                {
                    job.Progress = p.Overall > 1.0 ? p.Overall / 100.0 : p.Overall;
                    if (!string.IsNullOrEmpty(p.PreviewDataUrl)) job.Preview = p.PreviewDataUrl;
                    _ = Push(job);
                }, ct).ConfigureAwait(false);

                if (outcome.Ok)
                {
                    job.ArtifactPath = outcome.ArtifactPath; job.Progress = 1; job.Preview = null; job.Status = "done"; job.Message = "done";
                    GalleryService.WriteSidecar(outcome.ArtifactPath!, job.Id, job.Kind, job.Prompt);   // persist for the Library
                }
                else { job.Status = "failed"; job.Message = StripAnsi(outcome.Message); }
            }
            finally { _gpu.Release(); }
        }
        catch (OperationCanceledException) { job.Status = "failed"; job.Message = "cancelled"; }
        catch (Exception ex) { job.Status = "failed"; job.Message = StripAnsi(ex.Message); }
        finally { _cts.TryRemove(job.Id, out _); await Push(job).ConfigureAwait(false); }
    }

    private Task Push(GenJob job)
    {
        try { return _hub.Clients.All.SendAsync("job", job.ToDto()); } catch { return Task.CompletedTask; }
    }

    // doki.ps1 emits ANSI-colored errors. Drop each ESC..terminating-letter CSI sequence (regex-free to dodge
    // string-escaping pitfalls) so the card shows a clean message. 27 = the ESC control byte.
    private static string StripAnsi(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        bool inEsc = false;
        foreach (var ch in s)
        {
            if (inEsc) { if (char.IsLetter(ch)) inEsc = false; continue; }
            if (ch == (char)27) { inEsc = true; continue; }
            sb.Append(ch);
        }
        return sb.ToString().Trim();
    }
}
