using System;
using System.IO;
using DokiDex.Web;
using Xunit;

namespace DokiDex.Control.Tests;

// 1.9 — custom slash commands. CodeCommands is `internal` (InternalsVisibleTo, same seam as CodeSessions/
// CodePermissions) so these drive ExpandTemplate/CommandNameFromPath/Discover directly. Discover's `globalRoot`
// override (mirroring CodeSessions/CodePermissions' own disk-touching seams) keeps every test off the real
// %USERPROFILE%\.doki\commands.
public class CodeCommandsTests
{
    // ---- ExpandTemplate ----

    [Fact]
    public void ExpandTemplate_replaces_ARGUMENTS_with_empty_string_when_no_args_given()
    {
        var result = CodeCommands.ExpandTemplate("Say exactly: $ARGUMENTS", "");
        Assert.Equal("Say exactly: ", result);
    }

    [Fact]
    public void ExpandTemplate_replaces_a_single_occurrence()
    {
        var result = CodeCommands.ExpandTemplate("Say exactly: $ARGUMENTS", "hello world");
        Assert.Equal("Say exactly: hello world", result);
    }

    [Fact]
    public void ExpandTemplate_replaces_every_occurrence()
    {
        var result = CodeCommands.ExpandTemplate("$ARGUMENTS and again $ARGUMENTS", "x");
        Assert.Equal("x and again x", result);
    }

    [Fact]
    public void ExpandTemplate_leaves_a_template_with_no_token_completely_unchanged()
    {
        // Spec: v1 does NOT append the arguments anywhere the template didn't explicitly ask for them.
        var result = CodeCommands.ExpandTemplate("Just review the diff.", "hello world");
        Assert.Equal("Just review the diff.", result);
    }

    // ---- CommandNameFromPath ----

    [Theory]
    [InlineData(@"C:\ws\.doki\commands\review.md", "review")]
    [InlineData(@"C:\ws\.doki\commands\Changelog.MD", "changelog")]
    [InlineData("echo-test.md", "echo-test")]
    public void CommandNameFromPath_strips_extension_and_lowercases(string path, string expected)
        => Assert.Equal(expected, CodeCommands.CommandNameFromPath(path));

    // ---- Discover ----

    [Fact]
    public void Discover_finds_a_workspace_local_command()
    {
        var ws = NewTempDir();
        var global = NewTempDir();
        try
        {
            var cmdDir = Path.Combine(ws, ".doki", "commands");
            Directory.CreateDirectory(cmdDir);
            File.WriteAllText(Path.Combine(cmdDir, "review.md"), "Review: $ARGUMENTS");

            var found = CodeCommands.Discover(ws, global);
            Assert.True(found.ContainsKey("review"));
            Assert.Equal(Path.Combine(cmdDir, "review.md"), found["review"]);
        }
        finally { Cleanup(ws); Cleanup(global); }
    }

    [Fact]
    public void Discover_finds_a_global_command_when_no_workspace_one_exists()
    {
        var ws = NewTempDir();
        var global = NewTempDir();
        try
        {
            Directory.CreateDirectory(global);
            File.WriteAllText(Path.Combine(global, "changelog.md"), "Summarize recent changes.");

            var found = CodeCommands.Discover(ws, global);
            Assert.True(found.ContainsKey("changelog"));
            Assert.Equal(Path.Combine(global, "changelog.md"), found["changelog"]);
        }
        finally { Cleanup(ws); Cleanup(global); }
    }

    [Fact]
    public void Discover_workspace_local_shadows_a_global_command_of_the_same_name()
    {
        var ws = NewTempDir();
        var global = NewTempDir();
        try
        {
            var cmdDir = Path.Combine(ws, ".doki", "commands");
            Directory.CreateDirectory(cmdDir);
            Directory.CreateDirectory(global);
            File.WriteAllText(Path.Combine(cmdDir, "review.md"), "workspace version");
            File.WriteAllText(Path.Combine(global, "review.md"), "global version");

            var found = CodeCommands.Discover(ws, global);
            Assert.Equal(Path.Combine(cmdDir, "review.md"), found["review"]);
        }
        finally { Cleanup(ws); Cleanup(global); }
    }

    [Fact]
    public void Discover_returns_empty_when_neither_root_exists()
    {
        var ws = NewTempDir();
        var missingGlobal = Path.Combine(ws, "no-such-global-dir");
        try
        {
            var found = CodeCommands.Discover(ws, missingGlobal);
            Assert.Empty(found);
        }
        finally { Cleanup(ws); }
    }

    [Fact]
    public void Discover_normalizes_command_names_to_lowercase()
    {
        var ws = NewTempDir();
        var global = NewTempDir();
        try
        {
            var cmdDir = Path.Combine(ws, ".doki", "commands");
            Directory.CreateDirectory(cmdDir);
            File.WriteAllText(Path.Combine(cmdDir, "Changelog.md"), "text");

            var found = CodeCommands.Discover(ws, global);
            Assert.True(found.ContainsKey("changelog"));
        }
        finally { Cleanup(ws); Cleanup(global); }
    }

    [Fact]
    public void Discover_ignores_non_markdown_files()
    {
        var ws = NewTempDir();
        var global = NewTempDir();
        try
        {
            var cmdDir = Path.Combine(ws, ".doki", "commands");
            Directory.CreateDirectory(cmdDir);
            File.WriteAllText(Path.Combine(cmdDir, "notes.txt"), "not a command");

            var found = CodeCommands.Discover(ws, global);
            Assert.Empty(found);
        }
        finally { Cleanup(ws); Cleanup(global); }
    }

    private static string NewTempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "doki-commands-test-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(d);
        return d;
    }

    private static void Cleanup(string dir) { try { Directory.Delete(dir, true); } catch { } }
}
