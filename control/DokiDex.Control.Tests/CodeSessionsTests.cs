using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DokiDex.Web;
using Xunit;

namespace DokiDex.Control.Tests;

// 1.4 — session persistence. CodeSessions is `internal` (InternalsVisibleTo, same seam pattern as LocalLlm.Body /
// CodeAgent.RunBash) so these drive Hash/SessionDir/Save/Load/List/ExportMarkdown directly. Every disk-touching
// method takes an optional `sessionsRoot` override so tests never touch the real %USERPROFILE%\.doki\sessions.
public class CodeSessionsTests
{
    private static object Sys(string content) => new { role = "system", content };
    private static object Usr(string content) => new { role = "user", content };
    private static object Asst(string content) => new { role = "assistant", content };
    private static object ToolResult(string name, string content, string id = "call_0") => new { role = "tool", tool_call_id = id, name, content };
    private static object ToolCallTurn(string id, string name, string argsJson) => new
    {
        role = "assistant",
        content = (string?)null,
        tool_calls = new object[] { new { id, type = "function", function = new { name, arguments = argsJson } } },
    };

    // A representative transcript covering every shape 1.4's spec calls out: system, orientation-system, user,
    // an assistant tool-call turn (content:null + tool_calls[]), a role:"tool" result, and a final assistant text.
    private static List<object> RepresentativeWorking() => new()
    {
        Sys("You are doki code."),
        Sys("[workspace]\nsome orientation text"),
        Usr("please fix the bug"),
        ToolCallTurn("call_0", "Read", "{\"path\":\"a.cs\"}"),
        ToolResult("Read", "1\tfoo\n2\tbar\n"),
        Asst("Done — fixed it."),
    };

    // ---- Hash ----

    [Fact]
    public void Hash_is_stable_for_the_same_root()
    {
        var root = @"C:\projects\dokidex";
        Assert.Equal(CodeSessions.Hash(root), CodeSessions.Hash(root));
    }

