using Yagu.Helpers;

namespace Yagu.Tests;

public sealed class InlineCalculatorTests
{
    // ── Arithmetic ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("2+2", "4")]
    [InlineData("2 + 2", "4")]
    [InlineData("10-3", "7")]
    [InlineData("6*7", "42")]
    [InlineData("20/4", "5")]
    [InlineData("2^10", "1024")]
    [InlineData("(3+4)*2", "14")]
    [InlineData("2+3*4", "14")]          // precedence: * before +
    [InlineData("(2+3)*4", "20")]
    [InlineData("-5+8", "3")]
    [InlineData("3 × 4", "12")]           // unicode multiply
    [InlineData("12 ÷ 4", "3")]           // unicode divide
    public void Evaluate_Arithmetic_ComputesValue(string input, string expected)
    {
        var result = InlineCalculator.Evaluate(input);

        Assert.NotNull(result);
        Assert.Equal(InlineCalcKind.Arithmetic, result!.Kind);
        Assert.Equal(expected, result.Value);
        Assert.Equal($"{input.Trim()} = {expected}", result.Display);
    }

    [Theory]
    [InlineData("sqrt(9)", "3")]
    [InlineData("sqrt(9)*4", "12")]
    [InlineData("abs(-7)", "7")]
    [InlineData("floor(3.7)", "3")]
    [InlineData("ceil(3.2)", "4")]
    [InlineData("round(2.5)", "3")]
    [InlineData("log(1000)", "3")]
    public void Evaluate_Functions_AreSupported(string input, string expected)
    {
        var result = InlineCalculator.Evaluate(input);

        Assert.NotNull(result);
        Assert.Equal(expected, result!.Value);
    }

    [Theory]
    [InlineData("15% of 340", "51")]
    [InlineData("50% of 10", "5")]
    [InlineData("10% of 250", "25")]
    public void Evaluate_PercentOf_ComputesValue(string input, string expected)
    {
        var result = InlineCalculator.Evaluate(input);

        Assert.NotNull(result);
        Assert.Equal(expected, result!.Value);
    }

    [Fact]
    public void Evaluate_Constants_AreSupported()
    {
        // pi alone has no digit, so it isn't treated as an expression; use it in a computation.
        var result = InlineCalculator.Evaluate("2*pi");
        Assert.NotNull(result);
        Assert.StartsWith("6.283", result!.Value);
    }

    [Theory]
    [InlineData("123")]        // bare number — not a computation
    [InlineData("-5")]         // signed bare number
    [InlineData("3.14")]       // bare decimal
    [InlineData("hello")]      // no digits
    [InlineData("TODO")]       // a normal search term
    [InlineData("5/0")]        // division by zero → not shown
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Evaluate_NonExpressions_ReturnNull(string? input)
    {
        Assert.Null(InlineCalculator.Evaluate(input));
    }

    [Fact]
    public void Evaluate_QueryWithTrailingUnitsThatIsNotConversion_ReturnsNull()
    {
        // "5 km" alone (no "to <unit>") is not a conversion and not clean arithmetic.
        Assert.Null(InlineCalculator.Evaluate("5 km"));
    }

    [Fact]
    public void TryCalc_DeeplyNestedParens_ReturnsNullInsteadOfOverflowing()
    {
        string input = new string('(', 200) + "1" + new string(')', 200);
        Assert.Null(InlineCalculator.TryCalc(input));
    }

    // ── Unit conversion ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("1 km to m", "1000", "km", "m")]
    [InlineData("5 km to miles", "3.106856", "km", "miles")]
    [InlineData("100 cm to m", "1", "cm", "m")]
    [InlineData("1 kg to g", "1000", "kg", "g")]
    [InlineData("2 gb to mb", "2048", "gb", "mb")]
    [InlineData("90 min to hours", "1.5", "min", "hours")]
    [InlineData("1 mile to km", "1.609344", "mile", "km")]
    public void Evaluate_UnitConversion_ComputesValue(string input, string expectedValue, string fromUnit, string toUnit)
    {
        var result = InlineCalculator.Evaluate(input);

        Assert.NotNull(result);
        Assert.Equal(InlineCalcKind.UnitConversion, result!.Kind);
        Assert.Equal(expectedValue, result.Value);
        Assert.Contains(fromUnit, result.Display);
        Assert.Contains(toUnit, result.Display);
    }

    [Theory]
    [InlineData("100 c to f", "212")]
    [InlineData("32 f to c", "0")]
    [InlineData("0 c to k", "273.15")]
    [InlineData("72 f to c", "22.222222")]
    public void Evaluate_TemperatureConversion_ComputesValue(string input, string expected)
    {
        var result = InlineCalculator.Evaluate(input);

        Assert.NotNull(result);
        Assert.Equal(InlineCalcKind.UnitConversion, result!.Kind);
        Assert.Equal(expected, result.Value);
    }

    [Fact]
    public void Evaluate_InSeparatorWorksLikeTo()
    {
        var result = InlineCalculator.Evaluate("5 km in miles");
        Assert.NotNull(result);
        Assert.Equal("3.106856", result!.Value);
    }

    [Theory]
    [InlineData("5 km to kg")]       // incompatible categories
    [InlineData("5 foo to bar")]     // unknown units
    [InlineData("km to miles")]      // no number
    public void Evaluate_InvalidConversions_ReturnNull(string input)
    {
        Assert.Null(InlineCalculator.Evaluate(input));
    }
}
