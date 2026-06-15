using App;
using Xunit;

// Hidden tests (never seen by the agent): the visible cases are all NON-NEGATIVE, so they can't catch a
// naive Euclidean loop that returns a negative for negative inputs — these enforce the non-negative result,
// both zero-paths, and a two-negative case.
public class MathUtilHiddenTests
{
    [Theory]
    [InlineData(-12, 8, 4)]   // result stays non-negative for a negative input
    [InlineData(8, 0, 8)]
    [InlineData(0, 0, 0)]
    [InlineData(-6, -9, 3)]
    [InlineData(17, 5, 1)]
    public void Gcd_handles_signs_and_zero(int a, int b, int expected)
        => Assert.Equal(expected, MathUtil.Gcd(a, b));
}
