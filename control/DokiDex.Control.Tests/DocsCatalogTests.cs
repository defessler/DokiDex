using System.IO;
using System.Linq;
using DokiDex.Web;
using Xunit;

namespace DokiDex.Control.Tests;

// Pure unit tests for DocsCatalog: the whitelist gate, the id<->file mapping table, title extraction, and the
// wiki filename ordering. No client input ever reaches these functions in production -- these tests assert that
// invariant directly (an id/relPath that isn't on the whitelist simply never makes it into the map).
public class DocsCatalogTests
{
    [Theory]
    [InlineData("README.md", "wiki-readme")]
    [InlineData("Home.md", "wiki-home")]
    [InlineData("1-the-big-idea.md", "wiki-1-the-big-idea")]
    [InlineData("12-benchmarks.md", "wiki-12-benchmarks")]
    [InlineData("Weird__Name!!.md", "wiki-weird-name")]
    public void WikiSlug_produces_stable_lowercase_slugs(string fileName, string expected)
        => Assert.Equal(expected, DocsCatalog.WikiSlug(fileName));

    [Fact]
    public void WikiOrderKey_sorts_home_first_then_numeric_narrative_order()
    {
        var files = new[] { "12-benchmarks.md", "2-the-moving-parts.md", "Home.md", "1-the-big-idea.md", "10-how-it-works.md" };
        var ordered = files.OrderBy(DocsCatalog.WikiOrderKey).ThenBy(f => f).ToArray();
        Assert.Equal(new[] { "Home.md", "1-the-big-idea.md", "2-the-moving-parts.md", "10-how-it-works.md", "12-benchmarks.md" }, ordered);
    }

