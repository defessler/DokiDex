using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using DokiDex.Web;

namespace DokiDex.Cli;

// doki code — a local terminal coding agent that mirrors the Claude Code CLI, running the user's local coder model
// (coder-fast/coder-big via llama-swap) through CodeAgent's ReAct loop. The workspace is the current directory, so
// you `cd` into any project and run it. Read/Grep run freely; Edit/Write/Bash show a diff or the command and wait
// for your approval (y once / a always / n no). Slash commands: /help /model /clear /cwd /compact /context /resume
// /sessions /export /status /usage /plan /act /exit. Content tokens stream live by default (1.1; `--no-stream` forces the old
// blocking-per-turn path). Esc or Ctrl+C interrupts. A dim context meter prints after each interactive turn (1.3),
// extended (1.6) with wall-clock seconds and tok/s; past ~40k estimated tokens the session auto-compacts before the
// next turn runs (never in one-shot mode). Sessions persist automatically (1.4) to
// %USERPROFILE%\.doki\sessions\<workspace-hash>\<timestamp>.json — OUTSIDE the repo, so there's nothing to
// gitignore; `--continue` resumes the workspace's most recent one (composes with `-p`). Input ergonomics (1.7):
// `@rel/path` inline in any message (interactive or `-p`) appends a bounded read of that file for the model (up to
// 3 per message); a line starting `!` runs a shell command DIRECTLY, with no approval gate — the user typed it.
// Custom slash commands (1.9): a `.doki/commands/<name>.md` file (workspace-local, wins) or
// `%USERPROFILE%\.doki\commands\<name>.md` (global) becomes `/<name> [args]` — its text runs as a normal turn,
// "$ARGUMENTS" replaced by whatever followed the command name; built-ins above always take precedence.
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

    // Plan mode (1.8): read-only exploration. While true, every turn is run with CodeAgent's PlanToolsJson
    // (Read/Grep only) plus its extra request-layer instruction — Edit/Write/Bash are disabled until /act. Purely
    // session-scoped (like `always`/permission rules being loaded once) — never persisted, reset by process
    // restart. /plan turns it on; /act (or `/plan off`) turns it back off.
    private static bool _planMode;

    // Usage accumulation (1.6): summed for the WHOLE interactive session — turns, prompt/completion tokens (from
    // LocalLlm.LastUsage right after each turn), and wall-clock seconds spent in RunOneTurnInteractive. Backs
    // /usage (aliases /cost, /stats) and the per-turn meter suffix. Reset only by process restart; a one-shot
    // (`-p`) run never touches these — a single-turn process has no running "session" total worth showing.
    private static int _usageTurnCount;
    private static long _usagePromptTokensTotal;
    private static long _usageCompletionTokensTotal;
    private static double _usageWallSecondsTotal;

    // Dedicated short-timeout client for `/status` (1.6, folds in the old 1.8 /status per the F3 merge) — probes
    // llama-swap directly from the CLI rather than shelling out to `doki status`. Separate from LocalLlm's own
    // clients: this is a read-only, CLI-local status probe, not part of the chat path, and wants to fail FAST
    // (~3s) rather than share LocalLlm's multi-minute chat timeout.
    private static readonly HttpClient StatusHttp = new() { Timeout = TimeSpan.FromSeconds(3) };

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
            // @rel/path file mentions (1.7a) — applies here too (the plan covers one-shot, not just interactive):
            // augment BEFORE the piped-stdin append below, so mentions reflect what the user actually TYPED, not
            // whatever happens to be flowing through stdin.
            oneShot += CodeMentions.Augment(root, oneShot);

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
            // Plan mode's prompt indicator (1.8) — the one persistent visual cue that Edit/Write/Bash are
            // currently disabled, mirroring how Claude Code's own plan mode changes its prompt chrome.
            Paint(ConsoleColor.Cyan, _planMode ? "\nplan› " : "\n› ");
            var line = Console.ReadLine();
            if (line is null) { Console.WriteLine(); break; }   // EOF / Ctrl+Z
            line = line.Trim();
            if (line.Length == 0) continue;
            if (line[0] == '!')
            {
                // `!command` shell passthrough (1.7b) — the user typed this themselves, so it runs DIRECTLY: no
                // model round-trip, no approval gate (see HandleShellPassthrough's doc comment for the reasoning).
                HandleShellPassthrough(root, working, line[1..].Trim());
                SaveSession(root, sessionId, working, model);
                continue;
            }
            if (line[0] == '/')
            {
                // /init and /compact both run ASYNC work (a normal turn / one LocalLlm.ChatAsync call) — neither
                // fits inside SlashCommand's synchronous switch, so both are special-cased here before it runs.
                var cmdParts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                var cmd = cmdParts.Length > 0 ? cmdParts[0] : line;
                if (string.Equals(cmd, "/init", StringComparison.OrdinalIgnoreCase))
                {
                    await MaybeAutoCompactAsync(root, working, model);
                    await RunTimedInteractiveTurnAsync(root, working, model, always, InitPrompt, !noStream, _planMode);
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
                if (string.Equals(cmd, "/status", StringComparison.OrdinalIgnoreCase))
                {
                    await ShowStatusAsync(model);
                    continue;
                }
                var outcome = SlashCommand(line, ref model, working, root, ref sessionId);
                if (outcome == SlashOutcome.Exit) break;
                if (outcome == SlashOutcome.Unmatched)
                {
                    // Custom slash commands (1.9): only reached once every built-in has already failed to match
                    // (SlashCommand's own `default:` case) — built-ins always win over a same-named custom command.
                    await HandleCustomCommandAsync(root, working, model, always, cmd, cmdParts, noStream);
                    SaveSession(root, sessionId, working, model);
                }
                continue;
            }
            // @rel/path file mentions (1.7a): augment BEFORE the turn runs — the original "@token" wording stays in
            // the message exactly as typed; Augment only ever returns extra text to APPEND after it.
            var augmented = line + CodeMentions.Augment(root, line);
            await MaybeAutoCompactAsync(root, working, model);
            await RunTimedInteractiveTurnAsync(root, working, model, always, augmented, !noStream, _planMode);
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
        string root, List<object> working, string model, HashSet<string> always, string userText, bool stream,
        bool planMode = false)
    {
        using var pollCts = new CancellationTokenSource();
        var poller = Task.Run(() => PollEscape(pollCts.Token));
        try { return await RunOneTurn(root, working, model, always, userText, stream: stream, planMode: planMode); }
        finally
        {
            pollCts.Cancel();
            try { await poller; } catch { /* best-effort */ }
        }
    }

    // 1.6 usage/timing wrapper shared by every interactive turn (the plain user-turn path and /init's special
    // case): resets LocalLlm.LastUsage BEFORE the turn so a turn that fails before ever reaching the model reports
    // zeros for itself rather than showing a stale carry-over from a previous turn's hop; times the turn's
    // wall-clock duration; accumulates both into the session totals (/usage); then prints the extended meter.
    private static async Task RunTimedInteractiveTurnAsync(
        string root, List<object> working, string model, HashSet<string> always, string userText, bool stream,
        bool planMode = false)
    {
        LocalLlm.LastUsage = null;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await RunOneTurnInteractive(root, working, model, always, userText, stream, planMode);
        sw.Stop();
        var usage = LocalLlm.LastUsage;
        var wallSeconds = sw.Elapsed.TotalSeconds;
        _usageTurnCount++;
        _usagePromptTokensTotal += usage?.PromptTokens ?? 0;
        _usageCompletionTokensTotal += usage?.CompletionTokens ?? 0;
        _usageWallSecondsTotal += wallSeconds;
        PrintContextMeter(working, usage, wallSeconds);
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
        bool quiet = false, bool stream = true, bool planMode = false)
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
            var text = await CodeAgent.RunTurnAsync(root, working, model, ApproveGated, always, showTool, showToolResult, showAssistantText, onToken, _turnCts.Token, planMode);
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

    // 1.3 meter (extended by 1.6): printed after each interactive turn (never in one-shot mode) —
    // "~12.3k / 32k ctx · 12.4s · 38 tok/s", colored by CodeContext's healthy-working-set thresholds (DarkGray
    // under 24k, Yellow 24-32k, Red over 32k). 32k is the HEALTHY budget label shown here; the model's actual hard
    // context window is 131k (CodeContext.HardWindowTokens, surfaced by /context instead — not part of this
    // one-liner). The wall-clock segment always prints; the tok/s segment is OMITTED when `usage` is null or both
    // fields are zero (the 1.6 degrade path — no usage frame arrived, so a computed rate would be meaningless).
    private static void PrintContextMeter(List<object> working, LocalLlm.UsageInfo? usage, double wallSeconds)
    {
        var tokens = CodeContext.EstimateTokens(working);
        var color = tokens > CodeContext.HealthyBudgetTokens ? ConsoleColor.Red
                   : tokens > CodeContext.AmberThresholdTokens ? ConsoleColor.Yellow
                   : ConsoleColor.DarkGray;
        var line = $"  ~{CodeContext.FormatK(tokens)} / {CodeContext.HealthyBudgetTokens / 1000}k ctx"
            + $" · {wallSeconds.ToString("0.0", CultureInfo.InvariantCulture)}s";
        if (usage is { PromptTokens: > 0 } or { CompletionTokens: > 0 } && wallSeconds > 0)
        {
            var tokPerSec = usage!.CompletionTokens / wallSeconds;
            line += $" · {tokPerSec.ToString("0", CultureInfo.InvariantCulture)} tok/s";
        }
        Paint(color, line + "\n");
    }

    // `/usage` (aliases `/cost`, `/stats` — F2): the WHOLE session's accumulated totals (1.6) — turn count,
    // prompt/completion tokens (each hop's LocalLlm.LastUsage, summed by RunTimedInteractiveTurnAsync),
    // wall-clock time, and the average tok/s over that time. Degrades cleanly to all-zero output (never divides by
    // zero) when no turn has run yet, or every turn's usage was zero (stack down / older llama.cpp build).
    private static void ShowUsage()
    {
        var avgTokPerSec = _usageWallSecondsTotal > 0 ? _usageCompletionTokensTotal / _usageWallSecondsTotal : 0.0;
        Paint(ConsoleColor.Gray,
            $"  turns: {_usageTurnCount}\n" +
            $"  prompt tokens: {_usagePromptTokensTotal}\n" +
            $"  completion tokens: {_usageCompletionTokensTotal}\n" +
            $"  wall time: {_usageWallSecondsTotal.ToString("0.0", CultureInfo.InvariantCulture)}s\n" +
            $"  avg tok/s: {avgTokPerSec.ToString("0.0", CultureInfo.InvariantCulture)}\n");
    }

    // `/status` (1.6 — folds in the old 1.8 /status per the F3 merge): probe llama-swap DIRECTLY from the CLI
    // (no `doki status` subprocess) — GET /v1/models first (the configured-tiers list; also doubles as the
    // reachability check) then, only if that answered, GET /running (the currently loaded model/state). Both
    // probes are best-effort against StatusHttp's ~3s timeout; a connect failure on /v1/models means llama-swap
    // itself is down, so this degrades straight to the documented "not reachable" message and skips /running
    // entirely (no point probing a second endpoint on a server that's already unreachable).
    private static async Task ShowStatusAsync(string currentModel)
    {
        string? modelsJson = null;
        try { modelsJson = await StatusHttp.GetStringAsync("http://127.0.0.1:8080/v1/models"); } catch { /* unreachable */ }
        if (modelsJson is null)
        {
            Paint(ConsoleColor.Red, "  llama-swap not reachable — run: doki up agent\n");
            return;
        }

        string? runningJson = null;
        try { runningJson = await StatusHttp.GetStringAsync("http://127.0.0.1:8080/running"); } catch { /* degrade below */ }

        var configured = CodeStatus.ParseModels(modelsJson);
        var running = CodeStatus.ParseRunning(runningJson);

        Paint(ConsoleColor.Gray, "  llama-swap: reachable\n");
        var loadedModel = running.Model is { Length: > 0 } m ? m : "(none)";
        var loadedState = running.State is { Length: > 0 } st ? $" ({st})" : "";
        Paint(ConsoleColor.Gray, $"  loaded model: {loadedModel}{loadedState}\n");
        Paint(ConsoleColor.Gray, $"  configured tiers: {(configured.Count > 0 ? string.Join(", ", configured) : "(none)")}\n");
        var configuredNote = configured.Count == 0 ? "" : configured.Contains(currentModel) ? " (configured)" : " (NOT in configured tiers)";
        Paint(ConsoleColor.Gray, $"  session model: {currentModel}{configuredNote}\n");
    }

    // SlashCommand's three-way outcome (1.9): built-ins return Handled or Exit exactly as the old bool did
    // (true/false); Unmatched is new — the `default:` case now falls through to it INSTEAD of printing "unknown"
    // directly, so Main can try a custom command (CodeCommands.Discover) before ever reporting a miss. Built-ins
    // always win: Unmatched is only ever produced once every case above it has already failed to match.
    private enum SlashOutcome { Handled, Exit, Unmatched }

    private static SlashOutcome SlashCommand(string line, ref string model, List<object> working, string root, ref string sessionId)
    {
        var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        switch (parts[0].ToLowerInvariant())
        {
            case "/exit":
            case "/quit":
                return SlashOutcome.Exit;
            case "/help":
                Paint(ConsoleColor.Gray,
                    "  Commands: /help  /model <name>  /diff  /undo  /init  /clear  /cwd  /compact [instructions]\n" +
                    "            /context  /resume [index]  /sessions  /export [file]  /permissions  /status\n" +
                    "            /usage  /plan [off]  /act  /exit\n" +
                    "  The agent uses Read, Grep, Edit, Write, Bash — you approve each change & command.\n" +
                    "  /diff shows this session's working-tree changes; /undo reverts the last file change.\n" +
                    "  /init explores the repo and writes/improves a DOKI.md orientation file at the workspace root.\n" +
                    "  /compact summarizes older history down to free up context (optionally focus it, e.g.\n" +
                    "  \"/compact the auth refactor\"); the session also auto-compacts past ~40k estimated tokens.\n" +
                    "  /context shows a small token-budget breakdown (system / history / total vs 32k healthy /\n" +
                    "  131k hard window); a dim \"~Nk / 32k ctx · Ns · N tok/s\" meter prints after every turn too\n" +
                    "  (the tok/s part is omitted when no usage frame arrived).\n" +
                    "  Sessions persist automatically (outside the repo) after every turn: `doki code --continue`\n" +
                    "  resumes the workspace's most recent one; /resume (alias /sessions) lists them newest-first\n" +
                    "  and /resume <index> loads one; /export [file] writes the transcript as markdown.\n" +
                    "  /permissions (alias /allow) lists saved allow/deny rules; \"a\" on an approval prompt now\n" +
                    "  saves a persisted rule instead of a session-only bypass (Bash offers exact/prefix/tool-wide).\n" +
                    "  /permissions allow|deny <rule> adds a rule; /permissions remove <n> removes one — rules look\n" +
                    "  like `Read`, `Edit`, `Bash(git status)`, or `Bash(dotnet test *)`; a deny rule always wins and\n" +
                    "  is checked before you'd even be asked.\n" +
                    "  /status probes llama-swap directly: reachable?, loaded model, configured tiers, and whether\n" +
                    "  the session's model is among them.\n" +
                    "  /usage (aliases /cost, /stats) shows this session's totals: turns, prompt/completion tokens,\n" +
                    "  wall time, and average tok/s.\n" +
                    "  /plan switches to read-only PLAN MODE: only Read/Grep are offered to the model, and any\n" +
                    "  proposed edit is shown but NOT applied — the prompt becomes \"plan› \" while it's on. /act\n" +
                    "  (or /plan off) restores normal editing.\n" +
                    "  @rel/path mentions a workspace file inline (up to 3 per message) — its first ~200 lines are\n" +
                    "  appended for the model to see; an unresolved path is noted instead.\n" +
                    "  !command runs a shell command DIRECTLY, no approval needed (you typed it yourself) — its\n" +
                    "  output is shown and also given to the model as context for your next turn.\n" +
                    "  Custom commands (1.9): a `.doki/commands/<name>.md` file (workspace) or\n" +
                    "  `%USERPROFILE%\\.doki\\commands\\<name>.md` (global; workspace wins on a name clash) becomes\n" +
                    "  `/<name> [args]` — its text runs as your next turn, with every \"$ARGUMENTS\" replaced by\n" +
                    "  whatever you typed after the command name.\n");
                PrintCustomCommandsHelp(root);
                return SlashOutcome.Handled;
            case "/model":
                if (parts.Length > 1) { model = parts[1].Trim(); Paint(ConsoleColor.Gray, $"  model → {model}\n"); }
                else Paint(ConsoleColor.Gray, $"  model = {model}  (coder-fast | coder-big | fast-candidate-gptoss20b)\n");
                return SlashOutcome.Handled;
            case "/clear":
                // Keep ALL leading system messages (the system prompt AND, since 1.2, the orientation message) —
                // NOT just working[0]. A fixed RemoveRange(1, ...) would nuke the orientation message the moment
                // a second leading system message exists.
                var keep = LeadingSystemCount(working);
                if (working.Count > keep) working.RemoveRange(keep, working.Count - keep);
                Paint(ConsoleColor.Gray, "  context cleared.\n");
                return SlashOutcome.Handled;
            case "/cwd":
                Paint(ConsoleColor.Gray, $"  workspace: {root}\n");
                return SlashOutcome.Handled;
            case "/undo":
                Paint(ConsoleColor.Gray, "  " + CodeAgent.Undo() + "\n");
                return SlashOutcome.Handled;
            case "/diff":
                ShowGitDiff(root);
                return SlashOutcome.Handled;
            case "/context":
                ShowContext(working);
                return SlashOutcome.Handled;
            case "/resume":
            case "/sessions":
                HandleResume(parts, working, root, ref sessionId);
                return SlashOutcome.Handled;
            case "/export":
                HandleExport(parts, working, root);
                return SlashOutcome.Handled;
            case "/permissions":
            case "/allow":
                HandlePermissions(parts, root);
                return SlashOutcome.Handled;
            case "/usage":
            case "/cost":
            case "/stats":
                ShowUsage();
                return SlashOutcome.Handled;
            case "/plan":
                if (parts.Length > 1 && string.Equals(parts[1].Trim(), "off", StringComparison.OrdinalIgnoreCase))
                {
                    _planMode = false;
                    Paint(ConsoleColor.Gray, "  plan mode off — Edit/Write/Bash restored.\n");
                }
                else
                {
                    _planMode = true;
                    Paint(ConsoleColor.Yellow, "  plan mode on — read-only (Read/Grep); /act to apply changes.\n");
                }
                return SlashOutcome.Handled;
            case "/act":
                _planMode = false;
                Paint(ConsoleColor.Gray, "  plan mode off — Edit/Write/Bash restored.\n");
                return SlashOutcome.Handled;
            default:
                // Not a built-in — Main tries a custom command (CodeCommands.Discover) before reporting "unknown".
                return SlashOutcome.Unmatched;
        }
    }

    // /help's custom-commands section (1.9): re-scanned on every /help call (cheap — a couple of directory
    // listings) rather than cached, so a command file added mid-session shows up immediately. Printed in a
    // separate dim line, names only ("custom: /review /changelog ..."), and omitted entirely when none are
    // discovered — an empty "custom:" line would just be noise.
    private static void PrintCustomCommandsHelp(string root)
    {
        var names = CodeCommands.Discover(root).Keys.OrderBy(n => n, StringComparer.Ordinal).ToList();
        if (names.Count == 0) return;
        Paint(ConsoleColor.DarkGray, "  custom: " + string.Join(" ", names.Select(n => "/" + n)) + "\n");
    }

    // Custom slash commands (1.9): reached only once SlashCommand's own switch has already failed to match every
    // built-in (SlashOutcome.Unmatched) — built-ins always win over a same-named custom command. Looks up
    // `<workspace>\.doki\commands\<name>.md` (wins) then `%USERPROFILE%\.doki\commands\<name>.md` via
    // CodeCommands.Discover, expands "$ARGUMENTS" with everything typed after "/<name> " (CodeCommands.
    // ExpandTemplate), and runs the result as a completely NORMAL user turn through RunOneTurnInteractive — the
    // exact same path as anything hand-typed, so streaming/the context meter/session-save all apply unchanged. A
    // miss prints the same dim "unknown command" note SlashCommand's old default case used to, now hinting at
    // defining one.
    private static async Task HandleCustomCommandAsync(
        string root, List<object> working, string model, HashSet<string> always, string cmd, string[] cmdParts, bool noStream)
    {
        var name = cmd.Length > 1 ? cmd[1..].ToLowerInvariant() : "";
        if (name.Length == 0 || !CodeCommands.Discover(root).TryGetValue(name, out var path))
        {
            Paint(ConsoleColor.DarkGray, $"  unknown command {cmd} — try /help or define .doki/commands/{(name.Length > 0 ? name : "<name>")}.md\n");
            return;
        }

        string template;
        try { template = File.ReadAllText(path); }
        catch (Exception ex) { Paint(ConsoleColor.Red, $"  could not read {path}: {ex.Message}\n"); return; }

        var arguments = cmdParts.Length > 1 ? cmdParts[1].Trim() : "";
        var expanded = CodeCommands.ExpandTemplate(template, arguments);

        await MaybeAutoCompactAsync(root, working, model);
        await RunTimedInteractiveTurnAsync(root, working, model, always, expanded, !noStream, _planMode);
    }

    // `!command` shell passthrough (1.7b, F2): a bang-prefixed REPL line runs DIRECTLY, with NO approval gate and
    // NO model round-trip. Reasoning (why this is safe to skip the gate, unlike a model-originated Bash call): the
    // approval gate exists to protect the user from a MODEL choosing to run something on their behalf without
    // their say-so; here the USER is the one choosing — they typed the exact command themselves — so there's
    // nothing left to approve. This mirrors Claude Code's own `!` semantics. Still reuses the SAME bounded
    // executor as every model-driven Bash call (concurrent stdout/stderr drain, 120s cap, real cancellation — 0.1)
    // via a trivially-allowing `approve` callback + an `alwaysAllowed` set pre-seeded with "Bash": RunBash's own
    // Gate() short-circuits true the instant alwaysAllowed.Contains("Bash"), so that callback is never actually
    // invoked — it exists only to satisfy the method's signature (RunBash is NOT weakened for model-originated
    // calls; this is a wholly separate call site with its own always-true set, not a change to CodeAgent.Gate).
    // The output is echoed to the console (the normal "[exit N]" block) AND appended to `working` as a role:"user"
    // turn — NOT role:"tool", since there was no tool_call id to hang a tool-role result off of; this is modeled
    // the same way piped stdin (0.2) is: extra user-authored context the model sees on its NEXT turn.
    private static void HandleShellPassthrough(string root, List<object> working, string cmd)
    {
        if (cmd.Length == 0) { Paint(ConsoleColor.DarkGray, "  (empty command — try !git status)\n"); return; }
        var json = JsonSerializer.Serialize(new { command = cmd });
        var alwaysAllowed = new HashSet<string> { "Bash" };
        var result = CodeAgent.RunBash(root, json, _ => CodeAgent.ApprovalDecision.Once, alwaysAllowed, CancellationToken.None);
        Paint(ConsoleColor.Yellow, $"\n  $ {cmd}\n");
        Paint(ConsoleColor.Gray, "  " + result.Replace("\n", "\n  ") + "\n");
        working.Add(new { role = "user", content = $"[shell] $ {cmd}\n{result}" });
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
