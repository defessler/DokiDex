namespace App;

public static class RomanNumeral
{
    private static readonly (int Value, string Symbol)[] Map =
    {
        (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"),
        (100, "C"), (90, "XC"), (50, "L"), (40, "XL"),
        (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I")
    };

    public static string ToRoman(int number)
    {
        if (number is < 1 or > 3999)
            throw new ArgumentOutOfRangeException(nameof(number), "must be 1..3999");
        var result = new System.Text.StringBuilder();
        foreach (var (value, symbol) in Map)
        {
            while (number >= value)
            {
                result.Append(symbol);
                number -= value;
            }
        }
        return result.ToString();
    }
}