    [Fact]
    public void Hash_is_insensitive_to_case_and_trailing_separator()
    {
        var a = CodeSessions.Hash(@"C:\projects\dokidex");
        var b = CodeSessions.Hash(@"C:\PROJECTS\DokiDex\");
        Assert.Equal(a, b);
    }

    [Fact]
    public void Hash_differs_for_different_roots()
    {
        Assert.NotEqual(CodeSessions.Hash(@"C:\projects\a"), CodeSessions.Hash(@"C:\projects\b"));
    }

    [Fact]
    public void Hash_is_12_lowercase_hex_chars()
    {
        var h = CodeSessions.Hash(@"C:\projects\dokidex");
        Assert.Equal(12, h.Length);
        Assert.Matches("^[0-9a-f]{12}$", h);
    }

    // ---- SessionDir ----

    [Fact]
    public void SessionDir_nests_under_sessionsRoot_by_hash()
    {
        var root = @"C:\projects\dokidex";
        var expected = Path.Combine(@"C:\scratch\sessions", CodeSessions.Hash(root));
        Assert.Equal(expected, CodeSessions.SessionDir(root, @"C:\scratch\sessions"));
    }

    // ---- Save / Load round trip ----

    [Fact]
    public void Save_then_Load_round_trips_workspace_model_and_message_count()
    {
        var dir = NewTempDir();
        try
        {
            var working = RepresentativeWorking();
            var workspace = @"C:\fake\workspace";
            Assert.True(CodeSessions.Save(workspace, "20260701-120000", working, "coder-fast", dir));

            var loaded = CodeSessions.Load(Path.Combine(CodeSessions.SessionDir(workspace, dir), "20260701-120000.json"));
            Assert.NotNull(loaded);
            Assert.Equal(Path.GetFullPath(workspace), loaded!.Workspace);
            Assert.Equal("coder-fast", loaded.Model);
            Assert.Equal(working.Count, loaded.Working.Count);
            Assert.All(loaded.Working, m => Assert.IsType<JsonElement>(m));
        }
        finally { Cleanup(dir); }
    }

    // THE critical acceptance test (F3-R3): the request body LocalLlm's tool-calling path would send must be
    // BYTE-IDENTICAL whether built from the live `working` list or from the same list reloaded off disk — proving
    // the JsonElement round-trip is transparent to LocalLlm's re-serialization.
    [Fact]
    public void Reloaded_session_reserializes_to_the_byte_identical_request_body()
    {
        var dir = NewTempDir();
        try
        {
            var working = RepresentativeWorking();
            var bodyBefore = JsonSerializer.Serialize(
                LocalLlm.Body(working.ToArray(), CodeAgent.ToolTemperature, CodeAgent.MaxTokens, "coder-fast", minP: 0.1, topP: 0.9));

            var workspace = @"C:\fake\workspace2";
            Assert.True(CodeSessions.Save(workspace, "20260701-130000", working, "coder-fast", dir));
            var loaded = CodeSessions.LoadLatest(workspace, dir);
            Assert.NotNull(loaded);

            var bodyAfter = JsonSerializer.Serialize(
                LocalLlm.Body(loaded!.Working.ToArray(), CodeAgent.ToolTemperature, CodeAgent.MaxTokens, "coder-fast", minP: 0.1, topP: 0.9));

            Assert.Equal(bodyBefore, bodyAfter);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Save_overwrites_the_same_session_file_rather_than_creating_a_new_one()
    {
        var dir = NewTempDir();
        try
        {
            var workspace = @"C:\fake\workspace3";
            Assert.True(CodeSessions.Save(workspace, "20260701-140000", new List<object> { Usr("first") }, "coder-fast", dir));
            Assert.True(CodeSessions.Save(workspace, "20260701-140000", new List<object> { Usr("first"), Asst("second") }, "coder-fast", dir));

            var files = Directory.GetFiles(CodeSessions.SessionDir(workspace, dir), "*.json");
            Assert.Single(files);
            var loaded = CodeSessions.Load(files[0]);
            Assert.Equal(2, loaded!.Working.Count);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Save_returns_false_and_does_not_throw_on_an_unusable_path()
    {
        // A NUL char makes Path.Combine/GetFullPath throw inside Save's try/catch — must degrade to false, never throw.
        var ok = CodeSessions.Save(@"C:\fake\workspace", "bad\0id", new List<object> { Usr("x") }, "coder-fast", "C:\\scratch");
        Assert.False(ok);
    }

    [Fact]
    public void Load_returns_null_for_a_missing_file()
        => Assert.Null(CodeSessions.Load(@"C:\nope\does-not-exist.json"));

    [Fact]
    public void Load_returns_null_for_a_corrupt_file()
    {
        var dir = NewTempDir();
        try
        {
            var f = Path.Combine(dir, "bad.json");
            File.WriteAllText(f, "{ not json");
            Assert.Null(CodeSessions.Load(f));
        }
        finally { Cleanup(dir); }
    }

    // ---- LoadLatest ----

    [Fact]
    public void LoadLatest_returns_null_when_no_sessions_exist()
    {
        var dir = NewTempDir();
        try { Assert.Null(CodeSessions.LoadLatest(@"C:\fake\nothing-here", dir)); }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void LoadLatest_returns_the_most_recently_saved_session()
    {
        var dir = NewTempDir();
        try
        {
            var workspace = @"C:\fake\workspace4";
            CodeSessions.Save(workspace, "20260701-090000", new List<object> { Usr("older") }, "coder-fast", dir);
            CodeSessions.Save(workspace, "20260701-100000", new List<object> { Usr("newer") }, "coder-fast", dir);

            var latest = CodeSessions.LoadLatest(workspace, dir);
            Assert.Equal("20260701-100000", latest!.Id);
        }
        finally { Cleanup(dir); }
    }

    // ---- List ----

    [Fact]
    public void List_returns_empty_for_a_workspace_with_no_sessions()
    {
        var dir = NewTempDir();
        try { Assert.Empty(CodeSessions.List(@"C:\fake\nothing-at-all", dir)); }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void List_is_newest_first_with_count_and_first_user_snippet()
    {
        var dir = NewTempDir();
        try
        {
            var workspace = @"C:\fake\workspace5";
            CodeSessions.Save(workspace, "20260701-090000", new List<object> { Sys("prompt"), Usr("older task") }, "coder-fast", dir);
            CodeSessions.Save(workspace, "20260701-110000", new List<object> { Sys("prompt"), Usr("newer task"), Asst("ok") }, "coder-fast", dir);

            var list = CodeSessions.List(workspace, dir);
            Assert.Equal(2, list.Count);
            Assert.Equal("20260701-110000", list[0].Id);   // newest first
            Assert.Equal(3, list[0].MessageCount);
            Assert.Equal("newer task", list[0].FirstUserSnippet);
            Assert.Equal("20260701-090000", list[1].Id);
            Assert.Equal("older task", list[1].FirstUserSnippet);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void List_clips_the_snippet_at_60_chars_with_an_ellipsis()
    {
        var dir = NewTempDir();
        try
        {
            var workspace = @"C:\fake\workspace6";
            var longTask = new string('x', 200);
            CodeSessions.Save(workspace, "20260701-090000", new List<object> { Usr(longTask) }, "coder-fast", dir);

            var snippet = CodeSessions.List(workspace, dir)[0].FirstUserSnippet;
            Assert.Equal(61, snippet.Length);   // 60 chars + the ellipsis mark
            Assert.EndsWith("…", snippet);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void List_skips_a_corrupt_session_file_rather_than_failing_the_whole_listing()
    {
        var dir = NewTempDir();
        try
        {
            var workspace = @"C:\fake\workspace7";
            CodeSessions.Save(workspace, "20260701-090000", new List<object> { Usr("good") }, "coder-fast", dir);
            File.WriteAllText(Path.Combine(CodeSessions.SessionDir(workspace, dir), "20260701-100000.json"), "{ not json");

            var list = CodeSessions.List(workspace, dir);
            Assert.Single(list);
            Assert.Equal("good", list[0].FirstUserSnippet);
        }
        finally { Cleanup(dir); }
    }

    // ---- SummarizeMessages (pure) ----

    [Fact]
    public void SummarizeMessages_returns_zero_and_blank_snippet_for_a_non_array()
    {
        using var doc = JsonDocument.Parse("null");
        var (count, snippet) = CodeSessions.SummarizeMessages(doc.RootElement);
        Assert.Equal(0, count);
        Assert.Equal("", snippet);
    }

    [Fact]
    public void SummarizeMessages_skips_non_user_turns_to_find_the_first_user_snippet()
    {
        using var doc = JsonDocument.Parse(
            "[{\"role\":\"system\",\"content\":\"sys\"},{\"role\":\"user\",\"content\":\"do the thing\"},{\"role\":\"assistant\",\"content\":\"ok\"}]");
        var (count, snippet) = CodeSessions.SummarizeMessages(doc.RootElement);
        Assert.Equal(3, count);
        Assert.Equal("do the thing", snippet);
    }

    // ---- ExportMarkdown ----

    [Fact]
    public void ExportMarkdown_renders_role_headed_sections_for_plain_turns()
    {
        var md = CodeSessions.ExportMarkdown(new List<object> { Usr("do the thing"), Asst("done") });
        Assert.Contains("## User", md);
        Assert.Contains("do the thing", md);
        Assert.Contains("## Assistant", md);
        Assert.Contains("done", md);
    }

    [Fact]
    public void ExportMarkdown_fences_a_tool_result_under_a_named_tool_header()
    {
        var md = CodeSessions.ExportMarkdown(new List<object> { ToolResult("Read", "1\tfoo\n") });
        Assert.Contains("## tool: Read", md);
        Assert.Contains("````", md);
        Assert.Contains("1\tfoo", md);
    }

    [Fact]
    public void ExportMarkdown_summarizes_a_null_content_toolcall_turn_by_called_tool_names()
    {
        var md = CodeSessions.ExportMarkdown(new List<object> { ToolCallTurn("call_0", "Edit", "{}") });
        Assert.Contains("_called Edit_", md);
    }

    [Fact]
    public void ExportMarkdown_is_identical_for_a_reloaded_JsonElement_session_as_for_the_live_one()
    {
        var dir = NewTempDir();
        try
        {
            var working = RepresentativeWorking();
            var mdBefore = CodeSessions.ExportMarkdown(working);

            var workspace = @"C:\fake\workspace8";
            CodeSessions.Save(workspace, "20260701-150000", working, "coder-fast", dir);
            var loaded = CodeSessions.LoadLatest(workspace, dir);

            var mdAfter = CodeSessions.ExportMarkdown(loaded!.Working);
            Assert.Equal(mdBefore, mdAfter);
        }
        finally { Cleanup(dir); }
    }

    private static string NewTempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "doki-sessions-test-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(d);
        return d;
    }
    private static void Cleanup(string dir) { try { Directory.Delete(dir, true); } catch { } }
}
