using System;
using App;
using Xunit;

// Hidden tests (the agent never sees these): enforce the "omit zero MIDDLE units" rule + the negative
// guard, catching an implementation that only fit the visible cases (all of which have non-zero units).
public class DurationHiddenTests
{
    [Theory]
    [InlineData(3601, "1h 1s")]      // zero minutes omitted, NOT "1h 0m 1s"
    [InlineData(86461, "1d 1m 1s")]  // zero hours omitted
    [InlineData(90000, "1d 1h")]     // trailing zero minutes/seconds omitted
    [InlineData(59, "59s")]
    [InlineData(3599, "59m 59s")]
    [InlineData(172800, "2d")]
    public void Humanize_omits_zero_units_and_handles_boundaries(int seconds, string expected)
        => Assert.Equal(expected, DurationFormatter.Humanize(seconds));

    [Fact]
    public void Humanize_throws_on_negative()
        => Assert.Throws<ArgumentOutOfRangeException>(() => DurationFormatter.Humanize(-1));
}
