using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DokiDex.Control.Services;

namespace DokiDex.Web;

// The attach-a-doc request body (POST /api/chats/{id}/docs): the source FILENAME (.txt/.md) + the document TEXT
// the browser read client-side. Server-side the kb_id is the conversation id (never client-supplied), so there
// is no path/traversal surface — Source is only used as a label + the SafeName-guarded chunk key.
public sealed record DocAttachRequest(string? Source, string? Text);

// doc_search / doc_ingest sidecar — the knowledge-base ("chat with your documents") RAG over a conversation's
// attached docs, via serving/memory-mcp/doc_index.py's `doc_ingest` / `doc_search` subcommands, run one-shot
// through `uv run python ...` exactly like CodeSearch. doc_index.py is stdlib-only (it reuses code_index's pure
// primitives + the same :8090 embed call), so a plain `uv run python <script> ...` needs no extra packages.
//
// HARD runtime deps (DEGRADE, never throw, never hang): the :8090 embed server must be running (a SEPARATE
// always-on CPU server) AND the doc_index.db must hold this conversation's chunks. When either is missing the
// live exec exits non-zero (connection-refused / no rows) and RetrieveAsync returns an EMPTY DocChunk list — so
// ChatPrompt.Build injects NOTHING and plain chat proceeds byte-for-byte unchanged. This is the SAME contract as
// CodeSearch.cs:68-70: the KB never blocks or breaks a reply, it only enriches one when it can.
//
// Pure seams (unit-tested, no process): BuildSearchArgs, BuildIngestArgs, ParseDocJson, ToDocChunks.
public static class DocSearch
{
    private const int RetrieveK = 5;   // top-K chunks pulled per turn (the ChatPrompt.DocsBlock cap re-bounds it)

    // Ingest size cap. A doc this large would chunk into thousands of sequential :8090 embed calls and blow past
    // IngestAsync's 30s timeout, surfacing a MISLEADING "start the embed server" 503 (the server is fine — the
    // doc is just too big). This is the ONLY path ingesting ARBITRARY browser text, so it gets a hard, cheap,
    // tested upper bound that returns a CLEAR message BEFORE the embed loop. ~200k chars ≈ a large book chapter;
    // the doc_index.py MAX_CHUNKS cap is the defensive backstop for a direct CLI call. POST /chats/{id}/docs adds
    // a matching request-body length guard so a multi-MB body is rejected without buffering the whole thing.
    public const int MaxDocChars = 200_000;

    // Request-body ceiling for POST /chats/{id}/docs: MaxDocChars worst-case UTF-8 (~4 bytes/char) + JSON envelope
    // headroom. A body larger than this is rejected by Content-Length up front (a clean 413) so a multi-MB paste
    // never even reaches binding/ingest — the byte-level companion to the char-level ValidateIngest.
    public const long MaxDocBytes = 1_500_000;

    // Multipart upload ceiling for POST /chats/{id}/docs/file: MaxDocBytes (the file payload) + a small margin for
    // the multipart framing (boundaries, the per-part Content-Disposition/Content-Type headers, the 'source' field).
    // Applied as the ReadFormAsync MultipartBodyLengthLimit so a CHUNKED upload (no Content-Length, so the cheap 413
    // pre-check can't fire) — or a forged-low Content-Length — still can't force the whole body to buffer up to
    // Kestrel's 30MB default: ReadFormAsync aborts past this limit and the endpoint returns the 413. ~64KB margin
    // comfortably covers the framing for a single small file part without letting a multi-MB body through.
    public const long MaxUploadBytes = MaxDocBytes + 64 * 1024;

    private static string Script => Path.Combine(RepoPaths.Root, "serving", "memory-mcp", "doc_index.py");

    public static bool Installed => File.Exists(Script);

