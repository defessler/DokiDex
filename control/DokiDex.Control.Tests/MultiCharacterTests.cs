using System.Collections.Generic;
using DokiDex.Web;
using Xunit;

namespace DokiDex.Control.Tests;

// The pure core of the Multi-Character Directorial Composer: compiling a base scene + isolated per-character
// prompts (each pinned to a 5x5 grid cell) into one SwarmUI regional prompt. Locks the placement math,
// per-character isolation, count-tag handling, and the cap — all GPU-free.
public class MultiCharacterTests
{
    private static MultiCharSpec Spec(string @base, params CharacterSpec[] chars)
        => new(@base, new List<CharacterSpec>(chars), null);

    [Fact]
    public void Base_is_kept_verbatim_and_each_character_becomes_an_object_region()
    {
        var p = MultiCharacter.Compile(Spec("2girls, park, sunny",
            new CharacterSpec("red dress", 10),    // row 2, col 0 (left middle)
            new CharacterSpec("blue dress", 14)));  // row 2, col 4 (right middle)
        Assert.StartsWith("2girls, park, sunny ", p);
        Assert.Contains("<object:red dress,", p);
        Assert.Contains("<object:blue dress,", p);
        Assert.Equal(2, p.Split("<object:").Length - 1);   // exactly two regions
    }

    [Fact]
    public void Cell_maps_to_a_fractional_box_centered_on_the_cell()
    {
        // center cell (12) on a 5x5 grid: col2,row2 -> center (0.5,0.5); portrait box 0.2 x 0.4
        var (x, y, w, h) = MultiCharacter.CellBox(12);
        Assert.Equal(0.4, x, 3);   // 0.5 - 0.2/2
        Assert.Equal(0.3, y, 3);   // 0.5 - 0.4/2
        Assert.Equal(0.2, w, 3);
        Assert.Equal(0.4, h, 3);
    }

    [Fact]
    public void Corner_cells_clamp_the_box_inside_0_1()
    {
        var tl = MultiCharacter.CellBox(0);    // top-left
        Assert.True(tl.x >= 0 && tl.y >= 0);
        var br = MultiCharacter.CellBox(24);   // bottom-right
        Assert.True(br.x + br.w <= 1.0 + 1e-9);
        Assert.True(br.y + br.h <= 1.0 + 1e-9);
    }

    [Fact]
    public void Leading_count_tag_on_a_character_is_stripped_into_attributes_only()
    {
        // "1girl, red hair" -> the region carries only "red hair"; the count tag belongs in the base
        var p = MultiCharacter.Compile(Spec("2girls", new CharacterSpec("1girl, red hair", 6)));
        Assert.Contains("<object:red hair,", p);
        Assert.DoesNotContain("1girl, red hair", p);
    }

    [Fact]
    public void Unplaced_character_contributes_its_prompt_without_a_region()
    {
        var p = MultiCharacter.Compile(Spec("forest", new CharacterSpec("a glowing fox", -1)));
        Assert.Contains("a glowing fox", p);
        Assert.DoesNotContain("<object:", p);
    }

    [Fact]
    public void Empty_character_prompts_are_skipped()
    {
        var p = MultiCharacter.Compile(Spec("scene",
            new CharacterSpec("   ", 0),
            new CharacterSpec("knight", 4)));
        Assert.Equal(1, p.Split("<object:").Length - 1);
        Assert.Contains("knight", p);
    }

    [Fact]
    public void Character_count_is_capped_at_six()
    {
        var chars = new List<CharacterSpec>();
        for (int i = 0; i < 9; i++) chars.Add(new CharacterSpec($"char{i}", i));
        var p = MultiCharacter.Compile(new MultiCharSpec("crowd", chars, null));
        Assert.Equal(MultiCharacter.MaxCharacters, p.Split("<object:").Length - 1);   // only 6 regions
    }

    [Fact]
    public void Relationship_phrase_is_appended_to_the_scene()
    {
        var p = MultiCharacter.Compile(new MultiCharSpec("two knights",
            new List<CharacterSpec> { new("red armor", 10), new("blue armor", 14) },
            "red knight crosses swords with blue knight"));
        Assert.Contains("red knight crosses swords with blue knight", p);
    }

    [Fact]
    public void Coordinates_are_invariant_formatted_with_a_dot()
    {
        // never emit a locale comma as the decimal separator inside the tag (would corrupt the CSV coords)
        var p = MultiCharacter.Compile(Spec("x", new CharacterSpec("y", 12)));
        Assert.Contains("<object:y,0.4,0.3,0.2,0.4>", p);
    }
}
