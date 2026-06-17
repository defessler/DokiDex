using System.Collections.Generic;
using System.Linq;
using DokiDex.Web;
using Xunit;

namespace DokiDex.Control.Tests;

// The pure variation-lineage forest builder (parent links -> tree). No filesystem, no GPU.
public class LineageTests
{
    private static LinItem It(string name, string? parent = null) => new(name, parent, name + " prompt", "image");

    [Fact]
    public void Items_with_no_parents_are_all_roots()
    {
        var f = Lineage.BuildForest(new[] { It("a.png"), It("b.png") });
        Assert.Equal(2, f.Count);
        Assert.All(f, n => Assert.Empty(n.Children));
    }

    [Fact]
    public void A_child_attaches_under_its_parent()
    {
        var f = Lineage.BuildForest(new[] { It("root.png"), It("child.png", "root.png") });
        Assert.Single(f);
        Assert.Equal("root.png", f[0].Name);
        Assert.Single(f[0].Children);
        Assert.Equal("child.png", f[0].Children[0].Name);
    }

    [Fact]
    public void Grandchildren_nest_to_full_depth()
    {
        var f = Lineage.BuildForest(new[]
        {
            It("a.png"), It("b.png", "a.png"), It("c.png", "b.png"),
        });
        Assert.Single(f);
        Assert.Equal("c.png", f[0].Children.Single().Children.Single().Name);
    }

    [Fact]
    public void A_parent_outside_the_set_falls_back_to_a_root()
    {
        // the source was deleted/trashed: the orphan still appears, as a root
        var f = Lineage.BuildForest(new[] { It("child.png", "gone.png") });
        Assert.Single(f);
        Assert.Equal("child.png", f[0].Name);
    }

    [Fact]
    public void Self_parent_does_not_strand_or_loop()
    {
        var f = Lineage.BuildForest(new[] { It("a.png", "a.png") });
        Assert.Single(f);
        Assert.Equal("a.png", f[0].Name);
        Assert.Empty(f[0].Children);
    }

    [Fact]
    public void A_two_node_cycle_keeps_both_reachable_without_infinite_recursion()
    {
        // a.parent=b and b.parent=a — one attaches, the other falls back to a root; neither is lost.
        var f = Lineage.BuildForest(new[] { It("a.png", "b.png"), It("b.png", "a.png") });
        var names = Flatten(f).Select(n => n.Name).OrderBy(s => s).ToList();
        Assert.Equal(new[] { "a.png", "b.png" }, names);
    }

    [Fact]
    public void Duplicate_names_keep_only_the_first()
    {
        var f = Lineage.BuildForest(new[] { It("a.png"), It("a.png") });
        Assert.Single(f);
    }

    [Fact]
    public void Empty_input_is_an_empty_forest() => Assert.Empty(Lineage.BuildForest(new List<LinItem>()));

    private static IEnumerable<LinNode> Flatten(IEnumerable<LinNode> nodes)
        => nodes.SelectMany(n => new[] { n }.Concat(Flatten(n.Children)));
}
