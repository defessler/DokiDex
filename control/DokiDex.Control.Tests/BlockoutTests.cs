using System.Collections.Generic;
using System.Linq;
using DokiDex.Web;
using Xunit;

namespace DokiDex.Control.Tests;

// The pure software depth rasterizer for 3D-blockout: perspective projection + z-buffer occlusion -> a depth
// map. No GPU, no display, no three.js — so it's fully unit-testable here.
public class BlockoutTests
{
    private static byte At(byte[] d, int w, int x, int y) => d[y * w + x];

    [Fact]
    public void A_centered_box_fills_the_middle_and_leaves_corners_empty()
    {
        var d = Blockout.RenderDepth(new BlockoutScene(64, 64, 5, 1.0, new() { new Prim(0, 0, 0, 1) }));
        Assert.True(At(d, 64, 32, 32) > 0);   // center is filled
        Assert.Equal(0, At(d, 64, 0, 0));     // a corner is empty (far/background)
    }

    [Fact]
    public void A_nearer_box_is_brighter_than_a_farther_one()
    {
        var near = Blockout.RenderDepth(new BlockoutScene(64, 64, 5, 1.0, new() { new Prim(0, 0, 2, 1) }));
        var far = Blockout.RenderDepth(new BlockoutScene(64, 64, 5, 1.0, new() { new Prim(0, 0, -2, 1) }));
        Assert.True(At(near, 64, 32, 32) > At(far, 64, 32, 32));   // nearer => brighter depth
    }

    [Fact]
    public void A_nearer_box_projects_larger_more_filled_pixels()
    {
        int Filled(byte[] d) => d.Count(b => b > 0);
        var near = Blockout.RenderDepth(new BlockoutScene(64, 64, 5, 1.0, new() { new Prim(0, 0, 2, 1) }));
        var far = Blockout.RenderDepth(new BlockoutScene(64, 64, 5, 1.0, new() { new Prim(0, 0, -2, 1) }));
        Assert.True(Filled(near) > Filled(far));   // perspective: nearer is bigger on screen
    }

    [Fact]
    public void Occlusion_the_nearer_box_wins_the_overlap()
    {
        // two boxes at the same screen spot, different depths -> the center shows the NEARER one's depth
        var scene = new BlockoutScene(64, 64, 5, 1.0, new() { new Prim(0, 0, -2, 1), new Prim(0, 0, 2, 1) });
        var both = Blockout.RenderDepth(scene);
        var nearOnly = Blockout.RenderDepth(new BlockoutScene(64, 64, 5, 1.0, new() { new Prim(0, 0, 2, 1) }));
        Assert.Equal(At(nearOnly, 64, 32, 32), At(both, 64, 32, 32));   // nearer box's depth wins the z-buffer
    }

    [Fact]
    public void Empty_scene_is_all_background()
        => Assert.True(Blockout.RenderDepth(new BlockoutScene(32, 32, 5, 1.0, new())).All(b => b == 0));
}
