using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DokiDex.Web;

// The local CODING AGENT — the shared core behind `doki code` (and a future web code-mode). It mirrors the Claude
// Code CLI: a ReAct loop over a small, CC-named tool set (Read, Grep, Edit, Write, Bash) driven by the local coder
// model via the hardened LocalLlm.ChatToolsAsync, reusing Chat.AppendToolRound / ShouldContinue for the transcript
// shaping and CodeTools / CodeEdit for the actual file work. MUTATING tools (Edit/Write/Bash) are gated behind a
// per-action approval callback (allow-once / allow-always-this-tool / deny) with a git checkpoint before each edit
// — the front-end (console or web) supplies the approval + display callbacks, so this stays UI-agnostic and the
// pure arg-parsers / classification are unit-tested with no model and no disk.
public static class CodeAgent
{
    public const int MaxHops = 24;          // real coding tasks take many tool steps (vs chat's 4)
    public const int MaxTokens = 4096;      // edits/answers can be long
    public const double ToolTemperature = 0.1;   // tight sampling for reliable tool calls (Stage 1 §5.4)

    // Frozen, byte-stable coding system prompt (no timestamps/ids — preserves the --cache-reuse prefix).
    public const string SystemPrompt =
        "You are doki code, a coding agent running in the user's terminal on their local machine. You can Read and "
        + "Grep files, run shell commands with Bash, and CHANGE files. Work step by step: explore the code with Read "
        + "and Grep before changing it; run tests or build commands with Bash to verify your work; be concise and "
        + "direct with no preamble. The user must approve each file change and command, so propose the smallest "
        + "correct change.\n\n"
        + "To CHANGE an existing file, output a SEARCH/REPLACE block — the workspace-relative path on its own line, "
        + "then the block, with the SEARCH text copied EXACTLY from the file (enough lines to be unique):\n"
        + "path/to/file.ext\n"
        + "<<<<<<< SEARCH\n"
        + "the exact existing lines\n"
        + "=======\n"
        + "the new lines\n"
        + ">>>>>>> REPLACE\n\n"
        + "You may output several blocks; they apply in order. Use the Write tool only to create a brand-new file. "
        + "When the task is done, briefly state what you changed.";

    // ---- CC-mirrored tool set ----

    private static object Fn(string name, string description, object parameters)
        => new { type = "function", function = new { name, description, parameters } };

    public static readonly object ReadSchema = Fn("Read",
        "Read a text file by its workspace-relative path, returning the requested line window with 1-based line "
        + "numbers. Use this to see real file contents before editing. Pass offset/limit to page large files.",
        new { type = "object", properties = new {
            path = new { type = "string", description = "Workspace-relative path, e.g. 'src/app.cs'." },
            offset = new { type = "integer", description = "1-based first line (default 1)." },
            limit = new { type = "integer", description = "How many lines (default 1000)." },
        }, required = new[] { "path" } });

    public static readonly object GrepSchema = Fn("Grep",
        "Search files for a regular-expression pattern (literal/regex text search for exact symbols, strings, or "
        + "call sites). Returns matching path:line: text, capped. Optional path scopes a sub-dir; optional glob "
        + "filters file names (e.g. '*.cs').",
        new { type = "object", properties = new {
            pattern = new { type = "string", description = ".NET regular expression to search for." },
            path = new { type = "string", description = "Sub-directory to scope to (optional)." },
            glob = new { type = "string", description = "File-name filter, * and ? wildcards (optional)." },
        }, required = new[] { "pattern" } });

    public static readonly object EditSchema = Fn("Edit",
        "Edit a file by replacing an exact block of lines. Provide `search` = the exact lines currently in the file "
        + "(copy them verbatim from Read, with enough surrounding lines to be unique) and `replace` = the new lines. "
        + "The change is shown to the user as a diff for approval before it is applied.",
        new { type = "object", properties = new {
            path = new { type = "string", description = "Workspace-relative path of the file to edit." },
            search = new { type = "string", description = "The exact existing lines to replace (must match the file)." },
            replace = new { type = "string", description = "The new lines to put in their place." },
        }, required = new[] { "path", "search", "replace" } });

    public static readonly object WriteSchema = Fn("Write",
        "Create a new file (or fully overwrite an existing one) with the given content. Prefer Edit for changing "
        + "part of an existing file. The user approves the write before it happens.",
        new { type = "object", properties = new {
            path = new { type = "string", description = "Workspace-relative path of the file to write." },
            content = new { type = "string", description = "The full file content." },
        }, required = new[] { "path", "content" } });

    public static readonly object BashSchema = Fn("Bash",
        "Run a shell command in the workspace (the local shell — PowerShell on Windows). Use for tests, builds, git, "
        + "or listing files. The user approves each command before it runs. Returns the combined output (bounded).",
        new { type = "object", properties = new {
            command = new { type = "string", description = "The command line to run, e.g. 'dotnet test' or 'git status'." },
        }, required = new[] { "command" } });

    public static readonly object[] ToolsJson = { ReadSchema, GrepSchema, EditSchema, WriteSchema, BashSchema };

    // PURE: which tools change the machine (and so require approval). Case-insensitive, total.
    public static bool IsMutating(string? name)
        => (name ?? "").Trim().ToLowerInvariant() is "edit" or "write" or "bash";

    // ---- approval contract (front-end supplies the decision) ----

    public enum ApprovalDecision { Once, Always, Deny }
    public sealed record PendingAction(string Tool, string Title, string Preview);

    // ---- pure arg parsers (unit-tested, no disk) ----

