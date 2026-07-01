using System;
using System.Collections.Generic;
using System.IO;
using DokiDex.Web;
using Xunit;

namespace DokiDex.Control.Tests;

// 1.2 — repo orientation: the PURE seams (tree rendering from an in-memory listing, the git-status line cap,
// text truncation, and the section-omitting message assembly). The disk walk (CodeOrientation.BuildTree) and the
// git process call (RunGitStatus) are thin wrappers around these and get one integration-style smoke test each;
// everything else here is total + side-effect-free, no disk, no git, no model.
public class CodeOrientationTests
{
    // ---- CapText ----

    [Fact]
    public void CapText_leaves_short_text_untouched()
        => Assert.Equal("hello", CodeOrientation.CapText("hello", 100));

    [Fact]
    public void CapText_truncates_and_notes_the_cut()
    {
        var capped = CodeOrientation.CapText(new string('x', 20), 10);
        Assert.StartsWith(new string('x', 10), capped);
        Assert.Contains("truncated", capped);
    }

    // ---- RenderTree ----

    [Fact]
    public void RenderTree_renders_files_and_expandable_dirs()
    {
        var entries = new[]
        {
            new CodeOrientation.TreeEntry("control", true, Children: new[]
            {
                new CodeOrientation.TreeEntry("Web", true, Children: new[]
                {
                    new CodeOrientation.TreeEntry("CodeAgent.cs", false),
                }),
            }),
            new CodeOrientation.TreeEntry("README.md", false),
        };
        var text = CodeOrientation.RenderTree(entries, 2000);
        Assert.Contains("control/", text);
        Assert.Contains("Web/", text);
        Assert.Contains("CodeAgent.cs", text);
        Assert.Contains("README.md", text);
    }

    [Fact]
    public void RenderTree_shows_a_count_for_pruned_depth_instead_of_expanding()
    {
        var entries = new[]
        {
            new CodeOrientation.TreeEntry("serving", true, Pruned: true, PrunedFiles: 14, PrunedDirs: 3),
        };
        var text = CodeOrientation.RenderTree(entries, 2000);
        Assert.Contains("serving/ (14 files, 3 dirs)", text);
    }

    [Fact]
    public void RenderTree_caps_total_output_and_notes_the_truncation()
    {
        var entries = new List<CodeOrientation.TreeEntry>();
        for (var i = 0; i < 500; i++) entries.Add(new CodeOrientation.TreeEntry($"file{i}.txt", false));
        var text = CodeOrientation.RenderTree(entries, 200);
        Assert.True(text.Length < 500, "tree render should have stopped well short of the full 500 entries");
        Assert.Contains("truncated", text);
    }

    [Fact]
    public void RenderTree_of_an_empty_listing_is_empty()
        => Assert.Equal("", CodeOrientation.RenderTree(Array.Empty<CodeOrientation.TreeEntry>(), 2000));

    // ---- FormatGitStatus ----

    [Fact]
    public void FormatGitStatus_of_a_clean_tree_is_empty()
        => Assert.Equal("", CodeOrientation.FormatGitStatus("", CodeOrientation.GitStatusMaxLines));

    [Fact]
    public void FormatGitStatus_passes_through_a_short_status_unchanged()
    {
        var raw = " M control/Web/CodeAgent.cs\n?? docs/scratch.md\n";
        var text = CodeOrientation.FormatGitStatus(raw, 30);
        Assert.Equal(" M control/Web/CodeAgent.cs\n?? docs/scratch.md", text);
    }

