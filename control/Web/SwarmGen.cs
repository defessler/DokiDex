using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DokiDex.Web;

// Drives ONE SwarmUI generation over GenerateText2ImageWS for live progress, using a body produced by
// `doki gen -BodyOnly` (recipe single-sourced in PowerShell). Reports progress/preview via a callback and
// downloads the final artifact to outPath. Cancellation issues InterruptAll. The untyped SwarmUI WS frames
// are parsed defensively (keepalives / unknown frames are ignored).
public sealed class SwarmGen
{
    private const string Base = "http://127.0.0.1:7801";
    private const string WsUrl = "ws://127.0.0.1:7801/API/GenerateText2ImageWS";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    public sealed record Progress(double Overall, string? PreviewDataUrl);
    public sealed record Outcome(bool Ok, string? ArtifactPath, string? Message);

    public static async Task<Outcome> RunAsync(string bodyJson, string outPath, Action<Progress> onProgress, CancellationToken ct)
    {
        // 1. New session (also our reachability check — SwarmUI must be in media mode).
        string sessionId;
        try
        {
            using var s = await Http.PostAsync($"{Base}/API/GetNewSession", Json("{}"), ct).ConfigureAwait(false);
            s.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await s.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            sessionId = doc.RootElement.GetProperty("session_id").GetString() ?? "";
        }
        catch (OperationCanceledException) { return new Outcome(false, null, "cancelled"); }
        catch (Exception ex) { return new Outcome(false, null, $"SwarmUI not reachable at {Base} — start media mode first ({ex.Message})"); }

        // 2. Inject the real session into the BodyOnly body.
        JsonObject body;
        try { body = JsonNode.Parse(bodyJson)!.AsObject(); body["session_id"] = sessionId; }
        catch (Exception ex) { return new Outcome(false, null, $"bad generation body: {ex.Message}"); }

        // 3. Stream the gen over the WebSocket.
        using var ws = new ClientWebSocket();
        try { await ws.ConnectAsync(new Uri(WsUrl), ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return new Outcome(false, null, "cancelled"); }
        catch (Exception ex) { return new Outcome(false, null, $"WebSocket connect failed: {ex.Message}"); }

        string? artifact = null, error = null;
        try
        {
            await ws.SendAsync(Encoding.UTF8.GetBytes(body.ToJsonString()), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
            var buf = new byte[64 * 1024];
            var sb = new StringBuilder();
            while (ws.State == WebSocketState.Open)
            {
                var r = await ws.ReceiveAsync(buf, ct).ConfigureAwait(false);
                if (r.MessageType == WebSocketMessageType.Close) break;
                sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
                if (!r.EndOfMessage) continue;
                var frame = sb.ToString(); sb.Clear();
                if (TryHandle(frame, onProgress, ref artifact, ref error)) break;   // true = terminal (artifact/error)
            }
        }
        catch (OperationCanceledException) { return new Outcome(false, null, "cancelled"); }
        catch (Exception ex) { error ??= ex.Message; }
        finally { try { if (ws.State == WebSocketState.Open) await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).ConfigureAwait(false); } catch { } }

        if (artifact is null) return new Outcome(false, null, error ?? "no artifact returned");

        // 4. Download the artifact to the app-owned output path.
        var url = artifact.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? artifact
                : $"{Base}/{(artifact.StartsWith("View/", StringComparison.OrdinalIgnoreCase) ? artifact : "View/" + artifact)}";
        try
        {
            var bytes = await Http.GetByteArrayAsync(url, ct).ConfigureAwait(false);
            await File.WriteAllBytesAsync(outPath, bytes, ct).ConfigureAwait(false);
            return new Outcome(true, outPath, "done");
        }
        catch (Exception ex) { return new Outcome(false, null, $"artifact download failed: {ex.Message}"); }
    }

    // Parse one WS frame. Returns true when the gen is terminal (artifact found or error).
    internal static bool TryHandle(string frame, Action<Progress> onProgress, ref string? artifact, ref string? error)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(frame); } catch { return false; }   // keepalive / non-JSON
        if (node is null) return false;

        if (node["gen_progress"] is JsonNode gp)
        {
            double overall = TryDouble(gp["overall_percent"]);
            string? preview = gp["preview"]?.GetValue<string>();
            onProgress(new Progress(overall, preview));
        }
        if (node["error"] is JsonNode err) { error = err.ToString(); return true; }
        if (node["image"]?.GetValue<string>() is { Length: > 0 } one) { artifact = one; return true; }
        if (node["images"] is JsonArray arr && arr.Count > 0 && arr[0]?.GetValue<string>() is { Length: > 0 } first) { artifact = first; return true; }
        return false;
    }

    private static double TryDouble(JsonNode? n)
    {
        try { return n?.GetValue<double>() ?? 0; } catch { return 0; }
    }

    public static async Task InterruptAsync()
    {
        try { using var _ = await Http.PostAsync($"{Base}/API/InterruptAll", Json("{}"), CancellationToken.None).ConfigureAwait(false); } catch { }
    }

    private static StringContent Json(string s) => new(s, Encoding.UTF8, "application/json");
}
