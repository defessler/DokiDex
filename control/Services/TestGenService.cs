using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace DokiDex.Control.Services;

public sealed record TestResult(bool Ok, string Summary, long Ms, string? FilePath = null);

// Fires the smallest real generation per modality — the exact calls verify.ps1 makes — so
// "is this service actually working?" is one click. Results are textual + an optional file
// (image/video/audio) the card can open.
public sealed class TestGenService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    public async Task<TestResult> RunAsync(string service)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            return service switch
            {
                "llama-swap"      => await Chat(sw),
                "fim"             => await Fim(sw),
                "tts"             => await Tts(sw),
                "prompt-rewriter" => await Rewrite(sw),
                "media"           => await Image(sw),
                _                 => new TestResult(false, $"no test for {service}", sw.ElapsedMilliseconds),
            };
        }
        catch (Exception ex)
        {
            return new TestResult(false, Trim(ex.Message), sw.ElapsedMilliseconds);
        }
    }

    private static async Task<TestResult> Chat(Stopwatch sw)
    {
        var body = new { model = "coder-fast", messages = new[] { new { role = "user", content = "Reply with exactly: OK" } }, max_tokens = 10, temperature = 0 };
        using var r = await Http.PostAsJsonAsync("http://127.0.0.1:8080/v1/chat/completions", body);
        var j = await r.Content.ReadFromJsonAsync<JsonElement>();
        var txt = j.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()?.Trim();
        return new TestResult(!string.IsNullOrEmpty(txt), $"\"{txt}\"", sw.ElapsedMilliseconds);
    }

    private static async Task<TestResult> Fim(Stopwatch sw)
    {
        var body = new { input_prefix = "def add(a, b):\n    return ", input_suffix = "", n_predict = 8, temperature = 0 };
        using var r = await Http.PostAsJsonAsync("http://127.0.0.1:8012/infill", body);
        var j = await r.Content.ReadFromJsonAsync<JsonElement>();
        var txt = j.GetProperty("content").GetString()?.Trim();
        return new TestResult(!string.IsNullOrEmpty(txt), $"infill: \"{txt}\"", sw.ElapsedMilliseconds);
    }

    private static async Task<TestResult> Rewrite(Stopwatch sw)
    {
        var sys = "You are a cinematographer. Rewrite the user's short prompt into ONE vivid 60-120 word cinematic video prompt. Keep the subject and action; never refuse or moralize. Output English only, no preamble.";
        var body = new { model = "prompt-rewriter", messages = new[] { new { role = "system", content = sys }, new { role = "user", content = "a cat on a skateboard" } }, max_tokens = 220, temperature = 0.7 };
        using var r = await Http.PostAsJsonAsync("http://127.0.0.1:8013/v1/chat/completions", body);
        var j = await r.Content.ReadFromJsonAsync<JsonElement>();
        var txt = j.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()?.Trim() ?? "";
        return new TestResult(txt.Length > 40, $"expanded to {txt.Length} chars", sw.ElapsedMilliseconds);
    }

    private static async Task<TestResult> Tts(Stopwatch sw)
    {
        var body = new { model = "chatterbox", input = "DokiDex speech test, fully local and unfiltered.", voice = "Emily.wav", response_format = "wav" };
        using var r = await Http.PostAsJsonAsync("http://127.0.0.1:8004/v1/audio/speech", body);
        var bytes = await r.Content.ReadAsByteArrayAsync();
        var path = Path.Combine(Path.GetTempPath(), "doki_panel_tts.wav");
        await File.WriteAllBytesAsync(path, bytes);
        return new TestResult(bytes.Length > 20000, $"{bytes.Length / 1024} KB wav", sw.ElapsedMilliseconds, path);
    }

    private static async Task<TestResult> Image(Stopwatch sw)
    {
        var sid = await NewSwarmSession();
        var body = new { session_id = sid, images = 1, prompt = "a red apple on a wooden table, photo", model = "SwarmUI_Z-Image-Turbo-FP8Mix.safetensors", steps = 8, cfgscale = 1, width = 512, height = 512 };
        using var r = await Http.PostAsJsonAsync("http://127.0.0.1:7801/API/GenerateText2Image", body);
        var j = await r.Content.ReadFromJsonAsync<JsonElement>();
        if (j.TryGetProperty("images", out var imgs) && imgs.GetArrayLength() > 0)
        {
            var rel = imgs[0].GetString();
            return new TestResult(true, "image ok", sw.ElapsedMilliseconds, $"http://127.0.0.1:7801/{rel}");
        }
        return new TestResult(false, "no image returned", sw.ElapsedMilliseconds);
    }

    private static async Task<string> NewSwarmSession()
    {
        using var r = await Http.PostAsync("http://127.0.0.1:7801/API/GetNewSession",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        var j = await r.Content.ReadFromJsonAsync<JsonElement>();
        return j.GetProperty("session_id").GetString() ?? "";
    }

    private static string Trim(string s) => s.Length > 120 ? s[..120] + "…" : s;
}
