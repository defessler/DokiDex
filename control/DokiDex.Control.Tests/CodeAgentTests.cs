using System;
using System.IO;
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
}