    public static (string path, string search, string replace) ParseEditArgs(string? json)
        => (StrProp(json, "path"), StrProp(json, "search"), StrProp(json, "replace"));

    public static (string path, string content) ParseWriteArgs(string? json)
        => (StrProp(json, "path"), StrProp(json, "content"));

    public static string ParseBashArgs(string? json) => StrProp(json, "command");

    // PURE: a Claude-Code-style one-line label for a tool call, e.g. "● Read(src/app.cs)", "● Bash(dotnet test)".
    public static string DisplayToolCall(string name, string? argumentsJson)
    {
        var n = (name ?? "").Trim();
        string arg = n.ToLowerInvariant() switch
        {
            "read" => StrProp(argumentsJson, "path"),
            "grep" => StrProp(argumentsJson, "pattern"),
            "edit" or "write" => StrProp(argumentsJson, "path"),
            "bash" => StrProp(argumentsJson, "command"),
            _ => "",
        };
        if (arg.Length > 80) arg = arg[..80] + "…";
        return $"● {n}({arg})";
    }

    // Pull a trimmed string property out of a tool-call arguments JSON object; "" when absent/blank/malformed.
    private static string StrProp(string? json, string name)
    {
        if (string.IsNullOrWhiteSpace(json)) return "";
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
                return (v.GetString() ?? "").Trim();
        }
        catch { /* fall through */ }
        return "";
    }

    // ---- mutating executors (disk/process; gated by the approval callback) ----

    // A planned edit: the validated path + the new content + the rendered diff for approval, or an error to feed back.
    public sealed record EditPlan(bool Ok, string RelPath, string FullPath, string NewContent, string Diff, string? Error,
        string OldContent = "", bool Existed = false);

    // Read the file, apply the SEARCH/REPLACE via CodeEdit, and render the diff — WITHOUT writing (approval comes next).
    public static EditPlan PrepareEdit(string root, string? json)
    {
        var (path, search, replace) = ParseEditArgs(json);
        if (string.IsNullOrWhiteSpace(path)) return new(false, "", "", "", "", "Edit needs a `path`.");
        var full = CodeTools.ResolveWorkspacePath(root, path);
        if (full is null) return new(false, path, "", "", "", $"\"{path}\" is outside the workspace.");
        if (!File.Exists(full)) return new(false, path, full, "", "", $"No file at \"{path}\" — use Write to create it.");
        string old;
        try { old = File.ReadAllText(full); }
        catch (Exception ex) { return new(false, path, full, "", "", $"Edit failed to read \"{path}\": {ex.Message}"); }
        var outcome = CodeEdit.ApplyEdit(old, search, replace);
        if (!outcome.Ok) return new(false, path, full, "", "", outcome.Error);
        return new(true, path, full, outcome.NewContent, CodeEdit.RenderDiff(path, old, outcome.NewContent), null, old, true);
    }

    // Plan a Write (new or overwrite): validate the path and render a diff vs any existing content.
    public static EditPlan PrepareWrite(string root, string? json)
    {
        var (path, content) = ParseWriteArgs(json);
        if (string.IsNullOrWhiteSpace(path)) return new(false, "", "", "", "", "Write needs a `path`.");
        var full = CodeTools.ResolveWorkspacePath(root, path);
        if (full is null) return new(false, path, "", "", "", $"\"{path}\" is outside the workspace.");
        var existed = File.Exists(full);
        var old = "";
        try { if (existed) old = File.ReadAllText(full); } catch { existed = false; }
        var diff = !existed ? $"{path} (new file, {SplitCount(content)} lines)" : CodeEdit.RenderDiff(path, old, content);
        return new(true, path, full, content, diff, null, old, existed);
    }

    private static int SplitCount(string s) => string.IsNullOrEmpty(s) ? 0 : s.Replace("\r\n", "\n").Split('\n').Length;

    // ---- the ReAct loop for one user turn ----

    // Run one user turn to completion: drive the model, execute its tool calls (read-only directly; mutating behind
    // `approve` + `alwaysAllowed`), append the transcript via Chat.AppendToolRound, and loop until the model answers
    // or MaxHops is hit. `working` carries the full message list (system + history + the just-appended user turn);
    // the final assistant turn is appended on return. Callbacks: onTool(displayLine) before each tool runs;
    // onToolResult(name, resultText) after. `onToken` (1.1, optional): when non-null, each hop STREAMS its content
    // live through LocalLlm.ChatToolsStreamAsync instead of the blocking ChatToolsAsync — the caller passes an
    // ALREADY-WRAPPED callback (e.g. Program.cs's StreamDisplayFilter.Push, so SEARCH/REPLACE block bodies stay
    // suppressed there); this loop just forwards it. Everything downstream (edit-block parsing, ShouldContinue,
    // AppendToolRound) is unchanged — it operates on the returned ToolChatResult regardless of which path produced
    // it. Returns the final assistant text.
    public static async Task<string> RunTurnAsync(
        string root, List<object> working, string? model,
        Func<PendingAction, ApprovalDecision> approve, HashSet<string> alwaysAllowed,
        Action<string> onTool, Action<string, string> onToolResult, Action<string> onAssistantText,
        Action<string>? onToken, CancellationToken ct = default)
    {
        var finalText = "";
        for (var hop = 0; ; hop++)
        {
            var turn = onToken is not null
                ? await LocalLlm.ChatToolsStreamAsync(working, ToolsJson, ToolTemperature, MaxTokens, onToken, ct, model)
                    .ConfigureAwait(false)
                : await LocalLlm.ChatToolsAsync(working, ToolsJson, ToolTemperature, MaxTokens, ct, model)
                    .ConfigureAwait(false);
            if (!turn.Ok) return turn.Error ?? "(the local model is not reachable — is agent mode up?)";
            finalText = (turn.Content ?? "").Trim();

            var editBlocks = CodeEdit.ParseSearchReplaceBlocks(finalText);
            var hasTools = turn.ToolCalls is { Count: > 0 };

            // Show the model's PROSE between steps (mirrors Claude Code) — edit blocks render as diffs at the
            // approval gate, so strip them from the displayed text. Only for CONTINUING turns (the final answer is
            // printed by the caller); skip empty prose (a bare tool call with no explanation). Skipped entirely
            // when streaming (onToken != null): that same prose was already painted live, chunk by chunk, so
            // re-printing it here would show it twice.
            if ((editBlocks.Count > 0 || hasTools) && onToken is null)
            {
                var prose = editBlocks.Count > 0 ? CodeEdit.StripSearchReplaceBlocks(finalText) : finalText;
                if (prose.Length > 0) onAssistantText(prose);
            }

            // Final answer: no edit blocks and no tool calls — the content IS the answer.
            if (editBlocks.Count == 0 && !hasTools)
            {
                working.Add(new { role = "assistant", content = finalText });
                return finalText.Length > 0 ? finalText : "(no answer)";
            }

            // Budget guard: stop after MaxHops tool/edit rounds, returning whatever text we have.
            if (hop >= MaxHops)
            {
                working.Add(new { role = "assistant", content = finalText });
                return finalText.Length > 0 ? finalText : $"(stopped after {MaxHops} tool steps)";
            }

            // Preferred edit path: SEARCH/REPLACE blocks in the content — apply (gated), feed results back, then loop
            // so the model can verify or continue. Beats JSON edit-args for open coder models (Aider/Vibe protocol).
            if (editBlocks.Count > 0)
            {
                var (_, editResult) = ApplyTextEdits(root, editBlocks, approve, alwaysAllowed, onTool, onToolResult);
                working.Add(new { role = "assistant", content = finalText });
                working.Add(new { role = "user", content = "[edit results]\n" + editResult });
                continue;
            }

            // Tool calls (Read / Grep / Write / Bash / the JSON Edit fallback).
            var results = new List<string>(turn.ToolCalls.Count);
            foreach (var tc in turn.ToolCalls)
            {
                onTool(DisplayToolCall(tc.Name, tc.ArgumentsJson));
                var result = ExecuteTool(root, tc.Name, tc.ArgumentsJson, approve, alwaysAllowed, ct);
                onToolResult(tc.Name, result);
                results.Add(result);
            }
            Chat.AppendToolRound(working, turn.Content, turn.ToolCalls, results);
        }
    }

    // Apply SEARCH/REPLACE blocks the model emitted in its content — each gated by the same per-action approval +
    // git checkpoint as the JSON Edit tool (reuses RunEdit), returning (appliedCount, combined result text to feed
    // back so the model can verify/recover).
    public static (int applied, string result) ApplyTextEdits(
        string root, IReadOnlyList<CodeEdit.SearchReplaceBlock> blocks,
        Func<PendingAction, ApprovalDecision> approve, HashSet<string> alwaysAllowed,
        Action<string> onTool, Action<string, string> onToolResult)
    {
        var sb = new StringBuilder();
        var applied = 0;
        foreach (var b in blocks)
        {
            var json = JsonSerializer.Serialize(new { path = b.Path, search = b.Search, replace = b.Replace });
            onTool(DisplayToolCall("Edit", json));
            var r = RunEdit(root, json, approve, alwaysAllowed);
            onToolResult("Edit", r);
            if (r.StartsWith("Edited ", StringComparison.Ordinal)) applied++;
            sb.Append(r).Append('\n');
        }
        return (applied, sb.ToString().TrimEnd());
    }

    private static string ExecuteTool(string root, string name, string? json,
        Func<PendingAction, ApprovalDecision> approve, HashSet<string> alwaysAllowed, CancellationToken ct)
        => (name ?? "").Trim().ToLowerInvariant() switch
        {
            "read" => CodeTools.RunReadFile(root, json),
            "grep" => CodeTools.RunGrep(root, json),
            "edit" => RunEdit(root, json, approve, alwaysAllowed),
            "write" => RunWrite(root, json, approve, alwaysAllowed),
            "bash" => RunBash(root, json, approve, alwaysAllowed, ct),
            _ => $"unknown tool: '{name}'. Available: Read, Grep, Edit, Write, Bash.",
        };

    private static bool Gate(string tool, string title, string preview,
        Func<PendingAction, ApprovalDecision> approve, HashSet<string> alwaysAllowed)
    {
        if (alwaysAllowed.Contains(tool)) return true;
        var d = approve(new PendingAction(tool, title, preview));
        if (d == ApprovalDecision.Always) { alwaysAllowed.Add(tool); return true; }
        return d == ApprovalDecision.Once;
    }

    private static string RunEdit(string root, string? json,
        Func<PendingAction, ApprovalDecision> approve, HashSet<string> alwaysAllowed)
    {
        var plan = PrepareEdit(root, json);
        if (!plan.Ok) return plan.Error!;
        if (!Gate("Edit", plan.RelPath, plan.Diff, approve, alwaysAllowed)) return "User denied the edit.";
        try { File.WriteAllText(plan.FullPath, plan.NewContent); }
        catch (Exception ex) { return $"Edit failed to write \"{plan.RelPath}\": {ex.Message}"; }
        _undo.Push(new UndoEntry(plan.FullPath, plan.RelPath, plan.OldContent, plan.Existed));
        return $"Edited {plan.RelPath}.";
    }

    private static string RunWrite(string root, string? json,
        Func<PendingAction, ApprovalDecision> approve, HashSet<string> alwaysAllowed)
    {
        var plan = PrepareWrite(root, json);
        if (!plan.Ok) return plan.Error!;
        if (!Gate("Write", plan.RelPath, plan.Diff, approve, alwaysAllowed)) return "User denied the write.";
        try
        {
            var dir = Path.GetDirectoryName(plan.FullPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(plan.FullPath, plan.NewContent);
        }
        catch (Exception ex) { return $"Write failed for \"{plan.RelPath}\": {ex.Message}"; }
        _undo.Push(new UndoEntry(plan.FullPath, plan.RelPath, plan.OldContent, plan.Existed));
        return $"Wrote {plan.RelPath}.";
    }

    // Hard wall-clock cap for a Bash tool call. TODO: allow an env override once there's a need
    // (cf. Claude Code's BASH_DEFAULT_TIMEOUT_MS) — not plumbed yet.
    private const int BashTimeoutMs = 120_000;

    // internal (not private): lets CodeAgentTests exercise the process-drain/cancellation behavior directly
    // (InternalsVisibleTo — same seam pattern as LocalLlm.Body / MainViewModel.Apply).
    internal static string RunBash(string root, string? json,
        Func<PendingAction, ApprovalDecision> approve, HashSet<string> alwaysAllowed, CancellationToken ct)
    {
        var cmd = ParseBashArgs(json);
        if (string.IsNullOrWhiteSpace(cmd)) return "Bash needs a `command`.";
        if (!Gate("Bash", cmd, cmd, approve, alwaysAllowed)) return "User denied the command.";
        try
        {
            var psi = new ProcessStartInfo("pwsh", "-NoProfile -NonInteractive -Command -")
            {
                WorkingDirectory = Directory.Exists(root) ? root : Directory.GetCurrentDirectory(),
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return "Bash: could not start the shell (pwsh not found?).";
            p.StandardInput.Write(cmd);
            p.StandardInput.Close();

            // Drain both pipes CONCURRENTLY (mirrors DokiService.CaptureFullAsync) — reading stdout to EOF
            // before touching stderr deadlocks the moment a chatty child fills the OS stderr pipe buffer while
            // stdout is still open, well before the timeout below is ever reached.
            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(BashTimeoutMs);
            try { p.WaitForExitAsync(linked.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return ct.IsCancellationRequested ? "(interrupted)" : $"Bash: command timed out after {BashTimeoutMs / 1000}s.";
            }

            var stdout = stdoutTask.GetAwaiter().GetResult();
            var stderr = stderrTask.GetAwaiter().GetResult();
            var outText = (stdout + (stderr.Length > 0 ? "\n[stderr]\n" + stderr : "")).TrimEnd();
            if (outText.Length > 12000) outText = outText[..12000] + "\n… (output truncated)";
            return $"[exit {p.ExitCode}]\n{outText}".TrimEnd();
        }
        catch (Exception ex) { return $"Bash failed: {ex.Message}"; }
    }

    // ---- in-session undo (replaces the old per-edit git commit, which polluted the user's branch history from the
    // 2nd edit on). Each applied Edit/Write records the file's pre-edit content; `/undo` restores the most recent one
    // (or deletes a file that Write created). Session-scoped (lost on exit) — the user's own git is the durable
    // backstop, and edits are plain working-tree changes (reviewable with `git diff`), matching Claude Code.
    // NB: static state is fine for the single-user CLI (one process); revisit if the web reuses CodeAgent.
    public sealed record UndoEntry(string FullPath, string RelPath, string OldContent, bool Existed);
    private static readonly Stack<UndoEntry> _undo = new();

    internal static void ClearUndo() => _undo.Clear();   // test hook

    // Revert the most recent applied edit/write: restore the pre-edit content, or delete a file Write created.
    public static string Undo()
    {
        if (_undo.Count == 0) return "Nothing to undo.";
        var e = _undo.Pop();
        try
        {
            if (e.Existed) File.WriteAllText(e.FullPath, e.OldContent);
            else if (File.Exists(e.FullPath)) File.Delete(e.FullPath);
            return e.Existed ? $"Reverted {e.RelPath}." : $"Removed {e.RelPath} (undid the Write).";
        }
        catch (Exception ex) { return $"Undo failed for \"{e.RelPath}\": {ex.Message}"; }
    }
}

// PURE streaming DISPLAY filter (1.1): as a turn's content streams live, SEARCH/REPLACE block bodies must be
// suppressed — they render as a colored diff at the approval gate instead (CodeAgent.PrepareEdit/RenderDiff), so
// showing the raw "<<<<<<< SEARCH ... >>>>>>> REPLACE" markers too would be noisy AND duplicate the diff. Uses the
// SAME marker rules as CodeEdit (a line starting "<<<<<<<" opens the suppressed range, a line starting ">>>>>>>"
// closes it, both inclusive) but is line-BUFFERED so a marker split across two streamed chunks — entirely
// possible with token-by-token streaming — is still recognized as one line before any suppression decision is
// made. Program.cs (the console front-end) owns one instance per turn: `onToken` is `t => Paint(filter.Push(t))`
// with `filter.Flush()` called once at turn end for any still-buffered trailing partial line. No console/network
// — total + side-effect-free (StreamDisplayFilterTests).
internal sealed class StreamDisplayFilter
{
    private readonly StringBuilder _pending = new();   // a not-yet-newline-terminated partial line
    private bool _inBlock;

    // Feed the next streamed chunk; returns the portion (whole, newline-terminated lines only) safe to display
    // now. A trailing partial line (no terminator yet) is buffered — Push returns it once its newline arrives, or
    // Flush() returns it at turn end.
    public string Push(string chunk)
    {
        if (string.IsNullOrEmpty(chunk)) return "";
        _pending.Append(chunk.Replace("\r\n", "\n"));
        var text = _pending.ToString();
        var lastNl = text.LastIndexOf('\n');
        if (lastNl < 0) return "";   // still no complete line — keep buffering

        var completeText = text[..(lastNl + 1)];
        _pending.Clear();
        _pending.Append(text[(lastNl + 1)..]);   // leftover partial line carried forward to the next Push/Flush

        var sb = new StringBuilder();
        foreach (var line in CompleteLines(completeText))
        {
            var trimmed = line.TrimStart();
            if (!_inBlock && trimmed.StartsWith("<<<<<<<", StringComparison.Ordinal)) { _inBlock = true; continue; }
            if (_inBlock)
            {
                if (trimmed.StartsWith(">>>>>>>", StringComparison.Ordinal)) _inBlock = false;
                continue;   // suppress the marker line itself too, and every line inside the block
            }
            sb.Append(line).Append('\n');
        }
        return sb.ToString();
    }

    // End-of-turn: emit any buffered trailing partial line, UNLESS it is inside a still-open (unterminated) block
    // — a partial edit body must never leak to the display as stray text just because the stream ended early.
    public string Flush()
    {
        if (_pending.Length == 0) return "";
        var trailing = _pending.ToString();
        _pending.Clear();
        return _inBlock ? "" : trailing;
    }

    // `text` always ends with '\n' by construction (the caller only passes the newline-terminated prefix), so
    // Split('\n') yields exactly one extra empty trailing entry to drop.
    private static IEnumerable<string> CompleteLines(string text)
    {
        var parts = text.Split('\n');
        for (var i = 0; i < parts.Length - 1; i++) yield return parts[i];
    }
}

// Repo orientation (1.2): `doki code` otherwise starts Grep-blind — the model has zero project context until it
// spends tool hops discovering it. This builds a SECOND, byte-stable system message giving it that context up
// front: workspace instructions (DOKI.md/AGENTS.md/CLAUDE.md), a depth-2 directory tree, and a git-status
// snapshot. Program.cs calls Build() ONCE at session start and keeps the result FIXED for the whole session.
//
// IMPORTANT divergence from Claude Code (deliberate): CC injects its memory files as a USER message, re-sent (or
// re-derived) each turn. We use a role:"system" message instead: appended right after the system prompt, it stays
// byte-stable turn over turn, which preserves the llama.cpp --cache-reuse prefix (a user-message re-injection, even
// with identical bytes, still sits AFTER the growing history and so doesn't get the same prefix-cache benefit as
// two fixed leading messages). It also means the instructions/tree/git-snapshot survive conversation compaction
// (1.3) for free — compaction only ever summarizes non-system turns.
public static class CodeOrientation
{
    public const int InstructionsCapChars = 8_000;
    public const int TreeCapChars = 2_000;
    public const int GitStatusMaxLines = 30;
    public const int GitStatusTimeoutMs = 10_000;

    // First-found wins: DOKI.md (our own native format) > AGENTS.md (the emerging cross-tool standard, so a
    // workspace already documented for other agents "just works" here) > CLAUDE.md (Claude Code compatibility —
    // a repo set up for CC gets orientation here too, with no changes).
    public static readonly string[] InstructionFileNames = { "DOKI.md", "AGENTS.md", "CLAUDE.md" };

    public sealed record Loaded(string? FileName, string Message);

    // ---- (a) workspace instructions ----

    // Read the first-found instructions file at the workspace root, capped. (null, null) when none exist or the
    // only match couldn't be read (permissions, encoding) — startup must never fail because of this file.
    public static (string? fileName, string? content) LoadInstructions(string root)
    {
        foreach (var name in InstructionFileNames)
        {
            var full = Path.Combine(root, name);
            if (!File.Exists(full)) continue;
            try { return (name, CapText(File.ReadAllText(full), InstructionsCapChars)); }
            catch { return (name, null); }
        }
        return (null, null);
    }

    // PURE: cap `text` at `capChars` chars, appending a truncation note when cut. Total.
    public static string CapText(string text, int capChars)
        => text.Length <= capChars ? text : text[..capChars] + $"\n… (truncated at {capChars} chars)";

    // ---- (b) depth-2 directory tree ----

    // One entry in the depth-2 tree — PURE data, no disk. A file (IsDir=false); an EXPANDABLE depth-1 directory
    // (IsDir=true, Pruned=false, Children = its own depth-2 entries); or a PRUNED depth-2 directory (IsDir=true,
    // Pruned=true, no Children — PrunedFiles/PrunedDirs count ITS immediate children instead of listing them, so
    // depth-3 content is never named, only counted).
    public sealed record TreeEntry(string Name, bool IsDir, bool Pruned = false,
        IReadOnlyList<TreeEntry>? Children = null, int PrunedFiles = 0, int PrunedDirs = 0);

    // PURE: render a depth-2 listing to text, capped at ~capChars total. Directories print "name/"; a pruned
    // directory prints "name/ (N files, M dirs)" instead of expanding further. Total + side-effect-free — the
    // unit-test seam (CodeOrientationTests); the actual disk walk (BuildTree) is a thin wrapper around this.
    public static string RenderTree(IReadOnlyList<TreeEntry> entries, int capChars)
    {
        var sb = new StringBuilder();
        var truncated = false;

        void Walk(IReadOnlyList<TreeEntry> ents, string indent)
        {
            foreach (var e in ents)
            {
                if (truncated) return;
                var line = e.IsDir
                    ? (e.Pruned ? $"{indent}{e.Name}/ ({e.PrunedFiles} files, {e.PrunedDirs} dirs)\n" : $"{indent}{e.Name}/\n")
                    : $"{indent}{e.Name}\n";
                if (sb.Length + line.Length > capChars) { truncated = true; return; }
                sb.Append(line);
                if (e.IsDir && !e.Pruned && e.Children is { Count: > 0 }) Walk(e.Children, indent + "  ");
            }
        }

        Walk(entries, "");
        if (truncated) sb.Append("… (truncated at ~").Append(capChars).Append(" chars)");
        return sb.ToString().TrimEnd();
    }

    // The thin disk walk: build the depth-2 TreeEntry listing for `root`, skipping CodeTools.SkipDirs (VCS/build/
    // dependency/binary-heavy trees) and CodeTools.BinaryExts (image/model/binary noise, same discipline as
    // grep) at every level. Depth-2 directories are NOT expanded further — only shallow-counted (one more
    // Directory.GetFiles/GetDirectories, not a recursive walk), so this stays O(visible entries), never O(repo
    // size). Never throws — an unreadable root degrades to an empty listing.
    internal static IReadOnlyList<TreeEntry> BuildTree(string root)
    {
        try { return ListDepth(root, depthRemaining: 2); }
        catch { return Array.Empty<TreeEntry>(); }
    }

    private static IReadOnlyList<TreeEntry> ListDepth(string dir, int depthRemaining)
    {
        var entries = new List<TreeEntry>();
        string[] subdirs, files;
        try { subdirs = Directory.GetDirectories(dir); } catch { subdirs = Array.Empty<string>(); }
        try { files = Directory.GetFiles(dir); } catch { files = Array.Empty<string>(); }

        foreach (var d in subdirs)
        {
            var name = Path.GetFileName(d);
            if (CodeTools.SkipDirs.Contains(name)) continue;
            entries.Add(depthRemaining > 1
                ? new TreeEntry(name, true, Children: ListDepth(d, depthRemaining - 1))
                : PrunedEntry(name, d));
        }
        foreach (var f in files)
        {
            if (CodeTools.BinaryExts.Contains(Path.GetExtension(f))) continue;
            entries.Add(new TreeEntry(Path.GetFileName(f), false));
        }
        return entries
            .OrderByDescending(e => e.IsDir)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // Shallow (non-recursive) child count for a pruned depth-2 directory: how many text files and non-skipped
    // subdirectories sit directly inside it, WITHOUT descending further.
    private static TreeEntry PrunedEntry(string name, string dir)
    {
        int files = 0, dirs = 0;
        try { files = Directory.GetFiles(dir).Count(f => !CodeTools.BinaryExts.Contains(Path.GetExtension(f))); } catch { }
        try { dirs = Directory.GetDirectories(dir).Count(d => !CodeTools.SkipDirs.Contains(Path.GetFileName(d))); } catch { }
        return new TreeEntry(name, true, Pruned: true, PrunedFiles: files, PrunedDirs: dirs);
    }

    // ---- (b) bounded git-status snapshot ----

    // PURE: cap raw `git status --porcelain` stdout at `maxLines` lines, appending "…and N more" when cut. Blank
    // stdout (a clean tree) => "" — the caller then omits the whole [git status] section (nothing to say).
    public static string FormatGitStatus(string rawStdout, int maxLines)
    {
        var lines = (rawStdout ?? "").Replace("\r\n", "\n").Split('\n').Where(l => l.Length > 0).ToArray();
        if (lines.Length == 0) return "";
        if (lines.Length <= maxLines) return string.Join('\n', lines);
        return string.Join('\n', lines.Take(maxLines)) + $"\n…and {lines.Length - maxLines} more";
    }

    // The thin process call: `git -c core.quotepath=false status --porcelain` in `root`, bounded at `timeoutMs`.
    // Missing git binary, a non-repo workspace, a non-zero exit, or a timeout all degrade to "" (the caller omits
    // the section silently) — never throws. Stdout/stderr are drained concurrently (mirrors RunBash's fix) so a
    // pathological amount of git chatter can't deadlock the wait.
    internal static string RunGitStatus(string root, int timeoutMs = GitStatusTimeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo("git", "-c core.quotepath=false status --porcelain")
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return "";
            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return "";
            }
            p.WaitForExit();   // let the async readers finish flushing now that the process has exited
            var stdout = stdoutTask.GetAwaiter().GetResult();
            _ = stderrTask.GetAwaiter().GetResult();
            return p.ExitCode == 0 ? FormatGitStatus(stdout, GitStatusMaxLines) : "";
        }
        catch { return ""; }
    }

    // ---- message assembly ----

    // PURE: assemble the orientation system message from its three already-rendered, already-capped sections —
    // each omitted ENTIRELY when blank (no empty "[section]\n" headers). This exact text is what Program.cs wraps
    // as role:"system" and keeps FIXED for the session — re-deriving it per turn would defeat --cache-reuse.
    public static string BuildMessage(string? instructions, string? tree, string? gitStatus)
    {
        var sections = new List<string>();
        if (!string.IsNullOrWhiteSpace(instructions)) sections.Add("[workspace]\n" + instructions.TrimEnd());
        if (!string.IsNullOrWhiteSpace(tree)) sections.Add("[structure]\n" + tree.TrimEnd());
        if (!string.IsNullOrWhiteSpace(gitStatus)) sections.Add("[git status]\n" + gitStatus.TrimEnd());
        return string.Join("\n\n", sections);
    }

    // The impure entry point: Program.cs calls this ONCE at session start. Loads instructions, builds the tree,
    // snapshots git status, then assembles. Message is "" when the workspace has nothing to offer (no
    // instructions file, nothing to show, no git) — the caller then adds no second system message at all.
    public static Loaded Build(string root)
    {
        var (fileName, instructions) = LoadInstructions(root);
        var tree = RenderTree(BuildTree(root), TreeCapChars);
        var gitStatus = RunGitStatus(root);
        return new Loaded(fileName, BuildMessage(instructions, tree, gitStatus));
    }
}

// Context accounting (1.3): `working` grows unboundedly and SILENTLY across a session — per-tool result caps alone
// don't bound it (a single 24-hop turn can carry ~96k tokens of tool results). This gives that growth a METER (
// EstimateTokens + Program.cs's dim one-liner), makes it RECOVERABLE (SelectForCompaction + CompactAsync's one LLM
// summarization call, wired to /compact and auto-compact), and makes it INSPECTABLE (/context, via EstimateTokens
// over slices). Every pure helper here treats a `working` ENTRY as the same anonymous role:{system,user,assistant,
// tool} object Chat.AppendToolRound/CodeOrientation.Build already produce — read back via a tiny "role"/"content"
// reflection probe (same pattern Program.cs's own IsSystemMessage used, now centralized here).
public static class CodeContext
{
    // ~32k tokens is where local 30B coder models visibly degrade in practice (F4) — the METER's HEALTHY working-
    // set budget, not the model's actual limit. The hard context window is 131k (noted by /context, never a UI
    // threshold on its own).
    public const int HealthyBudgetTokens = 32_000;
    public const int AmberThresholdTokens = 24_000;
    public const int HardWindowTokens = 131_000;
    public const int AutoCompactThresholdTokens = 40_000;
    public const int DefaultKeepLastTurns = 4;

    private const double CompactTemperature = 0.2;
    private const int CompactMaxTokens = 1024;
    private const int TranscriptClipChars = 500;

    // PURE, approximate: chars/4 over EACH message's OWN serialized JSON, summed — NOT real tokenization (no BPE,
    // no shared-structure discount), just a fast, total estimate good enough to drive a meter/compaction trigger.
    // Serializing per-message (rather than the whole array once) keeps this correct regardless of which anonymous
    // shape a given entry is (plain system/user/assistant text, 1.1's tool_calls assistant turn, a role:"tool"
    // result) — JsonSerializer.Serialize handles any of them the same way.
    public static int EstimateTokens(IReadOnlyList<object> working)
    {
        var total = 0;
        foreach (var msg in working) total += JsonSerializer.Serialize(msg).Length / 4;
        return total;
    }

    // PURE: a one-decimal k-suffixed token count ("12.3k") for the meter/compact/context output. Invariant-culture
    // so a non-'.'-decimal locale (e.g. de-DE) can't corrupt the displayed number.
    public static string FormatK(int tokens)
        => (tokens / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "k";

    // A `working` entry is role:"system" when its "role" property equals "system". The ONE definition shared by
    // SelectForCompaction and Program.cs's /clear + /context (which used to duplicate this via its own private
    // reflection probe). Handles BOTH shapes a `working` entry can be: an anonymous {role,...} object (a live
    // session) OR a System.Text.Json.JsonElement (1.4: a message reloaded from a saved session file — LoadedSession
    // hands the raw parsed JSON back so LocalLlm's re-serialization stays byte-identical) — a resumed session's
    // /clear, /compact, and /context must see the real roles, not silently treat every reloaded message as non-system.
    public static bool IsSystemMessage(object message)
        => message is JsonElement je
            ? je.ValueKind == JsonValueKind.Object && je.TryGetProperty("role", out var r)
                && r.ValueKind == JsonValueKind.String && r.GetString() == "system"
            : message.GetType().GetProperty("role")?.GetValue(message) as string == "system";

    // PURE: split `working` into (toSummarize, kept) for compaction. kept = ALL leading system messages (the
    // system prompt + 1.2's orientation message — NEVER summarized away, preserving both the --cache-reuse prefix
    // and the workspace instructions) PLUS the last `keepLastTurns` non-system messages; toSummarize = every
    // non-system message strictly before those. Fewer than `keepLastTurns` non-system messages total => nothing
    // worth compacting yet: toSummarize is empty and kept is the WHOLE of `working`, unchanged.
    public static (IReadOnlyList<object> toSummarize, IReadOnlyList<object> kept) SelectForCompaction(
        IReadOnlyList<object> working, int keepLastTurns = DefaultKeepLastTurns)
    {
        var leadCount = 0;
        while (leadCount < working.Count && IsSystemMessage(working[leadCount])) leadCount++;

        var nonSystem = working.Skip(leadCount).ToList();
        if (nonSystem.Count <= keepLastTurns) return (Array.Empty<object>(), working.ToList());

        var splitAt = nonSystem.Count - keepLastTurns;
        var toSummarize = nonSystem.Take(splitAt).ToList();
        var keptNonSystem = nonSystem.Skip(splitAt).ToList();

        var kept = new List<object>(working.Take(leadCount));
        kept.AddRange(keptNonSystem);
        return (toSummarize, kept);
    }

    // PURE: render a transcript segment as readable "role: content" lines for the summarization prompt below —
    // role:"tool" results render as "tool(<name>): <content>" so the model knows WHICH tool produced each result;
    // every line is clipped to ~clipChars so one giant Bash/Read dump can't blow the summarizer's OWN prompt. A
    // tool-call assistant turn (content:null — Chat.AppendToolRound's OpenAI-convention shape) renders the called
    // tool names instead of a blank line, so "commands run" still reaches the summary.
    public static string RenderTranscript(IReadOnlyList<object> messages, int clipChars = TranscriptClipChars)
    {
        var sb = new StringBuilder();
        foreach (var msg in messages)
        {
            var role = PropStr(msg, "role") ?? "unknown";
            var content = PropStr(msg, "content");
            string line;
            if (content is null)
            {
                var names = ToolCallNames(msg);
                line = names.Count > 0 ? $"{role}: [called {string.Join(", ", names)}]" : $"{role}: ";
            }
            else
            {
                var clipped = content.Length > clipChars ? content[..clipChars] + "…" : content;
                var toolName = role == "tool" ? PropStr(msg, "name") : null;
                line = toolName is not null ? $"tool({toolName}): {clipped}" : $"{role}: {clipped}";
            }
            sb.Append(line).Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    // Pull the function.name out of each entry of an assistant turn's tool_calls[] (Chat.AppendToolRound's shape) —
    // via reflection for a live anonymous object, or straight JsonElement navigation for a reloaded one (1.4).
    // internal (not private): CodeSessions.ExportMarkdown reuses this SAME dual-shape logic rather than duplicating
    // it. Empty when absent/malformed.
    internal static List<string> ToolCallNames(object msg)
    {
        var names = new List<string>();
        if (msg is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.Object && je.TryGetProperty("tool_calls", out var tcs)
                && tcs.ValueKind == JsonValueKind.Array)
                foreach (var tc in tcs.EnumerateArray())
                    if (tc.ValueKind == JsonValueKind.Object && tc.TryGetProperty("function", out var fn)
                        && fn.ValueKind == JsonValueKind.Object && fn.TryGetProperty("name", out var n)
                        && n.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(n.GetString()))
                        names.Add(n.GetString()!);
            return names;
        }
        if (msg.GetType().GetProperty("tool_calls")?.GetValue(msg) is System.Collections.IEnumerable calls)
            foreach (var tc in calls)
            {
                if (tc is null) continue;
                var fn = tc.GetType().GetProperty("function")?.GetValue(tc);
                var name = fn is null ? null : fn.GetType().GetProperty("name")?.GetValue(fn) as string;
                if (!string.IsNullOrEmpty(name)) names.Add(name);
            }
        return names;
    }

    // Pull a trimmed-free string property off a `working` entry — EITHER shape (see IsSystemMessage above).
    // internal (not private): CodeSessions.ExportMarkdown reuses this for role/content/name/tool_call_id lookups
    // so there is ONE place that knows how to read a `working` entry regardless of where it came from.
    internal static string? PropStr(object msg, string name)
        => msg is JsonElement je
            ? (je.ValueKind == JsonValueKind.Object && je.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null)
            : msg.GetType().GetProperty(name)?.GetValue(msg) as string;

    // The one impure step (/compact + auto-compact): summarize `toSummarize` via ONE LocalLlm.ChatAsync call, then
    // rebuild `working` IN PLACE = leading system messages + one "[session summary]" user turn + the kept last
    // `keepLastTurns` turns. `instructions` (optional — /compact's CC-style focus passthrough) is appended to the
    // summarization system prompt when non-blank. `root` is accepted (currently unused) to keep this call site
    // symmetric with RunTurnAsync/PrepareEdit and leave room for a future workspace-aware summary with no further
    // signature churn. Nothing to compact (fewer than `keepLastTurns` non-system turns) short-circuits WITHOUT
    // touching the network. On LLM failure (or an empty summary): `working` is left COMPLETELY UNCHANGED — a
    // failed summarize must never lose history — and the raw LLM error is returned as Message.
    public static async Task<(bool Ok, string Message)> CompactAsync(
        string root, List<object> working, string? model, string instructions, CancellationToken ct)
    {
        var before = EstimateTokens(working);
        var (toSummarize, kept) = SelectForCompaction(working, DefaultKeepLastTurns);
        if (toSummarize.Count == 0) return (true, "(nothing to compact)");

        var transcript = RenderTranscript(toSummarize);
        var system = "Summarize this coding-session transcript segment concisely: decisions made, files read/"
            + "edited (paths), commands run + outcomes, current task state. Preserve exact file paths and any "
            + "error messages verbatim.";
        if (!string.IsNullOrWhiteSpace(instructions)) system += $" Focus especially on: {instructions.Trim()}";

        var result = await LocalLlm.ChatAsync(system, transcript, CompactTemperature, CompactMaxTokens, ct, model)
            .ConfigureAwait(false);
        if (!result.Ok || string.IsNullOrWhiteSpace(result.Text))
            return (false, result.Error ?? "compaction failed — the model returned no summary.");

        var leadCount = 0;
        while (leadCount < kept.Count && IsSystemMessage(kept[leadCount])) leadCount++;
        var rebuilt = new List<object>(kept.Take(leadCount))
        {
            new { role = "user", content = "[session summary]\n" + result.Text.Trim() },
        };
        rebuilt.AddRange(kept.Skip(leadCount));

        working.Clear();
        working.AddRange(rebuilt);

        var after = EstimateTokens(working);
        return (true, $"(compacted ~{FormatK(before)} → ~{FormatK(after)} tokens)");
    }
}
