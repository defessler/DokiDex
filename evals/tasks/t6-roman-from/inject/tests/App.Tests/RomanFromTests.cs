using App;

namespace App.Tests;

public class RomanFromTests
{
    [Theory]
    [InlineData("I", 1)]
    [InlineData("IV", 4)]
    [InlineData("IX", 9)]
    [InlineData("XIV", 14)]
    [InlineData("MCMXCIV", 1994)]
    [InlineData("MMMCMXCIX", 3999)]
    public void FromRoman_Parses(string roman, int expected) =>
        Assert.Equal(expected, RomanNumeral.FromRoman(roman));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Q")]
    [InlineData("MQX")]
    public void FromRoman_Invalid_Throws(string? input) =>
        Assert.Throws<ArgumentException>(() => RomanNumeral.FromRoman(input!));
}
