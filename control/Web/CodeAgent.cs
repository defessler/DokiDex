using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    public sealed record EditPlan(bool Ok, string RelPath, string FullPath, string NewContent, string Diff, string? Error);

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
        return new(true, path, full, outcome.NewContent, CodeEdit.RenderDiff(path, old, outcome.NewContent), null);
    }

    // Plan a Write (new or overwrite): validate the path and render a diff vs any existing content.
    public static EditPlan PrepareWrite(string root, string? json)
    {
        var (path, content) = ParseWriteArgs(json);
        if (string.IsNullOrWhiteSpace(path)) return new(false, "", "", "", "", "Write needs a `path`.");
        var full = CodeTools.ResolveWorkspacePath(root, path);
        if (full is null) return new(false, path, "", "", "", $"\"{path}\" is outside the workspace.");
        var old = "";
        try { if (File.Exists(full)) old = File.ReadAllText(full); } catch { /* treat as new */ }
        var diff = old.Length == 0 ? $"{path} (new file, {SplitCount(content)} lines)" : CodeEdit.RenderDiff(path, old, content);
        return new(true, path, full, content, diff, null);
    }

    private static int SplitCount(string s) => string.IsNullOrEmpty(s) ? 0 : s.Replace("\r\n", "\n").Split('\n').Length;

    // ---- the ReAct loop for one user turn ----

    // Run one user turn to completion: drive the model, execute its tool calls (read-only directly; mutating behind
    // `approve` + `alwaysAllowed`), append the transcript via Chat.AppendToolRound, and loop until the model answers
    // or MaxHops is hit. `working` carries the full message list (system + history + the just-appended user turn);
    // the final assistant turn is appended on return. Callbacks: onTool(displayLine) before each tool runs;
    // onToolResult(name, resultText) after. Returns the final assistant text.
    public static async Task<string> RunTurnAsync(
        string root, List<object> working, string? model,
        Func<PendingAction, ApprovalDecision> approve, HashSet<string> alwaysAllowed,
        Action<string> onTool, Action<string, string> onToolResult, Action<string> onAssistantText, CancellationToken ct)
    {
        var finalText = "";
        for (var hop = 0; ; hop++)
        {
            var turn = await LocalLlm.ChatToolsAsync(working, ToolsJson, ToolTemperature, MaxTokens, ct, model)
                .ConfigureAwait(false);
            if (!turn.Ok) return turn.Error ?? "(the local model is not reachable — is agent mode up?)";
            finalText = (turn.Content ?? "").Trim();

            var editBlocks = CodeEdit.ParseSearchReplaceBlocks(finalText);
            var hasTools = turn.ToolCalls is { Count: > 0 };

            // Show the model's PROSE between steps (mirrors Claude Code) — edit blocks render as diffs at the
            // approval gate, so strip them from the displayed text. Only for CONTINUING turns (the final answer is
            // printed by the caller); skip empty prose (a bare tool call with no explanation).
            if (editBlocks.Count > 0 || hasTools)
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
        GitCheckpoint(root, plan.RelPath);
        try { File.WriteAllText(plan.FullPath, plan.NewContent); }
        catch (Exception ex) { return $"Edit failed to write \"{plan.RelPath}\": {ex.Message}"; }
        return $"Edited {plan.RelPath}.";
    }

    private static string RunWrite(string root, string? json,
        Func<PendingAction, ApprovalDecision> approve, HashSet<string> alwaysAllowed)
    {
        var plan = PrepareWrite(root, json);
        if (!plan.Ok) return plan.Error!;
        if (!Gate("Write", plan.RelPath, plan.Diff, approve, alwaysAllowed)) return "User denied the write.";
        GitCheckpoint(root, plan.RelPath);
        try
        {
            var dir = Path.GetDirectoryName(plan.FullPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(plan.FullPath, plan.NewContent);
        }
        catch (Exception ex) { return $"Write failed for \"{plan.RelPath}\": {ex.Message}"; }
        return $"Wrote {plan.RelPath}.";
    }

    private static string RunBash(string root, string? json,
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
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(120_000)) { try { p.Kill(true); } catch { } return "Bash: command timed out after 120s."; }
            var outText = (stdout + (stderr.Length > 0 ? "\n[stderr]\n" + stderr : "")).TrimEnd();
            if (outText.Length > 12000) outText = outText[..12000] + "\n… (output truncated)";
            return $"[exit {p.ExitCode}]\n{outText}".TrimEnd();
        }
        catch (Exception ex) { return $"Bash failed: {ex.Message}"; }
    }

    // Best-effort git checkpoint so each approved edit is revertible (git stash-like safety without touching history:
    // a plain commit of the pre-edit state on whatever branch you're on). Silent no-op outside a git repo.
    private static void GitCheckpoint(string root, string relPath)
    {
        try
        {
            if (!Directory.Exists(Path.Combine(root, ".git"))) return;
            Git(root, "add -A");
            Git(root, $"commit -q -m \"doki code checkpoint before editing {relPath}\" --no-verify");
        }
        catch { /* checkpoint is best-effort; never block the edit */ }
    }

    private static void Git(string root, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("git", args)
            {
                WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            });
            p?.WaitForExit(15_000);
        }
        catch { }
    }
}
