using CalculatorSample;

using Xunit;

namespace CalculatorSample.Tests;

public sealed class AdvancedCalculationsTests
{
    private readonly AdvancedCalculations advanced = new();

    [Theory]
    [InlineData(2, 8, 256)]
    [InlineData(5, 0, 1)]
    [InlineData(3, 2, 9)]
    [InlineData(10, 1, 10)]
    public void Power_RaisesValueToExponent(double value, int exponent, double expected)
    {
        Assert.Equal(expected, advanced.Power(value, exponent), precision: 10);
    }

    [Theory]
    [InlineData(144, 12)]
    [InlineData(0, 0)]
    [InlineData(2, 1.4142135623730951)]
    public void SquareRoot_ReturnsRoot(double value, double expected)
    {
        Assert.Equal(expected, advanced.SquareRoot(value), precision: 10);
    }

    [Fact]
    public void SquareRoot_Negative_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => advanced.SquareRoot(-1));
    }
}
