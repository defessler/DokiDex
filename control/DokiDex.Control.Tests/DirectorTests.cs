using DokiDex.Web;
using Xunit;

namespace DokiDex.Control.Tests;

// The pure heart of the script-to-shotlist director: turning a messy LLM reply into clean, ordered shots.
// The live LLM call needs agent mode, but this parser is total + side-effect-free, so its robustness is
// locked here with no GPU.
public class DirectorTests
{
    [Fact]
    public void Bare_json_array_of_title_prompt_parses_in_order()
    {
        var shots = Director.ParseShotlist("""
            [{"title":"Wide","prompt":"a lone figure on a dune at dawn"},
             {"title":"Close","prompt":"cracked lips, squinting eyes"}]
            """);
        Assert.Equal(2, shots.Count);
        Assert.Equal(1, shots[0].Index);
        Assert.Equal("Wide", shots[0].Title);
        Assert.Equal("a lone figure on a dune at dawn", shots[0].Prompt);
        Assert.Equal(2, shots[1].Index);   // indices are 1-based and sequential
    }

    [Fact]
    public void Markdown_fenced_json_is_unwrapped()
    {
        var shots = Director.ParseShotlist("```json\n[{\"title\":\"A\",\"prompt\":\"p one\"}]\n```");
        Assert.Single(shots);
        Assert.Equal("p one", shots[0].Prompt);
    }

    [Fact]
    public void Object_wrapper_with_a_shots_array_is_unwrapped()
    {
        var shots = Director.ParseShotlist("""{"shots":[{"title":"A","prompt":"x"},{"title":"B","prompt":"y"}]}""");
        Assert.Equal(2, shots.Count);
        Assert.Equal("y", shots[1].Prompt);
    }

    [Fact]
    public void Leading_prose_before_the_json_is_ignored()
    {
        var shots = Director.ParseShotlist("Sure! Here is your storyboard:\n[{\"prompt\":\"a neon alley in the rain\"}]\nEnjoy.");
        Assert.Single(shots);
        Assert.Equal("a neon alley in the rain", shots[0].Prompt);
        Assert.Equal("", shots[0].Title);   // missing title -> empty, not null
    }

    [Fact]
    public void Bare_string_items_are_treated_as_prompts()
    {
        var shots = Director.ParseShotlist("""["first shot prompt", "second shot prompt"]""");
        Assert.Equal(2, shots.Count);
        Assert.Equal("first shot prompt", shots[0].Prompt);
    }

    [Fact]
    public void Alternate_key_names_are_accepted()
    {
        var shots = Director.ParseShotlist("""[{"name":"Establishing","description":"city skyline at golden hour"}]""");
        Assert.Single(shots);
        Assert.Equal("Establishing", shots[0].Title);
        Assert.Equal("city skyline at golden hour", shots[0].Prompt);
    }

    [Fact]
    public void Brackets_inside_string_values_do_not_truncate_extraction()
    {
        // a ']' inside a prompt string must not end the array early
        var shots = Director.ParseShotlist("""[{"prompt":"a sign reading [OPEN] at night"},{"prompt":"second"}]""");
        Assert.Equal(2, shots.Count);
        Assert.Equal("a sign reading [OPEN] at night", shots[0].Prompt);
    }

    [Fact]
    public void Empty_prompts_are_skipped_and_indices_stay_sequential()
    {
        var shots = Director.ParseShotlist("""[{"title":"A","prompt":""},{"title":"B","prompt":"real"}]""");
        Assert.Single(shots);
        Assert.Equal(1, shots[0].Index);   // the kept shot is #1, not #2
        Assert.Equal("real", shots[0].Prompt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("I'm sorry, I can't help with that.")]
    [InlineData("[not json at all")]
    [InlineData("{}")]
    public void Unparseable_replies_yield_no_shots(string text)
        => Assert.Empty(Director.ParseShotlist(text));
}
