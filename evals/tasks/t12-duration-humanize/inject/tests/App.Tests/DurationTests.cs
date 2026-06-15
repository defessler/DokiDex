using App;
using Xunit;

// Visible TDD spec for App.DurationFormatter.Humanize — the build fails until it's implemented.
public class DurationTests
{
    [Theory]
    [InlineData(0, "0s")]
    [InlineData(5, "5s")]
    [InlineData(60, "1m")]
    [InlineData(61, "1m 1s")]
    [InlineData(3600, "1h")]
    [InlineData(3661, "1h 1m 1s")]
    [InlineData(86400, "1d")]
    [InlineData(90061, "1d 1h 1m 1s")]
    public void Humanize_formats_durations(int seconds, string expected)
        => Assert.Equal(expected, DurationFormatter.Humanize(seconds));
}
