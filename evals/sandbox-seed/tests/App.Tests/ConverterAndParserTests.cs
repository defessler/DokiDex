using App;

namespace App.Tests;

public class TemperatureConverterTests
{
    [Theory]
    [InlineData(0, 32)]
    [InlineData(100, 212)]
    [InlineData(-40, -40)]
    public void CelsiusToFahrenheit_Converts(double c, double f) =>
        Assert.Equal(f, TemperatureConverter.CelsiusToFahrenheit(c), 5);

    [Theory]
    [InlineData(32, 0)]
    [InlineData(212, 100)]
    public void FahrenheitToCelsius_Converts(double f, double c) =>
        Assert.Equal(c, TemperatureConverter.FahrenheitToCelsius(f), 5);
}

public class CsvParserTests
{
    [Fact]
    public void ParseLine_SplitsSimpleFields() =>
        Assert.Equal(new[] { "a", "b", "c" }, CsvParser.ParseLine("a,b,c"));

    [Fact]
    public void ParseLine_EmptyInput_ReturnsEmpty() =>
        Assert.Empty(CsvParser.ParseLine(""));
}

public class InventoryTests
{
    [Fact]
    public void Add_ThenCount_Accumulates()
    {
        var inv = new Inventory();
        inv.Add("apple", 3);
        inv.Add("Apple", 2);
        Assert.Equal(5, inv.CountOf("apple"));
    }

    [Fact]
    public void Remove_TakesItemsOut()
    {
        var inv = new Inventory();
        inv.Add("box", 5);
        inv.Remove("box", 2);
        Assert.Equal(3, inv.CountOf("box"));
        Assert.Equal(3, inv.TotalItems);
    }

    [Fact]
    public void Remove_MoreThanHeld_Throws()
    {
        var inv = new Inventory();
        inv.Add("box", 1);
        Assert.Throws<InvalidOperationException>(() => inv.Remove("box", 2));
    }
}

public class RomanNumeralTests
{
    [Theory]
    [InlineData(1, "I")]
    [InlineData(4, "IV")]
    [InlineData(9, "IX")]
    [InlineData(14, "XIV")]
    [InlineData(1994, "MCMXCIV")]
    [InlineData(3999, "MMMCMXCIX")]
    public void ToRoman_Converts(int n, string expected) =>
        Assert.Equal(expected, RomanNumeral.ToRoman(n));

    [Fact]
    public void ToRoman_OutOfRange_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => RomanNumeral.ToRoman(0));
}
