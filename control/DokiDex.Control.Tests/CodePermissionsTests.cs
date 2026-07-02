using System;
using System.Collections.Generic;
using System.IO;
using DokiDex.Web;
using Xunit;

namespace DokiDex.Control.Tests;

// 1.5 — permission rules. CodePermissions is `internal` (InternalsVisibleTo, same seam as CodeSessions/
// CodeAgent.RunBash) so these drive TryParse/Matches/Decide/persistence directly. Deny-wins-over-allow is the
// load-bearing security property (Decide) — tested explicitly below.
public class CodePermissionsTests
{
    // ---- TryParse / IsValidRule ----

    [Fact]
    public void TryParse_reads_a_bare_tool_wide_rule()
    {
        Assert.True(CodePermissions.TryParse("Read", out var tool, out var spec));
        Assert.Equal("Read", tool);
        Assert.Null(spec);
    }

    [Fact]
    public void TryParse_reads_a_specifier_rule()
    {
        Assert.True(CodePermissions.TryParse("Bash(git status)", out var tool, out var spec));
        Assert.Equal("Bash", tool);
        Assert.Equal("git status", spec);
    }

    [Theory]
    [InlineData("Bash(")]              // unclosed paren
    [InlineData("Bash)")]               // stray close, no open
    [InlineData("")]                    // empty
    [InlineData("   ")]                 // blank
    [InlineData("Bash(foo(bar))")]      // nested paren inside specifier
    [InlineData("(no tool)")]           // empty tool name
    public void TryParse_rejects_malformed_rules(string rule)
        => Assert.False(CodePermissions.TryParse(rule, out _, out _));

    [Fact]
    public void IsValidRule_mirrors_TryParse()
    {
        Assert.True(CodePermissions.IsValidRule("Bash(dotnet test *)"));
        Assert.False(CodePermissions.IsValidRule("Bash("));
    }

    // ---- Matches: exact ----

    [Fact]
    public void Matches_exact_specifier_matches_only_that_literal_command()
    {
        Assert.True(CodePermissions.Matches("Bash(git status)", "Bash", "git status"));
        Assert.False(CodePermissions.Matches("Bash(git status)", "Bash", "git status -s"));
        Assert.False(CodePermissions.Matches("Bash(git status)", "Bash", "git statusx"));
    }

    [Fact]
    public void Matches_specifier_compare_is_case_sensitive()
        => Assert.False(CodePermissions.Matches("Bash(git status)", "Bash", "Git Status"));

    // ---- Matches: tool-wide ----

    [Fact]
    public void Matches_bare_tool_rule_matches_any_specifier_for_that_tool()
    {
        Assert.True(CodePermissions.Matches("Bash", "Bash", "rm -rf /"));
        Assert.True(CodePermissions.Matches("Edit", "Edit", "any/path.cs"));
    }

    [Fact]
    public void Matches_tool_name_compare_is_case_insensitive()
    {
        Assert.True(CodePermissions.Matches("bash", "Bash", "anything"));
        Assert.True(CodePermissions.Matches("BASH(git status)", "bash", "git status"));
    }

    [Fact]
    public void Matches_bare_tool_rule_does_not_match_a_different_tool()
        => Assert.False(CodePermissions.Matches("Read", "Edit", "a.cs"));

    // ---- Matches: trailing-star prefix (the load-bearing gotcha) ----

    [Fact]
    public void Matches_prefix_rule_matches_a_command_starting_with_the_prefix()
        => Assert.True(CodePermissions.Matches("Bash(dotnet test *)", "Bash", "dotnet test src/Foo.csproj"));

    [Fact]
    public void Matches_prefix_rule_requires_something_after_the_prefix()
        // Literal spec reading: "Bash(dotnet test *)" matches any command STARTING "dotnet test " (note the
        // trailing space) — the bare two-word command with nothing after it does not contain that trailing
        // space, so it does not match. (A user who wants the bare form too adds a separate exact rule for it.)
        => Assert.False(CodePermissions.Matches("Bash(dotnet test *)", "Bash", "dotnet test"));

