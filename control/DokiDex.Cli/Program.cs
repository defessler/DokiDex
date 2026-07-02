using System.Linq;
using System.Text;
using System.Text.Json;
using DokiDex.Web;

namespace DokiDex.Cli;

// doki code — a local terminal coding agent that mirrors the Claude Code CLI, running the user's local coder model
// (coder-fast/coder-big via llama-swap) through CodeAgent's ReAct loop. The workspace is the current directory, so
// you `cd` into any project and run it. Read/Grep run freely; Edit/Write/Bash show a diff or the command and wait
// for your approval (y once / a always / n no). Slash commands: /help /model /clear /cwd /compact /context /resume
// /sessions /export /exit. Content tokens stream live by default (1.1; `--no-stream` forces the old blocking-per-
// turn path). Esc or Ctrl+C interrupts. A dim context meter prints after each interactive turn (1.3); past ~40k
// estimated tokens the session auto-compacts before the next turn runs (never in one-shot mode). Sessions persist
// automatically (1.4) to %USERPROFILE%\.doki\sessions\<workspace-hash>\<timestamp>.json — OUTSIDE the repo, so
// there's nothing to gitignore; `--continue` resumes the workspace's most recent one (composes with `-p`).
internal static class Program
{
    private static CancellationTokenSource? _turnCts;

    // Set for the duration of the Approve() y/a/n prompt's Console.ReadKey — PollEscape checks this so the Esc-
    // interrupt poller doesn't race the approval gate for the same keystroke (1.1).
    private static volatile bool _approvalActive;

    // Permission rules (1.5): the current workspace root and its loaded rule set, set ONCE in Main and mutated
    // in place by ApproveGated/HandlePermissions as rules are added/removed (each mutation is immediately
    // persisted too, so the in-memory copy and the on-disk file never drift apart within a session).
    private static string _permRoot = "";
    private static CodePermissions.Rules _rules = CodePermissions.Rules.Empty;

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
        var continueSession = false;   // --continue (1.4): resume the workspace's most recent saved session
        for (var i = 0; i < args.Length; i++)
        {
            if ((args[i] is "--model" or "-m") && i + 1 < args.Length) model = args[++i];
            else if ((args[i] is "--print" or "-p") && i + 1 < args.Length) oneShot = args[++i];
            else if (args[i] == "--cwd" && i + 1 < args.Length) root = args[++i];
            else if (args[i] == "--output-format" && i + 1 < args.Length) outputFormat = args[++i];
            else if (args[i] == "--no-stream") noStream = true;
            else if (args[i] == "--continue") continueSession = true;
            else if (!args[i].StartsWith('-')) oneShot ??= args[i];
        }
        if (!Directory.Exists(root)) root = Directory.GetCurrentDirectory();

        // Repo orientation (1.2): built ONCE here and reused for the whole session — a second, byte-stable
        // role:"system" message right after the system prompt (workspace instructions + a depth-2 tree + a
        // git-status snapshot). Kept fixed for the session on purpose: the git-status snapshot going stale over a
        // long session matters less than preserving the --cache-reuse prefix (see CodeOrientation's own doc
        // comment for the full CC-divergence rationale).
        var orientation = CodeOrientation.Build(root);