    [Fact]
    public void FormatGitStatus_caps_long_status_and_counts_the_rest()
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < 40; i++) sb.Append("?? file").Append(i).Append(".txt\n");
        var text = CodeOrientation.FormatGitStatus(sb.ToString(), 30);
        var lines = text.Split('\n');
        Assert.Equal(31, lines.Length);   // 30 status lines + the "…and N more" line
        Assert.Contains("…and 10 more", text);
    }

    // ---- BuildMessage (section omission) ----

    [Fact]
    public void BuildMessage_assembles_all_three_sections_in_order()
    {
        var msg = CodeOrientation.BuildMessage("purpose: X", "control/\nREADME.md", " M a.cs");
        Assert.Equal(
            "[workspace]\npurpose: X\n\n[structure]\ncontrol/\nREADME.md\n\n[git status]\n M a.cs",
            msg);
    }

    [Fact]
    public void BuildMessage_omits_a_blank_instructions_section()
    {
        var msg = CodeOrientation.BuildMessage(null, "control/", " M a.cs");
        Assert.DoesNotContain("[workspace]", msg);
        Assert.StartsWith("[structure]", msg);
    }

    [Fact]
    public void BuildMessage_omits_a_blank_git_status_section()
    {
        var msg = CodeOrientation.BuildMessage("purpose: X", "control/", "");
        Assert.DoesNotContain("[git status]", msg);
        Assert.EndsWith("control/", msg);
    }

    [Fact]
    public void BuildMessage_of_nothing_is_empty()
        => Assert.Equal("", CodeOrientation.BuildMessage(null, "", "  "));

    // ---- LoadInstructions (first-found precedence) ----

    [Fact]
    public void LoadInstructions_prefers_DOKI_over_AGENTS_over_CLAUDE()
    {
        var dir = MakeTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "CLAUDE.md"), "claude");
            File.WriteAllText(Path.Combine(dir, "AGENTS.md"), "agents");
            File.WriteAllText(Path.Combine(dir, "DOKI.md"), "doki");
            var (name, content) = CodeOrientation.LoadInstructions(dir);
            Assert.Equal("DOKI.md", name);
            Assert.Equal("doki", content);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void LoadInstructions_falls_back_to_AGENTS_then_CLAUDE()
    {
        var dir = MakeTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "CLAUDE.md"), "claude");
            File.WriteAllText(Path.Combine(dir, "AGENTS.md"), "agents");
            var (name, content) = CodeOrientation.LoadInstructions(dir);
            Assert.Equal("AGENTS.md", name);
            Assert.Equal("agents", content);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void LoadInstructions_with_none_present_returns_null()
    {
        var dir = MakeTempDir();
        try
        {
            var (name, content) = CodeOrientation.LoadInstructions(dir);
            Assert.Null(name);
            Assert.Null(content);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void LoadInstructions_caps_a_huge_file()
    {
        var dir = MakeTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "DOKI.md"), new string('x', 20_000));
            var (_, content) = CodeOrientation.LoadInstructions(dir);
            Assert.True(content!.Length < 20_000);
            Assert.Contains("truncated", content);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---- BuildTree (thin disk-walk smoke test) ----

    [Fact]
    public void BuildTree_lists_depth1_and_depth2_and_prunes_depth3()
    {
        var dir = MakeTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "a", "b", "c"));
            File.WriteAllText(Path.Combine(dir, "a", "b", "c", "deep.txt"), "x");
            File.WriteAllText(Path.Combine(dir, "a", "b", "shallow.txt"), "x");
            File.WriteAllText(Path.Combine(dir, "root.txt"), "x");

            var text = CodeOrientation.RenderTree(CodeOrientation.BuildTree(dir), CodeOrientation.TreeCapChars);
            Assert.Contains("a/", text);
            Assert.Contains("b/ (1 files, 1 dirs)", text);   // depth-2 "b" is pruned: 1 file (shallow.txt) + 1 dir (c)
            Assert.Contains("root.txt", text);
            Assert.DoesNotContain("deep.txt", text);          // depth-3 content never listed by name
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void BuildTree_skips_CodeTools_SkipDirs()
    {
        var dir = MakeTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "node_modules"));
            File.WriteAllText(Path.Combine(dir, "node_modules", "pkg.json"), "{}");
            Directory.CreateDirectory(Path.Combine(dir, "src"));

            var text = CodeOrientation.RenderTree(CodeOrientation.BuildTree(dir), CodeOrientation.TreeCapChars);
            Assert.DoesNotContain("node_modules", text);
            Assert.Contains("src/", text);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---- RunGitStatus (thin process-call smoke test) ----

    [Fact]
    public void RunGitStatus_on_a_non_repo_directory_returns_empty()
    {
        var dir = MakeTempDir();
        try { Assert.Equal("", CodeOrientation.RunGitStatus(dir, 10_000)); }
        finally { Directory.Delete(dir, true); }
    }

    // ---- Build (end-to-end smoke, against THIS repo) ----

    [Fact]
    public void Build_against_the_real_repo_stays_under_the_caps_and_has_structure()
    {
        var root = RepoRoot();
        var loaded = CodeOrientation.Build(root);
        Assert.Contains("[structure]", loaded.Message);
        // this repo is a git working tree — a status section is expected too (possibly clean, but the test repo
        // checkout for CI/dev is rarely bit-for-bit clean; if it ever is, this assertion is the one to relax).
        Assert.True(loaded.Message.Length < CodeOrientation.InstructionsCapChars + CodeOrientation.TreeCapChars + 4000);
    }

    private static string RepoRoot()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir is not null && !Directory.Exists(Path.Combine(dir, ".git")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? Directory.GetCurrentDirectory();
    }

    private static string MakeTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "doki-orient-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
