using App;
using Xunit;

// Visible TDD spec for App.BaseConverter.ToBase — the build fails until it's implemented.
public class BaseConverterTests
{
    [Theory]
    [InlineData(0, 2, "0")]
    [InlineData(10, 2, "1010")]
    [InlineData(255, 16, "ff")]   // lowercase hex digits
    [InlineData(8, 8, "10")]
    public void ToBase_converts_valid_inputs(int n, int b, string expected)
        => Assert.Equal(expected, BaseConverter.ToBase(n, b));
}