    [Fact]
    public void Matches_prefix_rule_requires_a_word_boundary_after_the_prefix()
    {
        // THE gotcha explicitly called out by the plan: "git diff *" must NOT match "git diff-index" — the
        // character right after "git diff" is '-', not a space, so this is a different command entirely.
        Assert.False(CodePermissions.Matches("Bash(git diff *)", "Bash", "git diff-index"));
        Assert.True(CodePermissions.Matches("Bash(git diff *)", "Bash", "git diff HEAD~1"));
    }

    [Fact]
    public void Matches_prefix_rule_does_not_match_an_unrelated_command()
        => Assert.False(CodePermissions.Matches("Bash(dotnet test *)", "Bash", "dotnet build"));

    // ---- Matches: malformed rules are inert ----

    [Fact]
    public void Matches_a_malformed_rule_never_matches_and_never_throws()
    {
        Assert.False(CodePermissions.Matches("Bash(", "Bash", "anything"));
        Assert.False(CodePermissions.Matches("", "Bash", "anything"));
    }

    // ---- Decide: deny-wins-over-allow (the load-bearing security property) ----

    [Fact]
    public void Decide_returns_Ask_when_no_rule_matches()
        => Assert.Equal(CodePermissions.Decision.Ask,
            CodePermissions.Decide(CodePermissions.Rules.Empty, "Bash", "ls"));

    [Fact]
    public void Decide_returns_Allow_when_only_an_allow_rule_matches()
    {
        var rules = new CodePermissions.Rules(new List<string> { "Bash(dotnet test *)" }, new List<string>());
        Assert.Equal(CodePermissions.Decision.Allow, CodePermissions.Decide(rules, "Bash", "dotnet test x"));
    }

    [Fact]
    public void Decide_returns_Deny_when_only_a_deny_rule_matches()
    {
        var rules = new CodePermissions.Rules(new List<string>(), new List<string> { "Read(*.env)" });
        // Read(*.env) is not a prefix rule (no trailing " *") so it's actually an EXACT match on "*.env" here —
        // this test just checks the deny path fires for a matching exact specifier.
        var r2 = new CodePermissions.Rules(new List<string>(), new List<string> { "Bash(rm -rf *)" });
        Assert.Equal(CodePermissions.Decision.Deny, CodePermissions.Decide(r2, "Bash", "rm -rf /important"));
    }

    [Fact]
    public void Decide_deny_wins_even_when_the_SAME_call_also_matches_an_allow_rule()
    {
        // The load-bearing case: a tool-wide allow ("Bash") plus a specific deny both match this call — deny
        // must win. This is exactly the shape a security-conscious user relies on: "allow Bash generally, but
        // never this one destructive command."
        var rules = new CodePermissions.Rules(
            new List<string> { "Bash" },
            new List<string> { "Bash(rm -rf *)" });
        Assert.Equal(CodePermissions.Decision.Deny, CodePermissions.Decide(rules, "Bash", "rm -rf /"));
        // ...but an unrelated Bash command under the same rule set is still Allow (deny didn't blanket-deny Bash).
        Assert.Equal(CodePermissions.Decision.Allow, CodePermissions.Decide(rules, "Bash", "ls -la"));
    }

    [Fact]
    public void FindMatchingRule_returns_the_specific_rule_that_fired()
    {
        var deny = new List<string> { "Read(*.env)", "Bash(rm -rf *)" };
        Assert.Equal("Bash(rm -rf *)", CodePermissions.FindMatchingRule(deny, "Bash", "rm -rf /"));
        Assert.Null(CodePermissions.FindMatchingRule(deny, "Bash", "ls"));
    }

    // ---- editing: AddAllow / AddDeny / RemoveAt / List ----

    [Fact]
    public void AddAllow_appends_without_duplicating()
    {
        var rules = CodePermissions.AddAllow(CodePermissions.Rules.Empty, "Read");
        rules = CodePermissions.AddAllow(rules, "Read");
        Assert.Single(rules.Allow);
    }

    [Fact]
    public void List_numbers_allow_rules_then_deny_rules_together()
    {
        var rules = new CodePermissions.Rules(
            new List<string> { "Read", "Bash(dotnet test *)" },
            new List<string> { "Read(*.env)" });
        var numbered = CodePermissions.List(rules);
        Assert.Equal(3, numbered.Count);
        Assert.Equal((1, false, "Read"), (numbered[0].Index, numbered[0].IsDeny, numbered[0].Rule));
        Assert.Equal((2, false, "Bash(dotnet test *)"), (numbered[1].Index, numbered[1].IsDeny, numbered[1].Rule));
        Assert.Equal((3, true, "Read(*.env)"), (numbered[2].Index, numbered[2].IsDeny, numbered[2].Rule));
    }

