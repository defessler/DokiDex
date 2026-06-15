using System;
using App;
using Xunit;

// Hidden tests (never seen by the agent): the visible cases are all VALID inputs, so they can't catch a
// missing argument check — these enforce that a negative n and an out-of-range base both throw, plus a few
// boundary conversions (the 10->'a' hex roll-over, exact powers).
public class BaseConverterHiddenTests
{
    [Theory]
    [InlineData(15, 16, "f")]
    [InlineData(16, 16, "10")]
    [InlineData(7, 2, "111")]
    [InlineData(100, 16, "64")]
    [InlineData(1, 2, "1")]
    public void ToBase_handles_boundaries(int n, int b, string expected)
        => Assert.Equal(expected, BaseConverter.ToBase(n, b));

    [Fact]
    public void ToBase_throws_on_negative()
        => Assert.Throws<ArgumentOutOfRangeException>(() => BaseConverter.ToBase(-1, 10));

    [Theory]
    [InlineData(0)]    // base 0/1 would make a naive loop infinite — 0 gives a clean throw instead of a hang
    [InlineData(17)]
    public void ToBase_throws_on_base_out_of_range(int b)
        => Assert.Throws<ArgumentOutOfRangeException>(() => BaseConverter.ToBase(10, b));
}
