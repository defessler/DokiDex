using System.Text;
using System.Text.Json;
using DokiDex.Web;

namespace DokiDex.Cli;

// doki code — a local terminal coding agent that mirrors the Claude Code CLI, running the user's local coder model
// (coder-fast/coder-big via llama-swap) through CodeAgent's ReAct loop. The workspace is the current directory, so
// you `cd` into any project and run it. Read/Grep run freely; Edit/Write/Bash show a diff or the command and wait
// for your approval (y once / a always / n no). Slash commands: /help /model /clear /cwd /exit. Content tokens
// stream live by default (1.1; `--no-stream` forces the old blocking-per-turn path). Esc or Ctrl+C interrupts.
internal static class Program
{
    private static CancellationTokenSource? _turnCts;

    // Set for the duration of the Approve() y/a/n prompt's Console.ReadKey — PollEscape checks this so the Esc-
    // interrupt poller doesn't race the approval gate for the same keystroke (1.1).
    private static volatile bool _approvalActive;

    // CodeAgent.RunTurnAsync never throws on a dead/unreachable model — its !turn.Ok path (CodeAgent.cs) returns the
    // error TEXT as the turn's content instead. These are the exact prefixes LocalLlm.ChatToolsAsync produces for
    // that path (LocalLlm.cs), used below to detect a failed turn without brittle full-string matching.
    private const string ErrLlmReturned = "LLM returned";
    private const string ErrLlmUnreachable = "LLM not reachable";

    // Piped stdin (`git diff | doki code -p "review this"`) is bounded so a runaway producer can't blow up the
    // prompt / process memory.
    private const int StdinCap = 2_000_000;

