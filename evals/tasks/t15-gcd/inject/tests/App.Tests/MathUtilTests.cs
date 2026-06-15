using App;
using Xunit;

// Visible TDD spec for App.MathUtil.Gcd — the build fails until it's implemented.
public class MathUtilTests
{
    [Theory]
    [InlineData(12, 8, 4)]
    [InlineData(0, 5, 5)]
    [InlineData(7, 3, 1)]
    [InlineData(100, 10, 10)]
    public void Gcd_of(int a, int b, int expected)
        => Assert.Equal(expected, MathUtil.Gcd(a, b));
}
