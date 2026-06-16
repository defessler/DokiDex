using System.Collections.Concurrent;
using System.Text;
using DokiDex.Control.Services;
using Microsoft.AspNetCore.SignalR;

namespace DokiDex.Web;

// A browser-submitted generation request. Maps onto the tested GenRequest/GenCli recipe contract so the
// web path and the CLI path stay 1:1 (single source of truth).
public sealed record GenSubmit(
    string Prompt, string? Kind = "image",
    bool Fast = false, bool Upscale = false, bool Refine = false,
    bool Face = false, bool Realism = false, bool Raw = false, string? InitImage = null);

// One generation job, tracked in memory for the session.
public sealed class GenJob
{
    public required string Id { get; init; }
    public required string Prompt { get; init; }
    public required string Kind { get; init; }
    public string Status { get; set; } = "queued";   // queued | running | done | failed
    public double Progress { get; set; }              // 0..1 (indeterminate until the P1b WS bridge lands)
    public string? Message { get; set; }
    public string? ArtifactPath { get; set; }
    public bool HasArtifact => !string.IsNullOrEmpty(ArtifactPath) && File.Exists(ArtifactPath);

    public object ToDto() => new
    {
        id = Id, prompt = Prompt, kind = Kind, status = Status, progress = Progress, message = Message,
        // scoped media URL by job id only (never a client-supplied path) -> no traversal
        mediaUrl = Status == "done" && HasArtifact ? $"/api/media/{Id}" : null,
    };
}

// In-memory generation queue. Submissions are accepted immediately (queued) so the UI stays responsive; a
// single-flight gate serializes GPU EXECUTION (the 32 GB media group runs one gen at a time). Today it drives
// the tested CLI path (DokiService.RunGenAsync -> `doki gen`); P1b swaps in a GenerateText2ImageWS bridge for
// live %/preview while keeping this queue + the recipe contract intact.
public sealed class GenerationJobs
{
    private readonly DokiService _doki;
    private readonly IHubContext<StudioHub> _hub;
    private readonly ConcurrentDictionary<string, GenJob> _jobs = new();
    private readonly SemaphoreSlim _gpu = new(1, 1);
    private int _seq;

    public GenerationJobs(DokiService doki, IHubContext<StudioHub> hub) { _doki = doki; _hub = hub; }

    public GenJob Submit(GenRequest req)
    {
        var job = new GenJob { Id = $"g{Interlocked.Increment(ref _seq):D4}", Prompt = req.Prompt, Kind = req.Kind };
        _jobs[job.Id] = job;
        _ = Task.Run(() => RunAsync(job, req));
        return job;
    }

    public GenJob? Get(string id) => _jobs.TryGetValue(id, out var j) ? j : null;
    public IEnumerable<GenJob> Recent(int n = 60) => _jobs.Values.OrderByDescending(j => j.Id).Take(n);

    private async Task RunAsync(GenJob job, GenRequest req)
    {
        await _gpu.WaitAsync().ConfigureAwait(false);
        try
        {
            job.Status = "running";
            await Push(job).ConfigureAwait(false);
            var outPath = _doki.NewGenOutPath(req.Kind);
            var res = await _doki.RunGenAsync(req with { OutPath = outPath }).ConfigureAwait(false);
            if (res.Ok) { job.ArtifactPath = res.OutPath; job.Progress = 1; job.Status = "done"; job.Message = "done"; }
            else { job.Status = "failed"; job.Message = StripAnsi(res.Message); }
        }
        catch (Exception ex) { job.Status = "failed"; job.Message = StripAnsi(ex.Message); }
        finally { _gpu.Release(); await Push(job).ConfigureAwait(false); }
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
