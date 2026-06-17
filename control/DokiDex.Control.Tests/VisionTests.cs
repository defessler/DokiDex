using DokiDex.Web;
using Xunit;

namespace DokiDex.Control.Tests;

// The pure prompt-adherence verdict parser (the only non-IO half of the gated VLM verify path). No GPU, no LLM.
public class VisionTests
{
    [Fact]
    public void Pass_line_parses_as_a_pass_with_its_reason()
    {
        var v = Vision.ParseVerdict("PASS: the red fox and snow are both present");
        Assert.True(v.Pass);
        Assert.Equal("the red fox and snow are both present", v.Reason);
    }

    [Fact]
    public void Fail_line_parses_as_a_fail_with_its_reason()
    {
        var v = Vision.ParseVerdict("FAIL: the scarf is blue, not red");
        Assert.False(v.Pass);
        Assert.Equal("the scarf is blue, not red", v.Reason);
    }

    [Fact]
    public void Fail_wins_when_both_words_appear()
    {
        // models often say "this would PASS except ... so it FAILs" — be conservative
        var v = Vision.ParseVerdict("It mostly passes but the text is wrong, so FAIL.");
        Assert.False(v.Pass);
    }

    [Fact]
    public void Ambiguous_reply_is_not_a_pass()
    {
        var v = Vision.ParseVerdict("The image shows a fox in a forest.");
        Assert.False(v.Pass);
        Assert.Equal("The image shows a fox in a forest.", v.Reason);
    }

    [Fact]
    public void Empty_reply_is_a_no_response_fail()
    {
        var v = Vision.ParseVerdict("   ");
        Assert.False(v.Pass);
        Assert.Equal("no response", v.Reason);
    }

    [Fact]
    public void Only_the_first_line_of_the_reason_is_kept()
    {
        var v = Vision.ParseVerdict("PASS: looks right\nextra commentary on a second line");
        Assert.True(v.Pass);
        Assert.Equal("looks right", v.Reason);
    }

    [Fact]
    public void Case_insensitive_verdict_word()
    {
        Assert.True(Vision.ParseVerdict("pass - everything matches").Pass);
        Assert.False(Vision.ParseVerdict("fail - missing subject").Pass);
    }
}