    private static async Task<int> Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* some terminals reject this — ignore */ }
        var root = Directory.GetCurrentDirectory();
        var model = "coder-fast";
        string? oneShot = null;
        var outputFormat = "text";
        var noStream = false;   // --no-stream forces the blocking path (1.1's escape hatch, F3-R2)
        for (var i = 0; i < args.Length; i++)
        {
            if ((args[i] is "--model" or "-m") && i + 1 < args.Length) model = args[++i];
            else if ((args[i] is "--print" or "-p") && i + 1 < args.Length) oneShot = args[++i];
            else if (args[i] == "--cwd" && i + 1 < args.Length) root = args[++i];
            else if (args[i] == "--output-format" && i + 1 < args.Length) outputFormat = args[++i];
            else if (args[i] == "--no-stream") noStream = true;
            else if (!args[i].StartsWith('-')) oneShot ??= args[i];
        }
        if (!Directory.Exists(root)) root = Directory.GetCurrentDirectory();

        // Repo orientation (1.2): built ONCE here and reused for the whole session — a second, byte-stable
        // role:"system" message right after the system prompt (workspace instructions + a depth-2 tree + a
        // git-status snapshot). Kept fixed for the session on purpose: the git-status snapshot going stale over a
        // long session matters less than preserving the --cache-reuse prefix (see CodeOrientation's own doc
        // comment for the full CC-divergence rationale).
        var orientation = CodeOrientation.Build(root);
        var working = new List<object> { new { role = "system", content = CodeAgent.SystemPrompt } };
        if (orientation.Message.Length > 0) working.Add(new { role = "system", content = orientation.Message });
        var always = new HashSet<string>();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; _turnCts?.Cancel(); };

        // One-shot mode (doki code -p "task") — run a single turn and exit, for scripting.
        if (oneShot is not null)
        {
            // Piped stdin — never read in interactive mode (that stdin is the prompt loop).
            if (Console.IsInputRedirected)
            {
                var stdin = await Console.In.ReadToEndAsync();
                if (stdin.Length > StdinCap) stdin = stdin[..StdinCap];
                if (stdin.Length > 0) oneShot += "\n\n[stdin]\n" + stdin;
            }

            var jsonOutput = string.Equals(outputFormat, "json", StringComparison.OrdinalIgnoreCase);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var (ok, text) = await RunOneTurn(root, working, model, always, oneShot, quiet: jsonOutput, stream: !noStream);
            sw.Stop();
            // json mode: ONE machine-parseable object on stdout, no colored/informational noise. Approval prompts
            // (if the turn hits one) still print to the console as today — a one-shot run that needs approval will
            // just behave as it does now; that's an accepted limitation of this leaf, not fixed here.
            if (jsonOutput)
                Console.WriteLine(JsonSerializer.Serialize(new { result = text, ok, duration_ms = sw.ElapsedMilliseconds }));
            return ok ? 0 : 1;
        }

        Banner(root, model, orientation.FileName);
        while (true)
        {
            Paint(ConsoleColor.Cyan, "\n› ");
            var line = Console.ReadLine();
            if (line is null) { Console.WriteLine(); break; }   // EOF / Ctrl+Z
            line = line.Trim();
            if (line.Length == 0) continue;
            if (line[0] == '/')
            {
                // /init runs a NORMAL turn (not a synchronous SlashCommand) with a fixed exploration prompt — it
                // needs the full Read/Grep/Write loop, so it can't be handled inside SlashCommand's sync switch.
                var cmd = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } p ? p[0] : line;
                if (string.Equals(cmd, "/init", StringComparison.OrdinalIgnoreCase))
                {
                    await RunOneTurnInteractive(root, working, model, always, InitPrompt, !noStream);
                    continue;
                }
                if (!SlashCommand(line, ref model, working, root)) break;
                continue;
            }
            await RunOneTurnInteractive(root, working, model, always, line, !noStream);
        }
        return 0;
    }

    // /init's fixed prompt (1.2c): explore the repo and (re)write DOKI.md. Write's normal approval gate still
    // applies — this is just a normal user turn under the hood, nothing special-cased in CodeAgent.
    private const string InitPrompt =
        "Read any existing DOKI.md, AGENTS.md, or CLAUDE.md first and improve rather than overwrite. Explore this "
        + "repository with Read and Grep (key files, layout, build/test commands, conventions). Then Write a "
        + "concise DOKI.md at the workspace root covering: purpose, layout, how to build/test, conventions. Keep "
        + "it under 100 lines.";

    // Interactive-mode wrapper: runs a small background poller alongside the turn so Esc can interrupt it
    // (best-effort, alongside Ctrl+C — F2: CC interrupts with Esc, keeping work done so far). The poller is only
    // alive for the duration of this await, which is exactly when the REPL's own Console.ReadLine is NOT reading
    // — so it can't steal keys meant for the next prompt. It CAN, in principle, race the approval gate's
    // Console.ReadKey (RunEdit/RunBash prompts happen synchronously inside this same turn); _approvalActive
    // gates that window so the poller skips checking while a y/a/n prompt is up. A tiny race remains right at the
    // instant the flag flips (best-effort, as specified — not worth a bigger console-arbitration mechanism here).
    private static async Task<(bool Ok, string Text)> RunOneTurnInteractive(
        string root, List<object> working, string model, HashSet<string> always, string userText, bool stream)
    {
        using var pollCts = new CancellationTokenSource();
        var poller = Task.Run(() => PollEscape(pollCts.Token));
        try { return await RunOneTurn(root, working, model, always, userText, stream: stream); }
        finally
        {
            pollCts.Cancel();
            try { await poller; } catch { /* best-effort */ }
        }
    }

    // Polls for Esc every ~100ms while a turn is in flight; Ctrl+C (Console.CancelKeyPress) still works too.
    // Best-effort: any console that doesn't support KeyAvailable (redirected input, some terminals) just stops
    // polling rather than throwing.
    private static void PollEscape(CancellationToken pollCt)
    {
        while (!pollCt.IsCancellationRequested)
        {
            try
            {
                if (!_approvalActive && Console.KeyAvailable && Console.ReadKey(intercept: true).Key == ConsoleKey.Escape)
                {
                    _turnCts?.Cancel();
                    return;
                }
            }
            catch { return; }
            Thread.Sleep(100);
        }
    }

    // Runs one user turn. Returns (ok, text): ok is false when the model was unreachable (RunTurnAsync's !Ok path,
    // detected via the ErrLlm* prefixes above) or an exception was caught; true otherwise. Interactive mode ignores
    // the return value — the REPL always continues. One-shot mode uses it for the process exit code (Main) and,
    // in `--output-format json`, for the printed result. `quiet` suppresses the normal colored tool/prose echo (for
    // json mode) without changing the approval gate, which still writes to the console either way. `stream`
    // (1.1, default on) paints content tokens live through a fresh per-turn StreamDisplayFilter (suppresses
    // SEARCH/REPLACE block bodies — they render as a diff at the approval gate); it is forced off by `quiet`
    // (json mode wants machine output only, no token noise on stdout) and by `--no-stream`.
    private static async Task<(bool Ok, string Text)> RunOneTurn(
        string root, List<object> working, string model, HashSet<string> always, string userText,
        bool quiet = false, bool stream = true)
    {
        working.Add(new { role = "user", content = userText });
        _turnCts = new CancellationTokenSource();
        Action<string> showTool = quiet ? _ => { } : ShowTool;
        Action<string, string> showToolResult = quiet ? (_, _) => { } : ShowToolResult;
        Action<string> showAssistantText = quiet ? _ => { } : ShowAssistantText;

        var streaming = stream && !quiet;
        var filter = streaming ? new StreamDisplayFilter() : null;
        var streamedAny = false;   // true the moment any real content chunk arrives — NOT just "streaming was attempted"
        Action<string>? onToken = filter is null ? null : t =>
        {
            if (!streamedAny) { streamedAny = true; Console.WriteLine(); }   // one-time lead-in, mirrors the old blank line
            var shown = filter.Push(t);
            if (shown.Length > 0) ShowToken(shown);
        };

        try
        {
            var text = await CodeAgent.RunTurnAsync(root, working, model, Approve, always, showTool, showToolResult, showAssistantText, onToken, _turnCts.Token);
            var ok = !(text.StartsWith(ErrLlmReturned, StringComparison.Ordinal) || text.StartsWith(ErrLlmUnreachable, StringComparison.Ordinal));
            if (!quiet)
            {
                if (streamedAny)
                {
                    // The final answer already painted live, token by token — do NOT re-print it. Just flush any
                    // still-buffered trailing partial line and close out the streamed block.
                    var trailing = filter!.Flush();
                    if (trailing.Length > 0) ShowToken(trailing);
                    Console.WriteLine();
                }
                else
                {
                    // Nothing actually streamed (model unreachable before any token arrived, every hop fell back
                    // to the blocking path, or streaming was off) — same behavior as before 1.1.
                    Console.WriteLine();
                    Paint(ConsoleColor.Gray, text + "\n");
                }
            }
            return (ok, text);
        }
        catch (OperationCanceledException)
        {
            const string text = "(interrupted)";
            if (!quiet) Paint(ConsoleColor.DarkGray, "\n" + text + "\n");
            return (false, text);
        }
        catch (Exception ex)
        {
            var text = $"error: {ex.Message}";
            if (!quiet) Paint(ConsoleColor.Red, "\n" + text + "\n");
            return (false, text);
        }
        finally { _turnCts?.Dispose(); _turnCts = null; }
    }

    // The approval gate: show a colored diff (Edit/Write) or the command (Bash), then read a single key.
    // Default (Enter / any other key) is NO — the safe choice for a security-conscious local agent.
    private static CodeAgent.ApprovalDecision Approve(CodeAgent.PendingAction a)
    {
        Console.WriteLine();
        if (string.Equals(a.Tool, "Bash", StringComparison.OrdinalIgnoreCase))
            Paint(ConsoleColor.Yellow, $"  $ {a.Preview}\n");
        else
            PrintDiff(a.Preview);
        Paint(ConsoleColor.Cyan, $"  Allow {a.Tool}? [y]es / [a]lways / [n]o: ");
        ConsoleKeyInfo key;
        _approvalActive = true;   // tell the Esc-interrupt poller to stand down for this read (1.1)
        try { key = Console.ReadKey(); } catch { Console.WriteLine(); return CodeAgent.ApprovalDecision.Deny; }
        finally { _approvalActive = false; }
        Console.WriteLine();
        return char.ToLowerInvariant(key.KeyChar) switch
        {
            'a' => CodeAgent.ApprovalDecision.Always,
            'y' => CodeAgent.ApprovalDecision.Once,
            _ => CodeAgent.ApprovalDecision.Deny,
        };
    }

    private static void ShowTool(string line) => Paint(ConsoleColor.Cyan, "\n" + line + "\n");

    private static void ShowAssistantText(string text) => Paint(ConsoleColor.Gray, "\n" + text + "\n");

    // Streamed content tokens (already filtered — SEARCH/REPLACE block bodies suppressed by StreamDisplayFilter).
    private static void ShowToken(string text) => Paint(ConsoleColor.Gray, text);

    // `/diff` — show the workspace's working-tree changes (what the agent has edited this session, since edits land
    // as plain working-tree changes) with +/- coloring, so you can review before committing. Read-only.
    private static void ShowGitDiff(string root)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git", "-c core.pager=cat diff --no-color")
            {
                WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) { Paint(ConsoleColor.DarkGray, "  (git not available)\n"); return; }
            var outp = p.StandardOutput.ReadToEnd();
            p.WaitForExit(15000);
            if (string.IsNullOrWhiteSpace(outp)) { Paint(ConsoleColor.DarkGray, "  (no working-tree changes)\n"); return; }
            foreach (var l in outp.Replace("\r", "").Split('\n'))
            {
                var c = l.StartsWith("+") && !l.StartsWith("+++") ? ConsoleColor.Green
                      : l.StartsWith("-") && !l.StartsWith("---") ? ConsoleColor.Red
                      : l.StartsWith("@@") ? ConsoleColor.Cyan
                      : ConsoleColor.DarkGray;
                Paint(c, l + "\n");
            }
        }
        catch (Exception ex) { Paint(ConsoleColor.Red, $"  /diff failed: {ex.Message}\n"); }
    }

    private static void ShowToolResult(string name, string result)
    {
        var first = result.Replace("\r", "").Split('\n')[0];
        if (first.Length > 100) first = first[..100] + "…";
        Paint(ConsoleColor.DarkGray, "  ⎿ " + first + "\n");
    }

    private static void PrintDiff(string diff)
    {
        foreach (var l in diff.Replace("\r", "").Split('\n'))
        {
            var c = l.StartsWith("+ ") ? ConsoleColor.Green
                  : l.StartsWith("- ") ? ConsoleColor.Red
                  : ConsoleColor.DarkGray;
            Paint(c, "  " + l + "\n");
        }
    }

    // How many messages at the START of `working` are role:"system" (the system prompt, plus the 1.2 orientation
    // message when present) — everything /clear must preserve. `working` holds anonymous `{ role, content, ... }`
    // objects (never JsonElement pre-serialization), so a tiny reflection check on the "role" property is enough;
    // no need to serialize just to answer this.
    private static int LeadingSystemCount(List<object> working)
    {
        var i = 0;
        while (i < working.Count && IsSystemMessage(working[i])) i++;
        return i;
    }

    private static bool IsSystemMessage(object message)
        => message.GetType().GetProperty("role")?.GetValue(message) as string == "system";

    private static bool SlashCommand(string line, ref string model, List<object> working, string root)
    {
        var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        switch (parts[0].ToLowerInvariant())
        {
            case "/exit":
            case "/quit":
                return false;
            case "/help":
                Paint(ConsoleColor.Gray,
                    "  Commands: /help  /model <name>  /diff  /undo  /init  /clear  /cwd  /exit\n" +
                    "  The agent uses Read, Grep, Edit, Write, Bash — you approve each change & command.\n" +
                    "  /diff shows this session's working-tree changes; /undo reverts the last file change.\n" +
                    "  /init explores the repo and writes/improves a DOKI.md orientation file at the workspace root.\n");
                return true;
            case "/model":
                if (parts.Length > 1) { model = parts[1].Trim(); Paint(ConsoleColor.Gray, $"  model → {model}\n"); }
                else Paint(ConsoleColor.Gray, $"  model = {model}  (coder-fast | coder-big | fast-candidate-gptoss20b)\n");
                return true;
            case "/clear":
                // Keep ALL leading system messages (the system prompt AND, since 1.2, the orientation message) —
                // NOT just working[0]. A fixed RemoveRange(1, ...) would nuke the orientation message the moment
                // a second leading system message exists.
                var keep = LeadingSystemCount(working);
                if (working.Count > keep) working.RemoveRange(keep, working.Count - keep);
                Paint(ConsoleColor.Gray, "  context cleared.\n");
                return true;
            case "/cwd":
                Paint(ConsoleColor.Gray, $"  workspace: {root}\n");
                return true;
            case "/undo":
                Paint(ConsoleColor.Gray, "  " + CodeAgent.Undo() + "\n");
                return true;
            case "/diff":
                ShowGitDiff(root);
                return true;
            default:
                Paint(ConsoleColor.DarkGray, $"  unknown command {parts[0]} — try /help\n");
                return true;
        }
    }

    private static void Banner(string root, string model, string? instructionsFile)
    {
        Paint(ConsoleColor.Cyan, "\n  doki code");
        Paint(ConsoleColor.DarkGray, $"  · local coding agent · {model}\n");
        Paint(ConsoleColor.DarkGray, $"  workspace: {root}\n");
        if (instructionsFile is not null) Paint(ConsoleColor.DarkGray, $"  instructions: {instructionsFile}\n");
        Paint(ConsoleColor.DarkGray, "  type a task, or /help · Esc or Ctrl+C interrupts · /exit to quit\n");
    }

    private static void Paint(ConsoleColor c, string s)
    {
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = c;
        Console.Write(s);
        Console.ForegroundColor = prev;
    }
}
