using App;

namespace App.Tests;

public class RomanFromHiddenTests
{
    [Theory]
    [InlineData(40)]
    [InlineData(90)]
    [InlineData(400)]
    [InlineData(900)]
    [InlineData(1066)]
    [InlineData(2026)]
    public void RoundTrip(int n) =>
        Assert.Equal(n, RomanNumeral.FromRoman(RomanNumeral.ToRoman(n)));
}
