using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DokiDex.Web;
using Xunit;

namespace DokiDex.Control.Tests;

// The coding agent's pure surface: tool classification (which need approval), the arg parsers, the Claude-Code-style
// tool-call display, and the prepare-without-writing contract for Edit (the diff is computed; the file is untouched
// until the approval gate passes). The ReAct loop + executors are exercised live against the model; these lock the
// testable core with no model.
public class CodeAgentTests
{
    [Fact]
    public void IsMutating_flags_write_tools_only()
    {
        Assert.True(CodeAgent.IsMutating("Edit"));
        Assert.True(CodeAgent.IsMutating("write"));
        Assert.True(CodeAgent.IsMutating("BASH"));
        Assert.False(CodeAgent.IsMutating("Read"));
        Assert.False(CodeAgent.IsMutating("Grep"));
        Assert.False(CodeAgent.IsMutating(null));
    }

    [Fact]
    public void ParseEditArgs_reads_path_search_replace()
    {
        var (path, search, replace) = CodeAgent.ParseEditArgs("""{"path":"a.cs","search":"old","replace":"new"}""");
        Assert.Equal("a.cs", path);
        Assert.Equal("old", search);
        Assert.Equal("new", replace);
    }

    [Fact]
    public void ParseWriteArgs_reads_path_and_content()
    {
        var (path, content) = CodeAgent.ParseWriteArgs("""{"path":"a.cs","content":"hello"}""");
        Assert.Equal("a.cs", path);
        Assert.Equal("hello", content);
    }

    [Fact]
    public void ParseBashArgs_reads_the_command()
        => Assert.Equal("dotnet test", CodeAgent.ParseBashArgs("""{"command":"dotnet test"}"""));

    [Fact]
    public void Parsers_degrade_to_empty_on_garbage()
    {
        Assert.Equal(("", "", ""), CodeAgent.ParseEditArgs("not json"));
        Assert.Equal("", CodeAgent.ParseBashArgs(null));
    }

    [Fact]
    public void DisplayToolCall_is_claude_code_style()
    {
        Assert.Equal("● Read(src/app.cs)", CodeAgent.DisplayToolCall("Read", """{"path":"src/app.cs"}"""));
        Assert.Equal("● Bash(dotnet test)", CodeAgent.DisplayToolCall("Bash", """{"command":"dotnet test"}"""));
        Assert.Equal("● Grep(TODO)", CodeAgent.DisplayToolCall("Grep", """{"pattern":"TODO"}"""));
    }

    [Fact]
    public void PrepareEdit_reads_applies_and_diffs_without_writing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "doki-ca-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "x.txt");
            File.WriteAllText(file, "a\nb\nc\n");
            var plan = CodeAgent.PrepareEdit(dir, """{"path":"x.txt","search":"b","replace":"B"}""");
            Assert.True(plan.Ok);
            Assert.Equal("a\nB\nc\n", plan.NewContent);
            Assert.Contains("- b", plan.Diff);
            Assert.Contains("+ B", plan.Diff);
            Assert.Equal("a\nb\nc\n", File.ReadAllText(file));   // NOT written yet — approval comes next
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void PrepareEdit_rejects_a_path_outside_the_workspace()
        => Assert.False(CodeAgent.PrepareEdit(Path.GetFullPath("ws"),
            """{"path":"../../etc/hosts","search":"x","replace":"y"}""").Ok);

    // ---- text SEARCH/REPLACE edit path (parser → gated apply) ----

