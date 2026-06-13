using App;

namespace App.Tests;

public class KelvinHiddenTests
{
    [Theory]
    [InlineData(0, 273.15)]
    [InlineData(100, 373.15)]
    [InlineData(-273.15, 0)]
    public void CelsiusToKelvin_Converts(double c, double k) =>
        Assert.Equal(k, TemperatureConverter.CelsiusToKelvin(c), 5);

    [Theory]
    [InlineData(273.15, 0)]
    [InlineData(373.15, 100)]
    public void KelvinToCelsius_Converts(double k, double c) =>
        Assert.Equal(c, TemperatureConverter.KelvinToCelsius(k), 5);
}