        // Sessions (1.4): a fresh run gets a fresh `working` (system prompt + orientation) and a brand-new session
        // id; `--continue` loads the workspace's most recent saved session instead and ADOPTS its id, so this
        // process's own saves overwrite that same file (CC-style "keep going"). CRITICAL: a loaded session's
        // `working` already carries its OWN system + orientation messages from when it was first saved — do NOT
        // also prepend a fresh pair here, or the transcript would carry two competing leading system turns.
        List<object> working;
        string sessionId;
        string? resumedNote = null;
        if (continueSession)
        {
            var loaded = CodeSessions.LoadLatest(root);
            if (loaded is not null)
            {
                working = loaded.Working;
                sessionId = loaded.Id;
                resumedNote = $"(resumed {loaded.Id}, {loaded.Working.Count} messages)";
            }
            else
            {
                working = FreshWorking(orientation);
                sessionId = CodeSessions.NewSessionId();
                resumedNote = "(no previous session for this workspace — starting fresh)";
            }
        }
        else
        {
            working = FreshWorking(orientation);
            sessionId = CodeSessions.NewSessionId();
        }
        // Permission rules (1.5): REPLACES the old flat in-memory per-tool "always allowed" HashSet, which
        // approved EVERY future call to that tool for the rest of the process the instant 'a' was pressed once,
        // and forgot everything on exit. `always` below is now an inert, permanently-empty set — it still has to
        // be threaded through CodeAgent.RunTurnAsync's signature (unchanged, see ApproveGated's doc comment for
        // why), but CodeAgent's OWN alwaysAllowed.Contains(tool) short-circuit never fires against it, so every
        // gated call reaches ApproveGated below, which is where all "always" persistence now actually lives.
        var always = new HashSet<string>();
        _permRoot = root;
        _rules = CodePermissions.Load(root);
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
            if (resumedNote is not null && !jsonOutput) Paint(ConsoleColor.DarkGray, "  " + resumedNote + "\n");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var (ok, text) = await RunOneTurn(root, working, model, always, oneShot, quiet: jsonOutput, stream: !noStream);
            sw.Stop();
            SaveSession(root, sessionId, working, model);
            // json mode: ONE machine-parseable object on stdout, no colored/informational noise. Approval prompts
            // (if the turn hits one) still print to the console as today — a one-shot run that needs approval will
            // just behave as it does now; that's an accepted limitation of this leaf, not fixed here.
            if (jsonOutput)
                Console.WriteLine(JsonSerializer.Serialize(new { result = text, ok, duration_ms = sw.ElapsedMilliseconds }));
            return ok ? 0 : 1;
        }

        Banner(root, model, orientation.FileName);
        if (resumedNote is not null) Paint(ConsoleColor.DarkGray, "  " + resumedNote + "\n");
        while (true)
        {
            Paint(ConsoleColor.Cyan, "\n› ");
            var line = Console.ReadLine();
            if (line is null) { Console.WriteLine(); break; }   // EOF / Ctrl+Z
            line = line.Trim();
            if (line.Length == 0) continue;
            if (line[0] == '/')
            {
                // /init and /compact both run ASYNC work (a normal turn / one LocalLlm.ChatAsync call) — neither
                // fits inside SlashCommand's synchronous switch, so both are special-cased here before it runs.
                var cmdParts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                var cmd = cmdParts.Length > 0 ? cmdParts[0] : line;
                if (string.Equals(cmd, "/init", StringComparison.OrdinalIgnoreCase))
                {
                    await MaybeAutoCompactAsync(root, working, model);
                    await RunOneTurnInteractive(root, working, model, always, InitPrompt, !noStream);
                    PrintContextMeter(working);
                    SaveSession(root, sessionId, working, model);
                    continue;
                }
                if (string.Equals(cmd, "/compact", StringComparison.OrdinalIgnoreCase))
                {
                    var instructions = cmdParts.Length > 1 ? cmdParts[1].Trim() : "";
                    var (_, message) = await CodeContext.CompactAsync(root, working, model, instructions, CancellationToken.None);
                    Paint(ConsoleColor.Gray, "  " + message + "\n");
                    SaveSession(root, sessionId, working, model);
                    continue;
                }
                if (!SlashCommand(line, ref model, working, root, ref sessionId)) break;
                continue;
            }
            await MaybeAutoCompactAsync(root, working, model);
            await RunOneTurnInteractive(root, working, model, always, line, !noStream);
            PrintContextMeter(working);
            SaveSession(root, sessionId, working, model);
        }
        return 0;
    }

    // The fresh-session `working` seed: the system prompt plus (when the workspace has anything to offer) the 1.2
    // orientation message. Shared by the plain-start path and --continue's "no previous session yet" fallback.
    private static List<object> FreshWorking(CodeOrientation.Loaded orientation)
    {
        var working = new List<object> { new { role = "system", content = CodeAgent.SystemPrompt } };
        if (orientation.Message.Length > 0) working.Add(new { role = "system", content = orientation.Message });
        return working;
    }

    // Session persistence (1.4): saves are best-effort and must never interrupt the REPL — a failure prints ONE
    // dim note for the whole process (not one per turn) and otherwise stays silent.
    private static bool _sessionSaveFailNoted;
    private static void SaveSession(string root, string sessionId, List<object> working, string model)
    {
        if (CodeSessions.Save(root, sessionId, working, model)) return;
        if (_sessionSaveFailNoted) return;
        _sessionSaveFailNoted = true;
        Paint(ConsoleColor.DarkGray, "  (session save failed — continuing without persistence this run)\n");
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
            var text = await CodeAgent.RunTurnAsync(root, working, model, ApproveGated, always, showTool, showToolResult, showAssistantText, onToken, _turnCts.Token);
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

    // 1.5 wiring choice: CodeAgent.Gate/RunEdit/RunWrite/RunBash are UNCHANGED — this wrapper is the ENTIRE
    // permission-rules integration, and it is the `approve` callback CodeAgent.RunTurnAsync is given instead of
    // Approve directly. It is passed `PendingAction(Tool, Title, Preview)` — Title IS the specifier the rules
    // spec calls for (RunEdit/RunWrite pass `plan.RelPath`; RunBash passes the raw command `cmd` — verified by
    // reading each call site). This was the smallest change that satisfies all three requirements: (a) deny
    // short-circuits WITHOUT ever calling the real Approve() prompt — CodeAgent.Gate only sees this wrapper's
    // return value, never knows a rule fired; (b) a matching allow rule also skips the prompt (returns Once
    // straight away); (c) every existing CodeAgent test keeps passing explicit HashSets/approve lambdas of the
    // exact `Func<PendingAction, ApprovalDecision>` shape, because that delegate's signature never changed.
    // NOTE: because `ApprovalDecision` carries no accompanying text, the "denied by permission rule <rule>" detail
    // is necessarily surfaced by THIS wrapper printing it directly to the console (immediately, in place of the
    // old y/a/n prompt) rather than by changing CodeAgent's generic "User denied the edit."/"...command." tool-
    // result strings — there is no channel through the unchanged `approve` delegate to carry rule text back into
    // those strings without widening it, which would ripple into every test that constructs one.
    private static CodeAgent.ApprovalDecision ApproveGated(CodeAgent.PendingAction a)
    {
        var specifier = a.Title;
        var decision = CodePermissions.Decide(_rules, a.Tool, specifier);

        if (decision == CodePermissions.Decision.Deny)
        {
            var denyRule = CodePermissions.FindMatchingRule(_rules.Deny, a.Tool, specifier) ?? "?";
            Console.WriteLine();
            Paint(ConsoleColor.Red, $"  ✗ denied by permission rule {denyRule}\n");
            return CodeAgent.ApprovalDecision.Deny;
        }
        if (decision == CodePermissions.Decision.Allow) return CodeAgent.ApprovalDecision.Once;

        // Ask: fall back to the normal interactive y/a/n prompt (diff or command echoed as always).
        var result = Approve(a);
        if (result != CodeAgent.ApprovalDecision.Always) return result;

        // 'a' (always): persist a RULE instead of CodeAgent's own ephemeral, session-only tool-wide bypass — so
        // this choice survives the process exiting. Bash gets the CC-style follow-up choice (exact / prefix /
        // tool-wide); Edit/Write keep it simple and go straight to a tool-wide rule (path-glob rules are a
        // future refinement — see the plan's F2 note).
        var rule = string.Equals(a.Tool, "Bash", StringComparison.OrdinalIgnoreCase)
            ? PromptBashRuleChoice(specifier)
            : a.Tool;
        _rules = CodePermissions.AddAllow(_rules, rule);
        Paint(ConsoleColor.DarkGray, CodePermissions.Save(_permRoot, _rules)
            ? $"  allow rule saved: {rule}\n"
            : $"  (permission rule save failed — allowed for this session only: {rule})\n");
        return CodeAgent.ApprovalDecision.Once;   // apply now too — CodeAgent's OWN hashset bypass stays unused
    }

    // The CC-style follow-up for a Bash "always": [c]ommand exact / [p]refix "<first two words> *" / [t]ool-wide.
    // Any other key (including a failed/redirected ReadKey) degrades to the exact-command rule — the narrowest,
    // safest choice.
    private static string PromptBashRuleChoice(string command)
    {
        var firstTwo = FirstTwoWords(command);
        var prefixLabel = firstTwo.Length > 0 ? $"{firstTwo} *" : "*";
        Paint(ConsoleColor.Cyan, $"  always allow: [c]ommand exact / [p]refix \"{prefixLabel}\" / [t]ool-wide: ");
        ConsoleKeyInfo key;
        _approvalActive = true;
        try { key = Console.ReadKey(); }
        catch { Console.WriteLine(); return $"Bash({command})"; }
        finally { _approvalActive = false; }
        Console.WriteLine();
        return char.ToLowerInvariant(key.KeyChar) switch
        {
            'p' when firstTwo.Length > 0 => $"Bash({firstTwo} *)",
            't' => "Bash",
            _ => $"Bash({command})",
        };
    }

    // The first two whitespace-separated words of a command, or the whole command when it has fewer than two
    // (there's nothing sensible to offer as a prefix rule then — PromptBashRuleChoice degrades the 'p' choice).
    private static string FirstTwoWords(string command)
    {
        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? $"{parts[0]} {parts[1]}" : "";
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

    // `/context` — a small token-budget breakdown: the leading system messages (system prompt + 1.2 orientation,
    // which never get compacted away) vs. the rest of the history, each with its own CodeContext.EstimateTokens,
    // against the 32k healthy / 131k hard numbers (same constants the meter and auto-compact use).
    private static void ShowContext(List<object> working)
    {
        var leadCount = LeadingSystemCount(working);
        var leadTokens = CodeContext.EstimateTokens(working.Take(leadCount).ToArray());
        var histTokens = CodeContext.EstimateTokens(working.Skip(leadCount).ToArray());
        var total = leadTokens + histTokens;
        Paint(ConsoleColor.Gray,
            $"  system: {leadCount} msg(s) (prompt + orientation) · ~{CodeContext.FormatK(leadTokens)} tokens\n" +
            $"  history: {working.Count - leadCount} msg(s) · ~{CodeContext.FormatK(histTokens)} tokens\n" +
            $"  total: ~{CodeContext.FormatK(total)} tokens  —  {CodeContext.HealthyBudgetTokens / 1000}k healthy working set / "
            + $"{CodeContext.HardWindowTokens / 1000}k hard model window\n");
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
    // message when present) — everything /clear must preserve, and the split point /context reports on. `working`
    // holds anonymous `{ role, content, ... }` objects (never JsonElement pre-serialization); the actual role
    // check is CodeContext.IsSystemMessage (1.3) — the ONE definition, shared with SelectForCompaction.
    private static int LeadingSystemCount(List<object> working)
    {
        var i = 0;
        while (i < working.Count && CodeContext.IsSystemMessage(working[i])) i++;
        return i;
    }

    // 1.3 auto-compact: checked in the REPL loop BEFORE each turn runs (never in one-shot mode — Main's one-shot
    // branch never calls this). Past CodeContext.AutoCompactThresholdTokens estimated tokens, summarize the
    // session down via CodeContext.CompactAsync (no /compact instructions) so the user's next turn never has to
    // wait on an already-blown context window. Always prints ONE dim line — the success message ("(compacted
    // ~Nk -> ~Mk tokens)", reworded to "(auto-compacted ...)" here) or, on failure, the raw error — and always
    // continues with whatever `working` now is (CompactAsync guarantees it is UNCHANGED on failure), so a
    // summarization hiccup never blocks the user's actual turn.
    private static async Task MaybeAutoCompactAsync(string root, List<object> working, string model)
    {
        if (CodeContext.EstimateTokens(working) <= CodeContext.AutoCompactThresholdTokens) return;
        var (ok, message) = await CodeContext.CompactAsync(root, working, model, "", CancellationToken.None);
        var line = ok && message.StartsWith("(compacted", StringComparison.Ordinal) ? "(auto-" + message[1..] : message;
        Paint(ConsoleColor.DarkGray, "  " + line + "\n");
    }

    // 1.3 meter: printed after each interactive turn (never in one-shot mode) — "~12.3k / 32k ctx", colored by
    // CodeContext's healthy-working-set thresholds (DarkGray under 24k, Yellow 24-32k, Red over 32k). 32k is the
    // HEALTHY budget label shown here; the model's actual hard context window is 131k (CodeContext.HardWindowTokens,
    // surfaced by /context instead — not part of this one-liner).
    private static void PrintContextMeter(List<object> working)
    {
        var tokens = CodeContext.EstimateTokens(working);
        var color = tokens > CodeContext.HealthyBudgetTokens ? ConsoleColor.Red
                   : tokens > CodeContext.AmberThresholdTokens ? ConsoleColor.Yellow
                   : ConsoleColor.DarkGray;
        Paint(color, $"  ~{CodeContext.FormatK(tokens)} / {CodeContext.HealthyBudgetTokens / 1000}k ctx\n");
    }

    private static bool SlashCommand(string line, ref string model, List<object> working, string root, ref string sessionId)
    {
        var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        switch (parts[0].ToLowerInvariant())
        {
            case "/exit":
            case "/quit":
                return false;
            case "/help":
                Paint(ConsoleColor.Gray,
                    "  Commands: /help  /model <name>  /diff  /undo  /init  /clear  /cwd  /compact [instructions]\n" +
                    "            /context  /resume [index]  /sessions  /export [file]  /permissions  /exit\n" +
                    "  The agent uses Read, Grep, Edit, Write, Bash — you approve each change & command.\n" +
                    "  /diff shows this session's working-tree changes; /undo reverts the last file change.\n" +
                    "  /init explores the repo and writes/improves a DOKI.md orientation file at the workspace root.\n" +
                    "  /compact summarizes older history down to free up context (optionally focus it, e.g.\n" +
                    "  \"/compact the auth refactor\"); the session also auto-compacts past ~40k estimated tokens.\n" +
                    "  /context shows a small token-budget breakdown (system / history / total vs 32k healthy /\n" +
                    "  131k hard window); a dim \"~Nk / 32k ctx\" meter prints after every turn too.\n" +
                    "  Sessions persist automatically (outside the repo) after every turn: `doki code --continue`\n" +
                    "  resumes the workspace's most recent one; /resume (alias /sessions) lists them newest-first\n" +
                    "  and /resume <index> loads one; /export [file] writes the transcript as markdown.\n" +
                    "  /permissions (alias /allow) lists saved allow/deny rules; \"a\" on an approval prompt now\n" +
                    "  saves a persisted rule instead of a session-only bypass (Bash offers exact/prefix/tool-wide).\n" +
                    "  /permissions allow|deny <rule> adds a rule; /permissions remove <n> removes one — rules look\n" +
                    "  like `Read`, `Edit`, `Bash(git status)`, or `Bash(dotnet test *)`; a deny rule always wins and\n" +
                    "  is checked before you'd even be asked.\n");
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
            case "/context":
                ShowContext(working);
                return true;
            case "/resume":
            case "/sessions":
                HandleResume(parts, working, root, ref sessionId);
                return true;
            case "/export":
                HandleExport(parts, working, root);
                return true;
            case "/permissions":
            case "/allow":
                HandlePermissions(parts, root);
                return true;
            default:
                Paint(ConsoleColor.DarkGray, $"  unknown command {parts[0]} — try /help\n");
                return true;
        }
    }

    // `/resume` (alias `/sessions`): bare => list this workspace's saved sessions newest-first (index, timestamp,
    // message count, ~60-char first-user-turn snippet); `/resume <index>` loads that one, REPLACING `working` and
    // adopting its id so this process's future saves overwrite that file instead of the one it started with.
    private static void HandleResume(string[] parts, List<object> working, string root, ref string sessionId)
    {
        var sessions = CodeSessions.List(root);
        if (sessions.Count == 0) { Paint(ConsoleColor.Gray, "  no saved sessions for this workspace.\n"); return; }

        if (parts.Length > 1 && int.TryParse(parts[1].Trim(), out var idx))
        {
            if (idx < 1 || idx > sessions.Count)
            {
                Paint(ConsoleColor.Gray, $"  no session #{idx} — have 1-{sessions.Count} (see /resume with no argument).\n");
                return;
            }
            var loaded = CodeSessions.Load(sessions[idx - 1].Path);
            if (loaded is null) { Paint(ConsoleColor.Gray, "  that session file could not be read.\n"); return; }
            working.Clear();
            working.AddRange(loaded.Working);
            sessionId = loaded.Id;
            Paint(ConsoleColor.Gray, $"  resumed {loaded.Id} ({loaded.Working.Count} messages).\n");
            return;
        }

        Paint(ConsoleColor.Gray, "  sessions for this workspace (newest first):\n");
        for (var i = 0; i < sessions.Count; i++)
        {
            var s = sessions[i];
            var snippet = s.FirstUserSnippet.Length > 0 ? s.FirstUserSnippet : "(no user turn yet)";
            Paint(ConsoleColor.Gray, $"  [{i + 1}] {s.Id}  ·  {s.MessageCount} msg(s)  ·  {snippet}\n");
        }
        Paint(ConsoleColor.DarkGray, "  /resume <index> to load one.\n");
    }

    // `/export [file]`: render the CURRENT `working` as markdown (CodeSessions.ExportMarkdown) to a workspace-
    // relative path (default "doki-session-<timestamp>.md" at the workspace root), via ResolveWorkspacePath for
    // the same escape-the-workspace safety every other file-touching tool uses. Overwrite-safe: notes when replacing.
    private static void HandleExport(string[] parts, List<object> working, string root)
    {
        var relArg = parts.Length > 1 ? parts[1].Trim() : "";
        var rel = relArg.Length > 0 ? relArg : $"doki-session-{DateTime.Now.ToString(CodeSessions.TimestampFormat)}.md";
        var full = CodeTools.ResolveWorkspacePath(root, rel);
        if (full is null) { Paint(ConsoleColor.Red, $"  \"{rel}\" is outside the workspace.\n"); return; }
        try
        {
            var existed = File.Exists(full);
            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(full, CodeSessions.ExportMarkdown(working));
            Paint(ConsoleColor.Gray, $"  {(existed ? "replaced" : "wrote")} {rel}\n");
        }
        catch (Exception ex) { Paint(ConsoleColor.Red, $"  /export failed: {ex.Message}\n"); }
    }

    // `/permissions` (alias `/allow`): bare => list saved allow/deny rules, numbered together (allow first, then
    // deny — CodePermissions.List's order); `/permissions allow|deny <rule>` adds and persists a rule (rejecting
    // a syntactically-invalid one before it's ever written, so a typo can't become a permanently-inert rule);
    // `/permissions remove <n>` removes by the printed index. Every add/remove persists immediately.
    private static void HandlePermissions(string[] parts, string root)
    {
        var argLine = parts.Length > 1 ? parts[1].Trim() : "";
        var argParts = argLine.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var sub = argParts.Length > 0 ? argParts[0].ToLowerInvariant() : "";

        if (sub is "allow" or "deny")
        {
            var rule = argParts.Length > 1 ? argParts[1].Trim() : "";
            if (rule.Length == 0 || !CodePermissions.IsValidRule(rule))
            {
                Paint(ConsoleColor.Gray,
                    "  usage: /permissions allow|deny <rule>  (e.g. Read, Edit, Bash(git status), Bash(dotnet test *))\n");
                return;
            }
            _rules = sub == "deny" ? CodePermissions.AddDeny(_rules, rule) : CodePermissions.AddAllow(_rules, rule);
            var saved = CodePermissions.Save(root, _rules);
            Paint(ConsoleColor.Gray, $"  {sub} rule added: {rule}" + (saved ? "\n" : "  (save failed — session only)\n"));
            return;
        }

        if (sub == "remove")
        {
            if (argParts.Length < 2 || !int.TryParse(argParts[1].Trim(), out var n))
            {
                Paint(ConsoleColor.Gray, "  usage: /permissions remove <n>  (see /permissions for the numbers)\n");
                return;
            }
            var (ok, updated) = CodePermissions.RemoveAt(_rules, n);
            if (!ok) { Paint(ConsoleColor.Gray, $"  no rule #{n} — see /permissions for the current list.\n"); return; }
            _rules = updated;
            CodePermissions.Save(root, _rules);
            Paint(ConsoleColor.Gray, $"  removed rule #{n}.\n");
            return;
        }

        var numbered = CodePermissions.List(_rules);
        if (numbered.Count == 0)
        {
            Paint(ConsoleColor.Gray, "  no permission rules yet. /permissions allow|deny <rule> to add one.\n");
            return;
        }
        Paint(ConsoleColor.Gray, "  permission rules:\n");
        foreach (var r in numbered)
            Paint(ConsoleColor.Gray, $"  [{r.Index}] {(r.IsDeny ? "deny " : "allow")}  {r.Rule}\n");
        Paint(ConsoleColor.DarkGray, "  /permissions remove <n> to remove one.\n");
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