    // PURE upper-bound gate for one ingest: null when the text is acceptable (at/under MaxDocChars, including
    // null/empty — the empty-text case is the endpoint's own concern), else a CLEAR, user-facing message naming
    // the size and the fix. Checked at the top of IngestAsync (before any process spawn), so an over-large paste
    // fails FAST and HONESTLY instead of timing out the embed loop into a bogus 503. Unit-tested so the bound
    // can't silently drift.
    public static string? ValidateIngest(string? text)
    {
        var len = text?.Length ?? 0;
        return len > MaxDocChars
            ? $"document too large ({len} chars) — split it or attach a smaller file (max {MaxDocChars} chars)."
            : null;
    }

    // PURE raw-bytes upper bound for one BINARY ingest: null when at/under MaxDocBytes (the PRIMARY binary gate —
    // a PDF's bytes exceed its extracted text), else a CLEAR "too large" message. The endpoint also gates on
    // Content-Length (413) before buffering; this is the in-process companion checked at the top of IngestBinAsync.
    public static string? ValidateIngestBytes(byte[]? data)
    {
        var len = data?.LongLength ?? 0;
        return len > MaxDocBytes
            ? $"document too large ({len} bytes) — split it or attach a smaller file (max {MaxDocBytes} bytes)."
            : null;
    }

    // Pure: the argv for `uv run python doc_index.py doc_search KB Q K`. k is clamped to [1,10].
    public static IReadOnlyList<string> BuildSearchArgs(string kbId, string query, int k)
    {
        var n = Math.Clamp(k, 1, 10);
        return new[] { Script, "doc_search", kbId ?? "", query ?? "", n.ToString() };
    }

    // Pure: the argv for `uv run python doc_index.py doc_ingest KB SOURCE` (the document TEXT is piped via STDIN).
    public static IReadOnlyList<string> BuildIngestArgs(string kbId, string source)
        => new[] { Script, "doc_ingest", kbId ?? "", source ?? "" };

    // Pure: the argv for `doc_index.py doc_ingest_bin KB SOURCE` (the raw FILE BYTES are piped via STDIN). The
    // server extracts text by extension (.pdf->pypdf, .docx->python-docx, else utf-8) then reuses the SAME ingest.
    public static IReadOnlyList<string> BuildIngestBinArgs(string kbId, string source)
        => new[] { Script, "doc_ingest_bin", kbId ?? "", source ?? "" };

    // The parser deps the binary path makes available to uv ONLY for doc_ingest_bin (pure-python, lightweight).
    private static readonly string[] ParserWith = { "--with", "pypdf", "--with", "python-docx" };

    // PURE: the FULL `uv` argument list for the txt/md TEXT ingest — `run python <script> doc_ingest …`, with NO
    // `--with` overlay so the stdlib fast path never pays dependency resolution. Locked by a test so the zero-dep
    // invariant can't silently regress. (StartOrNull builds the live invocation from the same pieces.)
    public static IReadOnlyList<string> BuildIngestUvArgs(string kbId, string source)
        => UvArgs(BuildIngestArgs(kbId, source));

    // PURE: the FULL `uv` argument list for the BINARY (.pdf/.docx) ingest — `run --with pypdf --with python-docx
    // python <script> doc_ingest_bin …`. The `--with` overlay sits on the `run` verb (before `python`) so uv
    // resolves+caches the parser env for this path ONLY; the text path above stays clean.
    public static IReadOnlyList<string> BuildIngestBinUvArgs(string kbId, string source)
        => UvArgs(BuildIngestBinArgs(kbId, source));

    // The shared `uv run [--with …] python <args>` assembly: the `--with` overlay is added ONLY when the
    // subcommand is doc_ingest_bin (the binary parser path). Every other subcommand (search/list/remove/delete/
    // text-ingest) is plain `uv run python …` — zero added deps.
    private static IReadOnlyList<string> UvArgs(IReadOnlyList<string> scriptArgs)
    {
        var isBin = scriptArgs.Count > 1 && scriptArgs[1] == "doc_ingest_bin";
        var uv = new List<string> { "run" };
        if (isBin) uv.AddRange(ParserWith);
        uv.Add("python");
        uv.AddRange(scriptArgs);
        return uv;
    }