    [Fact]
    public void ApplyTextEdits_applies_an_approved_block_to_disk()
    {
        var dir = Path.Combine(Path.GetTempPath(), "doki-ca-te-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "x.txt");
            File.WriteAllText(file, "a\nb\nc\n");
            var blocks = CodeEdit.ParseSearchReplaceBlocks("x.txt\n<<<<<<< SEARCH\nb\n=======\nB\n>>>>>>> REPLACE\n");
            var (applied, result) = CodeAgent.ApplyTextEdits(dir, blocks,
                _ => CodeAgent.ApprovalDecision.Once,
                new System.Collections.Generic.HashSet<string>(),
                _ => { }, (_, __) => { });
            Assert.Equal(1, applied);
            Assert.Equal("a\nB\nc\n", File.ReadAllText(file));
            Assert.Contains("Edited x.txt", result);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void ApplyTextEdits_does_not_write_a_denied_block()
    {
        var dir = Path.Combine(Path.GetTempPath(), "doki-ca-td-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "x.txt");
            File.WriteAllText(file, "a\nb\nc\n");
            var blocks = CodeEdit.ParseSearchReplaceBlocks("x.txt\n<<<<<<< SEARCH\nb\n=======\nB\n>>>>>>> REPLACE\n");
            var (applied, _) = CodeAgent.ApplyTextEdits(dir, blocks,
                _ => CodeAgent.ApprovalDecision.Deny,
                new System.Collections.Generic.HashSet<string>(),
                _ => { }, (_, __) => { });
            Assert.Equal(0, applied);
            Assert.Equal("a\nb\nc\n", File.ReadAllText(file));   // unchanged — denied at the gate
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ---- in-session undo (replaces the polluting per-edit git commit) ----

    [Fact]
    public void Undo_restores_an_edited_file_to_its_pre_edit_content()
    {
        CodeAgent.ClearUndo();
        var dir = Path.Combine(Path.GetTempPath(), "doki-undo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "x.txt");
            File.WriteAllText(file, "a\nb\nc\n");
            var blocks = CodeEdit.ParseSearchReplaceBlocks("x.txt\n<<<<<<< SEARCH\nb\n=======\nB\n>>>>>>> REPLACE\n");
            CodeAgent.ApplyTextEdits(dir, blocks, _ => CodeAgent.ApprovalDecision.Once,
                new System.Collections.Generic.HashSet<string>(), _ => { }, (_, __) => { });
            Assert.Equal("a\nB\nc\n", File.ReadAllText(file));      // edit applied
            Assert.Contains("Reverted", CodeAgent.Undo());
            Assert.Equal("a\nb\nc\n", File.ReadAllText(file));      // restored to pre-edit
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Undo_with_an_empty_journal_reports_nothing_to_undo()
    {
        CodeAgent.ClearUndo();
        Assert.Equal("Nothing to undo.", CodeAgent.Undo());
    }

    // ---- Bash: concurrent pipe drain + cancellation (0.1) ----
    // RunBash is `internal` (InternalsVisibleTo) purely so these can drive the real process seam: proving the
    // fix empirically beats reasoning about it, and there's no other seam that exercises the pipe-drain path.

    [Fact]
    public async Task RunBash_drains_a_large_stderr_stream_without_deadlocking()
    {
        // Old code (sequential ReadToEnd on stdout THEN stderr) deadlocks here: the child can't exit until its
        // ~80KB stderr write completes, which can't complete until something drains stderr, which never happens
        // because the parent is stuck blocking on stdout.ReadToEnd() waiting for EOF (i.e. process exit).
        var json = JsonSerializer.Serialize(new
        {
            command = "$s = ('x' * 80000); [Console]::Error.WriteLine($s); Write-Output 'done'",
        });
        string? result = null;
        var task = Task.Run(() => result = CodeAgent.RunBash(
            Directory.GetCurrentDirectory(), json,
            _ => CodeAgent.ApprovalDecision.Once, new System.Collections.Generic.HashSet<string>(), CancellationToken.None));

        var finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(30))) == task;
        Assert.True(finished, "RunBash deadlocked draining a large stderr stream.");
        Assert.NotNull(result);
        Assert.StartsWith("[exit 0]", result);
        Assert.Contains("done", result);
    }

    [Fact]
    public async Task RunBash_honors_cancellation_and_returns_promptly()
    {
        var json = JsonSerializer.Serialize(new { command = "Start-Sleep -Seconds 600" });
        using var cts = new CancellationTokenSource();
        string? result = null;
        var task = Task.Run(() => result = CodeAgent.RunBash(
            Directory.GetCurrentDirectory(), json,
            _ => CodeAgent.ApprovalDecision.Once, new System.Collections.Generic.HashSet<string>(), cts.Token));

        await Task.Delay(1000);   // give the child a moment to actually start sleeping
        cts.Cancel();

        var finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(30))) == task;
        Assert.True(finished, "RunBash did not respond to cancellation promptly.");
        Assert.Equal("(interrupted)", result);
    }

    // ---- /plan mode (1.8): schema-level tool filtering + the text-edit safety net ----
    // RunTurnAsync itself needs a live model, so these test the extracted PURE decision (SkipEditsForPlanMode),
    // the notify-without-applying seam (NotifyEditsSkippedForPlanMode), and the mutating-tool guard (ExecuteTool)
    // directly — the three pieces RunTurnAsync composes to make plan mode safe.

    [Fact]
    public void PlanToolsJson_is_exactly_read_and_grep()
    {
        Assert.Equal(2, CodeAgent.PlanToolsJson.Length);
        Assert.Same(CodeAgent.ReadSchema, CodeAgent.PlanToolsJson[0]);
        Assert.Same(CodeAgent.GrepSchema, CodeAgent.PlanToolsJson[1]);
    }

    [Fact]
    public void SkipEditsForPlanMode_skips_only_when_in_plan_mode_with_blocks_present()
    {
        Assert.True(CodeAgent.SkipEditsForPlanMode(planMode: true, editBlockCount: 1));
        Assert.False(CodeAgent.SkipEditsForPlanMode(planMode: true, editBlockCount: 0));
        Assert.False(CodeAgent.SkipEditsForPlanMode(planMode: false, editBlockCount: 1));
        Assert.False(CodeAgent.SkipEditsForPlanMode(planMode: false, editBlockCount: 0));
    }

    [Fact]
    public void NotifyEditsSkippedForPlanMode_never_touches_disk_and_reports_the_plan_mode_note()
    {
        var dir = Path.Combine(Path.GetTempPath(), "doki-ca-plan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "x.txt");
            File.WriteAllText(file, "a\nb\nc\n");
            var blocks = CodeEdit.ParseSearchReplaceBlocks("x.txt\n<<<<<<< SEARCH\nb\n=======\nB\n>>>>>>> REPLACE\n");

            var toolLines = new System.Collections.Generic.List<string>();
            var resultLines = new System.Collections.Generic.List<(string name, string result)>();
            var result = CodeAgent.NotifyEditsSkippedForPlanMode(blocks, toolLines.Add, (n, r) => resultLines.Add((n, r)));

            Assert.Equal("a\nb\nc\n", File.ReadAllText(file));   // untouched — never applied
            Assert.Equal(CodeAgent.PlanModeEditNote, result);
            Assert.Single(toolLines);
            Assert.Contains("Edit(x.txt)", toolLines[0]);
            Assert.Single(resultLines);
            Assert.Equal(("Edit", CodeAgent.PlanModeEditNote), resultLines[0]);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void ExecuteTool_blocks_mutating_tools_in_plan_mode_without_prompting_or_running()
    {
        var approveCalls = 0;
        CodeAgent.ApprovalDecision Approve(CodeAgent.PendingAction a) { approveCalls++; return CodeAgent.ApprovalDecision.Once; }
        var always = new System.Collections.Generic.HashSet<string>();

        var editJson = JsonSerializer.Serialize(new { path = "nope.txt", search = "a", replace = "b" });
        var editResult = CodeAgent.ExecuteTool(Directory.GetCurrentDirectory(), "Edit", editJson, Approve, always, CancellationToken.None, planMode: true);
        Assert.Equal("plan mode: Edit is disabled — /act to enable", editResult);

        var writeResult = CodeAgent.ExecuteTool(Directory.GetCurrentDirectory(), "Write",
            JsonSerializer.Serialize(new { path = "nope.txt", content = "x" }), Approve, always, CancellationToken.None, planMode: true);
        Assert.Equal("plan mode: Write is disabled — /act to enable", writeResult);

        var bashResult = CodeAgent.ExecuteTool(Directory.GetCurrentDirectory(), "Bash",
            JsonSerializer.Serialize(new { command = "echo hi" }), Approve, always, CancellationToken.None, planMode: true);
        Assert.Equal("plan mode: Bash is disabled — /act to enable", bashResult);

        Assert.Equal(0, approveCalls);   // never even asked
        Assert.False(File.Exists(Path.Combine(Directory.GetCurrentDirectory(), "nope.txt")));
    }

    [Fact]
    public void ExecuteTool_still_runs_read_only_tools_in_plan_mode()
    {
        var dir = Path.Combine(Path.GetTempPath(), "doki-ca-plan-ro-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "x.txt");
            File.WriteAllText(file, "hello\n");
            var readResult = CodeAgent.ExecuteTool(dir, "Read", JsonSerializer.Serialize(new { path = "x.txt" }),
                _ => CodeAgent.ApprovalDecision.Once, new System.Collections.Generic.HashSet<string>(), CancellationToken.None, planMode: true);
            Assert.Contains("hello", readResult);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ---- opt-in WebSearch/MemoryRecall tools (1.10): closes the Crush/CC asymmetry, default OFF ----

    [Fact]
    public void ExtendedToolsJson_is_the_base_five_plus_WebSearch_and_MemoryRecall()
    {
        Assert.Equal(5, CodeAgent.ToolsJson.Length);
        Assert.Equal(7, CodeAgent.ExtendedToolsJson.Length);
        for (var i = 0; i < CodeAgent.ToolsJson.Length; i++)
            Assert.Same(CodeAgent.ToolsJson[i], CodeAgent.ExtendedToolsJson[i]);   // base five untouched, same order
        Assert.Same(CodeAgent.WebSearchToolSchema, CodeAgent.ExtendedToolsJson[5]);
        Assert.Same(CodeAgent.MemoryRecallSchema, CodeAgent.ExtendedToolsJson[6]);
    }

    [Fact]
    public void WebSearchToolSchema_and_MemoryRecallSchema_are_well_formed_and_CC_named()
    {
        var wsFn = JsonDocument.Parse(JsonSerializer.Serialize(CodeAgent.WebSearchToolSchema)).RootElement.GetProperty("function");
        Assert.Equal("WebSearch", wsFn.GetProperty("name").GetString());
        var wsRequired = wsFn.GetProperty("parameters").GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(new[] { "query" }, wsRequired);

        var mrFn = JsonDocument.Parse(JsonSerializer.Serialize(CodeAgent.MemoryRecallSchema)).RootElement.GetProperty("function");
        Assert.Equal("MemoryRecall", mrFn.GetProperty("name").GetString());
        Assert.Empty(mrFn.GetProperty("parameters").GetProperty("required").EnumerateArray());   // takes no arguments
    }

    [Fact]
    public void SelectTools_plan_mode_always_wins_even_with_web_tools_on()
    {
        Assert.Same(CodeAgent.PlanToolsJson, CodeAgent.SelectTools(planMode: true, webTools: true));
        Assert.Same(CodeAgent.PlanToolsJson, CodeAgent.SelectTools(planMode: true, webTools: false));
    }

    [Fact]
    public void SelectTools_web_tools_toggles_between_the_base_and_extended_set()
    {
        Assert.Same(CodeAgent.ToolsJson, CodeAgent.SelectTools(planMode: false, webTools: false));
        Assert.Same(CodeAgent.ExtendedToolsJson, CodeAgent.SelectTools(planMode: false, webTools: true));
    }

    [Theory]
    [InlineData("WebSearch", "websearch")]
    [InlineData("web_search", "websearch")]
    [InlineData("Web_Search", "websearch")]
    [InlineData("MemoryRecall", "memoryrecall")]
    [InlineData("memory_recall", "memoryrecall")]
    [InlineData("Read", "read")]
    [InlineData(null, "")]
    public void NormalizeToolName_lowercases_and_strips_underscores(string? name, string expected)
        => Assert.Equal(expected, CodeAgent.NormalizeToolName(name));

    [Fact]
    public void ExecuteTool_dispatches_WebSearch_and_never_falls_through_to_unknown_tool()
    {
        // web_search is network-backed (uvx ddgs) — bounded by WebSearch.SearchAsync's own 20s cap either way, and
        // its executor never throws, so this assertion holds whether the sidecar is up (real results) or down
        // (the "web_search unavailable: ..." degrade text) — it must simply never be the generic unknown-tool text.
        var result = CodeAgent.ExecuteTool(Directory.GetCurrentDirectory(), "WebSearch",
            JsonSerializer.Serialize(new { query = "doki code agent test" }),
            _ => CodeAgent.ApprovalDecision.Once, new System.Collections.Generic.HashSet<string>(), CancellationToken.None);
        Assert.DoesNotContain("unknown tool", result, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(result));
    }

    [Fact]
    public void ExecuteTool_dispatches_MemoryRecall_and_degrades_gracefully()
    {
        var result = CodeAgent.ExecuteTool(Directory.GetCurrentDirectory(), "MemoryRecall", null,
            _ => CodeAgent.ApprovalDecision.Once, new System.Collections.Generic.HashSet<string>(), CancellationToken.None);
        Assert.DoesNotContain("unknown tool", result, StringComparison.OrdinalIgnoreCase);
        // Content depends on whatever is (or isn't) saved in the local memory store, but the shape is always one
        // of these two — RunMemoryRecallTool never throws and never returns anything else.
        Assert.True(result.Contains("memory note(s):") || result == "No long-term memory notes are saved yet.");
    }

    [Fact]
    public void ExecuteTool_recognizes_the_underscore_variant_of_MemoryRecall_too()
    {
        var result = CodeAgent.ExecuteTool(Directory.GetCurrentDirectory(), "memory_recall", null,
            _ => CodeAgent.ApprovalDecision.Once, new System.Collections.Generic.HashSet<string>(), CancellationToken.None);
        Assert.DoesNotContain("unknown tool", result, StringComparison.OrdinalIgnoreCase);
    }
}
