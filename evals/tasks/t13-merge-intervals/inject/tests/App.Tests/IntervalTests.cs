using App;
using Xunit;

// Visible TDD spec for App.IntervalMerger.Merge — the build fails until it's implemented.
public class IntervalTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("1-3,2-6,8-10,15-18", "1-6,8-10,15-18")]
    [InlineData("1-4,4-5", "1-5")]              // touching ranges merge
    [InlineData("5-7,1-3", "1-3,5-7")]          // input order is arbitrary
    public void Merge_combines_overlapping_and_touching(string input, string expected)
        => Assert.Equal(expected, IntervalMerger.Merge(input));
}