    [Fact]
    public void RemoveAt_removes_the_rule_at_that_display_index()
    {
        var rules = new CodePermissions.Rules(
            new List<string> { "Read", "Edit" },
            new List<string> { "Read(*.env)" });
        var (ok, updated) = CodePermissions.RemoveAt(rules, 2);   // "Edit"
        Assert.True(ok);
        Assert.Equal(new[] { "Read" }, updated.Allow);
        Assert.Equal(new[] { "Read(*.env)" }, updated.Deny);
    }

    [Fact]
    public void RemoveAt_returns_false_and_unchanged_rules_for_an_out_of_range_index()
    {
        var rules = new CodePermissions.Rules(new List<string> { "Read" }, new List<string>());
        var (ok, updated) = CodePermissions.RemoveAt(rules, 99);
        Assert.False(ok);
        Assert.Same(rules, updated);
    }

    // ---- persistence: round trip via the sessionsRoot-style test seam ----

    [Fact]
    public void FilePath_nests_under_permissionsRoot_by_workspace_hash()
    {
        var root = @"C:\projects\dokidex";
        var expected = Path.Combine(@"C:\scratch\perms", CodeSessions.Hash(root) + ".json");
        Assert.Equal(expected, CodePermissions.FilePath(root, @"C:\scratch\perms"));
    }

    [Fact]
    public void Save_then_Load_round_trips_allow_and_deny_rules()
    {
        var dir = NewTempDir();
        try
        {
            var workspace = @"C:\fake\ws-perm-1";
            var rules = new CodePermissions.Rules(
                new List<string> { "Read", "Bash(dotnet test *)" },
                new List<string> { "Read(*.env)" });
            Assert.True(CodePermissions.Save(workspace, rules, dir));

            var loaded = CodePermissions.Load(workspace, dir);
            Assert.Equal(rules.Allow, loaded.Allow);
            Assert.Equal(rules.Deny, loaded.Deny);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Save_writes_the_lowercase_allow_deny_json_shape()
    {
        var dir = NewTempDir();
        try
        {
            var workspace = @"C:\fake\ws-perm-2";
            var rules = new CodePermissions.Rules(new List<string> { "Read" }, new List<string>());
            CodePermissions.Save(workspace, rules, dir);

            var json = File.ReadAllText(CodePermissions.FilePath(workspace, dir));
            Assert.Contains("\"allow\"", json);
            Assert.Contains("\"deny\"", json);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Load_returns_empty_rules_when_no_file_exists_yet()
    {
        var dir = NewTempDir();
        try
        {
            var loaded = CodePermissions.Load(@"C:\fake\never-saved", dir);
            Assert.Empty(loaded.Allow);
            Assert.Empty(loaded.Deny);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Load_degrades_to_empty_rules_for_a_corrupt_file()
    {
        var dir = NewTempDir();
        try
        {
            var workspace = @"C:\fake\ws-perm-3";
            Directory.CreateDirectory(dir);
            File.WriteAllText(CodePermissions.FilePath(workspace, dir), "{ not json");
            var loaded = CodePermissions.Load(workspace, dir);
            Assert.Empty(loaded.Allow);
            Assert.Empty(loaded.Deny);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Save_overwrites_the_same_workspace_file_rather_than_creating_a_new_one()
    {
        var dir = NewTempDir();
        try
        {
            var workspace = @"C:\fake\ws-perm-4";
            CodePermissions.Save(workspace, new CodePermissions.Rules(new List<string> { "Read" }, new List<string>()), dir);
            CodePermissions.Save(workspace, new CodePermissions.Rules(new List<string> { "Read", "Edit" }, new List<string>()), dir);

            var loaded = CodePermissions.Load(workspace, dir);
            Assert.Equal(new[] { "Read", "Edit" }, loaded.Allow);
        }
        finally { Cleanup(dir); }
    }

    private static string NewTempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "doki-perms-test-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(d);
        return d;
    }

    private static void Cleanup(string dir) { try { Directory.Delete(dir, true); } catch { } }
}