    // Pure: the argv for `uv run python doc_index.py doc_sources KB` / `doc_remove KB SOURCE`.
    public static IReadOnlyList<string> BuildSourcesArgs(string kbId)
        => new[] { Script, "doc_sources", kbId ?? "" };

    public static IReadOnlyList<string> BuildRemoveArgs(string kbId, string source)
        => new[] { Script, "doc_remove", kbId ?? "", source ?? "" };

    // Pure: the argv for `uv run python doc_index.py doc_delete KB` — drops the WHOLE KB (every source), the
    // conversation-delete cleanup so a deleted-with-docs thread's chunks don't leak in doc_index.db forever.
    public static IReadOnlyList<string> BuildDeleteArgs(string kbId)
        => new[] { Script, "doc_delete", kbId ?? "" };

    public sealed record Result(bool Ok, IReadOnlyList<(string source, int ord, string content, double score)> Rows, string? Message);

    // Retrieve the top-K KB chunks for one turn, mapped to DocChunks for ChatPrompt injection. ANY failure
    // (sidecar absent, embed server down, no index, timeout, malformed output) degrades to an EMPTY list — the
    // no-KB / no-context path. Never throws, never hangs (30s cap + KillTree, mirroring CodeSearch).
    public static async Task<IReadOnlyList<DocChunk>> RetrieveAsync(string? kbId, string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(kbId) || string.IsNullOrWhiteSpace(query)) return Array.Empty<DocChunk>();
        var r = await SearchAsync(kbId!, query, RetrieveK, ct).ConfigureAwait(false);
        return r.Ok ? ToDocChunks(r.Rows) : Array.Empty<DocChunk>();
    }

    public static async Task<Result> SearchAsync(string kbId, string query, int k, CancellationToken ct)
    {
        if (!Installed) return new Result(false, Array.Empty<(string, int, string, double)>(),
            "knowledge base unavailable — doc_index.py is missing.");
        using Process? p = StartOrNull(BuildSearchArgs(kbId, query, k), out _);
        try
        {
            if (p is null) return new Result(false, Array.Empty<(string, int, string, double)>(), "could not start uv/python");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            var outTask = p.StandardOutput.ReadToEndAsync(ct);
            var errTask = p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            var stdout = await outTask.ConfigureAwait(false);
            await errTask.ConfigureAwait(false);

            // Embed server down / no index => non-zero exit (connection-refused). Degrade, never throw.
            if (p.ExitCode != 0)
                return new Result(false, Array.Empty<(string, int, string, double)>(),
                    "knowledge base unavailable — start the embed server (start-embed.ps1).");

            return new Result(true, ParseDocJson(stdout), "done");
        }
        catch (OperationCanceledException) { KillTree(p); return new Result(false, Array.Empty<(string, int, string, double)>(), "doc search cancelled / timed out"); }
        catch (Exception ex) { KillTree(p); return new Result(false, Array.Empty<(string, int, string, double)>(), $"doc search error: {ex.Message}"); }
    }

    public sealed record IngestResult(bool Ok, int Chunks, string? Message);

    // Ingest one document's TEXT under (kbId, source): chunk + embed via :8090 + store. The text is piped via
    // STDIN (no temp file, no shell-arg length limit, no escaping). A down embed server / missing python =>
    // Ok=false + a clear message (the attach endpoint surfaces it); never throws, never hangs (30s + KillTree).
    public static async Task<IngestResult> IngestAsync(string kbId, string source, string text, CancellationToken ct)
    {
        // Bound FIRST — before the install check and any process spawn: an over-cap doc fails fast with a CLEAR
        // "too large" message, never the 30s-timeout "start the embed server" 503 (the server is fine, the doc is
        // just too big). The size gate is a pure pre-condition independent of whether the sidecar is present.
        var tooBig = ValidateIngest(text);
        if (tooBig is not null) return new IngestResult(false, 0, tooBig);
        if (!Installed) return new IngestResult(false, 0, "knowledge base unavailable — doc_index.py is missing.");
        using Process? p = StartOrNull(BuildIngestArgs(kbId, source), out var redirectStdin);
        try
        {
            if (p is null) return new IngestResult(false, 0, "could not start uv/python");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));

            if (redirectStdin)
            {
                await p.StandardInput.WriteAsync((text ?? "").AsMemory(), ct).ConfigureAwait(false);
                p.StandardInput.Close();   // EOF so doc_index's sys.stdin.read() returns
            }

            var outTask = p.StandardOutput.ReadToEndAsync(ct);
            var errTask = p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            var stdout = await outTask.ConfigureAwait(false);
            await errTask.ConfigureAwait(false);

            if (p.ExitCode != 0)
                return new IngestResult(false, 0, "ingest failed — start the embed server (start-embed.ps1).");

            return new IngestResult(true, ParseChunks(stdout), "done");
        }
        catch (OperationCanceledException) { KillTree(p); return new IngestResult(false, 0, "ingest cancelled / timed out"); }
        catch (Exception ex) { KillTree(p); return new IngestResult(false, 0, $"ingest error: {ex.Message}"); }
    }

    // Ingest one BINARY document (.pdf/.docx — or any extension; the server decodes utf-8 for the rest) under
    // (kbId, source): the raw FILE BYTES are piped via STDIN to `doc_ingest_bin`, which extracts text by extension
    // and reuses the SAME chunk+embed+store pipeline. Distinct error exits each surface a CLEAR, RIGHT message (NEVER
    // the misleading embed-down 503): 3 = parsers couldn't load, 4 = corrupt/encrypted/wrong-format file, 5 =
    // extracted text over MaxDocChars (the same too-large contract as the text path). Never throws, never hangs.
    public static async Task<IngestResult> IngestBinAsync(string kbId, string source, byte[] data, CancellationToken ct)
    {
        // Bound on RAW BYTES FIRST — before the install check and any spawn — so an over-cap upload fails fast with
        // a clear "too large" message, never the 30s-timeout 503. (The endpoint's Content-Length 413 is the cheap
        // outer guard; this holds even on a direct call.)
        var tooBig = ValidateIngestBytes(data);
        if (tooBig is not null) return new IngestResult(false, 0, tooBig);
        if (!Installed) return new IngestResult(false, 0, "knowledge base unavailable — doc_index.py is missing.");
        using Process? p = StartOrNull(BuildIngestBinArgs(kbId, source), out var redirectStdin);
        try
        {
            if (p is null) return new IngestResult(false, 0, "could not start uv/python");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));

            if (redirectStdin)
            {
                await p.StandardInput.BaseStream.WriteAsync((data ?? Array.Empty<byte>()).AsMemory(), ct).ConfigureAwait(false);
                await p.StandardInput.BaseStream.FlushAsync(ct).ConfigureAwait(false);
                p.StandardInput.Close();   // EOF so doc_index's sys.stdin.buffer.read() returns
            }

            var outTask = p.StandardOutput.ReadToEndAsync(ct);
            var errTask = p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            var stdout = await outTask.ConfigureAwait(false);
            await errTask.ConfigureAwait(false);

            // The exit-code -> message mapping is a PURE seam (MapIngestBinExit, unit-tested): 0 = ok (chunks may
            // be 0 for a scanned PDF), 3 = parsers couldn't load, 4 = corrupt/encrypted/wrong-format file, 5 =
            // extracted text over MaxDocChars, any other non-zero = generic (offline parsers OR a down embed server).
            return MapIngestBinExit(p.ExitCode, stdout);
        }
        catch (OperationCanceledException) { KillTree(p); return new IngestResult(false, 0, "ingest cancelled / timed out"); }
        catch (Exception ex) { KillTree(p); return new IngestResult(false, 0, $"ingest error: {ex.Message}"); }
    }

    // PURE: map doc_ingest_bin's process exit code (+ its stdout) to the user-facing IngestResult. Distinct codes so
    // a corrupt file / an over-cap doc / offline parsers each surface the RIGHT message instead of the misleading
    // "start the embed server" 503 (the embed server is fine on every one of those):
    //   exit 0  -> Ok, with the parsed {"chunks":N} (0 chunks = a scanned/image PDF, a benign no-op the endpoint hints)
    //   exit 3  -> the parsers couldn't load (offline / unresolved): surface the script's clear `uv run --with …` hint
    //   exit 4  -> the FILE is unreadable (corrupt, encrypted, not really that format): a clear "couldn't read" message
    //   exit 5  -> the EXTRACTED text exceeds MaxDocChars: the SAME "document too large" message as the text path
    //   other   -> generic: parsers may be unavailable OFFLINE, or the embed server is down — name BOTH (FIX 5)
    public static IngestResult MapIngestBinExit(int exitCode, string? stdout) => exitCode switch
    {
        0 => new IngestResult(true, ParseChunks(stdout), "done"),
        3 => new IngestResult(false, 0, ParseError(stdout)
                 ?? "the PDF/DOCX parsers couldn't load (offline? run: uv run --with pypdf --with python-docx ...)."),
        4 => new IngestResult(false, 0, ParseError(stdout)
                 ?? "couldn't read this file (corrupt, encrypted, or not a valid PDF/DOCX)."),
        5 => new IngestResult(false, 0, ParseError(stdout)
                 ?? $"document too large — split it or attach a smaller file (max {MaxDocChars} chars)."),
        _ => new IngestResult(false, 0,
                 "document ingest failed (the parsers may be unavailable offline, or the embed server is down — see start-embed.ps1)."),
    };

    // List the sources attached to a KB ([{source, chunks}]); empty on any failure (graceful).
    public static async Task<IReadOnlyList<(string source, int chunks)>> SourcesAsync(string kbId, CancellationToken ct)
    {
        if (!Installed || string.IsNullOrWhiteSpace(kbId)) return Array.Empty<(string, int)>();
        using Process? p = StartOrNull(BuildSourcesArgs(kbId), out _);
        try
        {
            if (p is null) return Array.Empty<(string, int)>();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            var outTask = p.StandardOutput.ReadToEndAsync(ct);
            var errTask = p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            var stdout = await outTask.ConfigureAwait(false);
            await errTask.ConfigureAwait(false);
            if (p.ExitCode != 0) return Array.Empty<(string, int)>();
            return ParseSources(stdout);
        }
        catch (OperationCanceledException) { KillTree(p); return Array.Empty<(string, int)>(); }
        catch { KillTree(p); return Array.Empty<(string, int)>(); }
    }

    // Remove one source from a KB; Ok regardless of count (idempotent), degrades on a down sidecar.
    public static async Task<bool> RemoveAsync(string kbId, string source, CancellationToken ct)
    {
        if (!Installed || string.IsNullOrWhiteSpace(kbId)) return false;
        using Process? p = StartOrNull(BuildRemoveArgs(kbId, source), out _);
        try
        {
            if (p is null) return false;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return p.ExitCode == 0;
        }
        catch (OperationCanceledException) { KillTree(p); return false; }
        catch { KillTree(p); return false; }
    }

    // Drop a WHOLE KB's chunks (every source) — the conversation-delete cleanup so a deleted-with-docs thread's
    // vectors don't accumulate in doc_index.db forever. BEST-EFFORT: returns false (never throws, never hangs)
    // when the sidecar is absent / errors / times out, so DELETE /chats/{id} can call it without ever failing the
    // conversation delete itself (a down index just means the rows are cleaned on the next live ingest/reset).
    public static async Task<bool> DeleteAsync(string kbId, CancellationToken ct)
    {
        if (!Installed || string.IsNullOrWhiteSpace(kbId)) return false;
        using Process? p = StartOrNull(BuildDeleteArgs(kbId), out _);
        try
        {
            if (p is null) return false;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return p.ExitCode == 0;
        }
        catch (OperationCanceledException) { KillTree(p); return false; }
        catch { KillTree(p); return false; }
    }

    // `uv run [--with pypdf --with python-docx] python <script> ...` — uv resolves the project python; doc_index.py
    // is stdlib-only EXCEPT the doc_ingest_bin path, which adds the `--with` parser overlay (resolved+cached by uv
    // the first time, free thereafter) so the txt/md/search/list/remove paths stay zero-dep. redirectStdin is true
    // for the two ingest subcommands (doc_ingest pipes text; doc_ingest_bin pipes raw file bytes).
    private static Process? StartOrNull(IReadOnlyList<string> args, out bool redirectStdin)
    {
        var sub = args.Count > 1 ? args[1] : "";
        redirectStdin = sub is "doc_ingest" or "doc_ingest_bin";
        var psi = new ProcessStartInfo("uv")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = redirectStdin,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = RepoPaths.Root,
        };
        // UvArgs adds the `--with` parser overlay ONLY for doc_ingest_bin; every other subcommand stays plain
        // `uv run python …` (zero added deps — the stdlib fast path the text/search sidecar relies on).
        foreach (var a in UvArgs(args)) psi.ArgumentList.Add(a);
        return Process.Start(psi);
    }

    // PURE: pull the {"error":…} message doc_ingest_bin prints on an error exit (3/4/5); null if absent / not a
    // string / not an object / not JSON (the caller then falls back to its own clear default). Unit-tested seam.
    public static string? ParseError(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String)
                return e.GetString();
        }
        catch { /* not JSON — fall through to the caller's default message */ }
        return null;
    }

    private static void KillTree(Process? p)
    {
        try { if (p is { HasExited: false }) p.Kill(entireProcessTree: true); }
        catch { /* already exited / no longer killable — best-effort */ }
    }

    // PURE: parse the JSON array of { source, ord, content, score } doc_search prints. Any malformed / non-array
    // input yields an empty list (never throws), so a garbage stdout degrades to "no context injected".
    public static IReadOnlyList<(string source, int ord, string content, double score)> ParseDocJson(string? json)
    {
        var rows = new List<(string, int, string, double)>();
        if (string.IsNullOrWhiteSpace(json)) return rows;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return rows;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                rows.Add((Str(el, "source"), Int(el, "ord"), Str(el, "content"), Dbl(el, "score")));
            }
        }
        catch { return new List<(string, int, string, double)>(); }
        return rows;
    }

    // PURE: map the parsed rows to the DocChunks ChatPrompt.Build injects. Empty rows => an empty list (not null),
    // so a no-hit retrieval injects nothing and the no-KB output is preserved byte-for-byte.
    public static IReadOnlyList<DocChunk> ToDocChunks(IReadOnlyList<(string source, int ord, string content, double score)> rows)
        => rows is { Count: > 0 }
            ? rows.Select(r => new DocChunk(r.source, r.content, r.score)).ToList()
            : Array.Empty<DocChunk>();

    // Parse doc_ingest's {"chunks":N}; 0 on anything malformed.
    private static int ParseChunks(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return 0;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? Int(doc.RootElement, "chunks") : 0;
        }
        catch { return 0; }
    }

    // Parse doc_sources' [{source, chunks}]; empty on anything malformed.
    private static IReadOnlyList<(string source, int chunks)> ParseSources(string? json)
    {
        var outp = new List<(string, int)>();
        if (string.IsNullOrWhiteSpace(json)) return outp;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return outp;
            foreach (var el in doc.RootElement.EnumerateArray())
                if (el.ValueKind == JsonValueKind.Object)
                    outp.Add((Str(el, "source"), Int(el, "chunks")));
        }
        catch { return new List<(string, int)>(); }
        return outp;
    }

    private static string Str(JsonElement o, string n)
        => o.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int Int(JsonElement o, string n)
    {
        if (!o.TryGetProperty(n, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s)) return s;
        return 0;
    }

    private static double Dbl(JsonElement o, string n)
    {
        if (!o.TryGetProperty(n, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return d;
        if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), out var s)) return s;
        return 0;
    }
}