    [Theory]
    [InlineData("README.md", true)]
    [InlineData("docs/quickstart.md", true)]
    [InlineData("docs/CAPABILITIES.md", true)]
    [InlineData("docs/wiki/Home.md", true)]
    [InlineData("docs/wiki/1-the-big-idea.md", true)]
    public void IsAllowedRelPath_accepts_the_whitelisted_shapes(string relPath, bool expected)
        => Assert.Equal(expected, DocsCatalog.IsAllowedRelPath(relPath));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("../secrets.md")]
    [InlineData("docs/../../secrets.md")]
    [InlineData("docs/wiki/../../../Windows/System32/config.md")]
    [InlineData("README.txt")]                 // wrong extension
    [InlineData("serving/memory-mcp/doc_index.py")]   // wrong dir entirely, wrong extension
    [InlineData("control/appsettings.json")]   // wrong dir, wrong extension
    public void IsAllowedRelPath_rejects_traversal_bad_extensions_and_other_dirs(string? relPath)
        => Assert.False(DocsCatalog.IsAllowedRelPath(relPath));

    [Fact]
    public void BuildMap_drops_any_tuple_whose_relpath_fails_the_whitelist()
    {
        var discovered = new (string Id, string RelPath, string? TitleOverride)[]
        {
            ("readme", "README.md", "Overview"),
            ("evil", "../secrets.md", null),
            ("evil2", "docs/wiki/../../../secrets.md", null),
            ("wiki-home", "docs/wiki/Home.md", null),
        };
        var map = DocsCatalog.BuildMap(discovered);

        Assert.True(map.ContainsKey("readme"));
        Assert.True(map.ContainsKey("wiki-home"));
        Assert.False(map.ContainsKey("evil"));
        Assert.False(map.ContainsKey("evil2"));
        // The literal case the leaf spec calls out: an id shaped like a traversal attempt is simply never a key.
        Assert.False(map.ContainsKey("../secrets"));
        Assert.Equal(2, map.Count);
    }

    [Fact]
    public void BuildMap_normalizes_backslashes_and_trims()
    {
        var map = DocsCatalog.BuildMap(new (string, string, string?)[] { ("readme", "  README.md  ", null) });
        Assert.Equal("README.md", map["readme"].RelPath);

        var map2 = DocsCatalog.BuildMap(new (string, string, string?)[] { ("wiki-home", @"docs\wiki\Home.md", null) });
        Assert.Equal("docs/wiki/Home.md", map2["wiki-home"].RelPath);
    }

    [Fact]
    public void ResolveSafe_resolves_a_whitelisted_relpath_under_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "dokidex-docs-root-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "docs", "wiki"));
        try
        {
            var full = DocsCatalog.ResolveSafe(root, "docs/wiki/Home.md");
            Assert.NotNull(full);
            Assert.StartsWith(Path.GetFullPath(root), full!, System.StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ResolveSafe_returns_null_for_a_disallowed_relpath()
    {
        var root = Path.GetTempPath();
        Assert.Null(DocsCatalog.ResolveSafe(root, "../outside.md"));
        Assert.Null(DocsCatalog.ResolveSafe(root, "notes.txt"));
    }

    [Theory]
    [InlineData("docs/wiki/Home.md", "wiki")]
    [InlineData("docs/wiki/1-the-big-idea.md", "wiki")]
    [InlineData("README.md", "guides")]
    [InlineData("docs/quickstart.md", "guides")]
    public void GroupFor_classifies_wiki_vs_guides(string relPath, string expected)
        => Assert.Equal(expected, DocsCatalog.GroupFor(relPath));

    [Fact]
    public void FallbackTitle_is_the_filename_without_extension()
    {
        Assert.Equal("quickstart", DocsCatalog.FallbackTitle("docs/quickstart.md"));
        Assert.Equal("1-the-big-idea", DocsCatalog.FallbackTitle("docs/wiki/1-the-big-idea.md"));
    }

    [Fact]
    public void ExtractTitle_returns_the_first_h1_line_trimmed()
    {
        Assert.Equal("DokiDex", DocsCatalog.ExtractTitle("# DokiDex\n\nSome body text.", "fallback"));
        Assert.Equal("Quick Start", DocsCatalog.ExtractTitle("\n\n  # Quick Start  \nbody", "fallback"));
    }

    [Fact]
    public void ExtractTitle_ignores_deeper_headings_and_keeps_scanning_for_an_h1()
    {
        Assert.Equal("Real Title", DocsCatalog.ExtractTitle("## Not this\nsome text\n# Real Title\nbody", "fallback"));
    }

    [Fact]
    public void ExtractTitle_falls_back_when_no_h1_is_present()
    {
        Assert.Equal("fallback", DocsCatalog.ExtractTitle("## Only an h2\nbody text, no h1 anywhere", "fallback"));
        Assert.Equal("fallback", DocsCatalog.ExtractTitle("", "fallback"));
        Assert.Equal("fallback", DocsCatalog.ExtractTitle(null, "fallback"));
    }

    [Fact]
    public void ResolveTitle_prefers_the_override_over_the_extracted_title()
    {
        var mapping = new DocMapping("README.md", "Overview");
        Assert.Equal("Overview", DocsCatalog.ResolveTitle(mapping, "# DokiDex\nbody"));
    }

    [Fact]
    public void ResolveTitle_extracts_when_no_override_is_set()
    {
        var mapping = new DocMapping("docs/quickstart.md", null);
        Assert.Equal("Quick Start", DocsCatalog.ResolveTitle(mapping, "# Quick Start\nbody"));
    }

    [Fact]
    public void ReadCapped_truncates_at_the_requested_char_count()
    {
        var path = Path.Combine(Path.GetTempPath(), "dokidex-readcapped-" + System.Guid.NewGuid().ToString("N") + ".md");
        File.WriteAllText(path, new string('x', 10_000));
        try
        {
            var text = DocsCatalog.ReadCapped(path, 100);
            Assert.Equal(100, text.Length);
        }
        finally { File.Delete(path); }
    }

    // ---- DiscoverAll / ToListEntry against a SYNTHETIC docs tree (not RepoPaths.Root -- an installed/adopted
    // home can legitimately point at a different on-disk tree than this checkout, so these stay hermetic). ----
    [Fact]
    public void DiscoverAll_scans_docs_wiki_and_merges_with_the_four_core_docs()
    {
        var root = Path.Combine(Path.GetTempPath(), "dokidex-docscatalog-" + System.Guid.NewGuid().ToString("N"));
        var wikiDir = Path.Combine(root, "docs", "wiki");
        Directory.CreateDirectory(wikiDir);
        try
        {
            File.WriteAllText(Path.Combine(root, "README.md"), "# DokiDex\nbody");
            File.WriteAllText(Path.Combine(root, "docs", "quickstart.md"), "# Quick Start\nbody");
            File.WriteAllText(Path.Combine(root, "docs", "tutorial.md"), "# Tutorial\nbody");
            File.WriteAllText(Path.Combine(root, "docs", "CAPABILITIES.md"), "# Capabilities\nbody");
            File.WriteAllText(Path.Combine(wikiDir, "Home.md"), "# Wiki Home\nbody");
            File.WriteAllText(Path.Combine(wikiDir, "1-the-big-idea.md"), "# The Big Idea\nbody");
            File.WriteAllText(Path.Combine(wikiDir, "2-the-moving-parts.md"), "# Moving Parts\nbody");

            var map = DocsCatalog.DiscoverAll(root);

            Assert.True(map.ContainsKey("readme"));
            Assert.Equal("Overview", map["readme"].TitleOverride);
            Assert.True(map.ContainsKey("quickstart"));
            Assert.True(map.ContainsKey("tutorial"));
            Assert.True(map.ContainsKey("capabilities"));
            Assert.True(map.ContainsKey("wiki-home"));
            Assert.True(map.ContainsKey("wiki-1-the-big-idea"));
            Assert.True(map.ContainsKey("wiki-2-the-moving-parts"));
            Assert.Equal(7, map.Count);
            Assert.All(map.Values, m => Assert.True(DocsCatalog.IsAllowedRelPath(m.RelPath)));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ToListEntry_reads_the_title_from_disk_and_falls_back_gracefully_when_missing()
    {
        var root = Path.Combine(Path.GetTempPath(), "dokidex-docscatalog-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "docs"));
        try
        {
            File.WriteAllText(Path.Combine(root, "docs", "quickstart.md"), "# Quick Start\nbody");
            var mapping = new DocMapping("docs/quickstart.md", null);
            var entry = DocsCatalog.ToListEntry(root, "quickstart", mapping);
            Assert.Equal("quickstart", entry.Id);
            Assert.Equal("Quick Start", entry.Title);
            Assert.Equal("guides", entry.Group);

            // missing file on disk -> degrades to the filename fallback, never throws
            var missing = new DocMapping("docs/does-not-exist.md", null);
            var entry2 = DocsCatalog.ToListEntry(root, "missing", missing);
            Assert.Equal("does-not-exist", entry2.Title);
        }
        finally { Directory.Delete(root, true); }
    }
}
