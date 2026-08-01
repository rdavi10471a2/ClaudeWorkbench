using CalculatorSample;

using Xunit;

namespace CalculatorSample.Tests;

public sealed class CalculatorTests
{
    private readonly Calculator calculator = new();

    [Theory]
    [InlineData(2, 3, 5)]
    [InlineData(-1, 1, 0)]
    [InlineData(-4, -6, -10)]
    [InlineData(0, 0, 0)]
    public void Add_ReturnsSum(double a, double b, double expected)
    {
        Assert.Equal(expected, calculator.Add(a, b));
    }

    [Theory]
    [InlineData(10, 4, 6)]
    [InlineData(0, 5, -5)]
    [InlineData(-3, -3, 0)]
    public void Subtract_ReturnsDifference(double a, double b, double expected)
    {
        Assert.Equal(expected, calculator.Subtract(a, b));
    }

    [Theory]
    [InlineData(6, 7, 42)]
    [InlineData(-2, 3, -6)]
    [InlineData(5, 0, 0)]
    public void Multiply_ReturnsProduct(double a, double b, double expected)
    {
        Assert.Equal(expected, calculator.Multiply(a, b));
    }

    [Theory]
    [InlineData(20, 5, 4)]
    [InlineData(-9, 3, -3)]
    [InlineData(1, 4, 0.25)]
    public void Divide_ReturnsQuotient(double a, double b, double expected)
    {
        Assert.Equal(expected, calculator.Divide(a, b));
    }

    [Fact]
    public void Divide_ByZero_Throws()
    {
        Assert.Throws<DivideByZeroException>(() => calculator.Divide(1, 0));
    }
}
