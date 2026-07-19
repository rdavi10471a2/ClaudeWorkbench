namespace CalculatorSample;

// Higher-level operations built on top of Calculator. The cross-file dependency
// (Power leans on Calculator.Multiply) makes this pair a good target for the
// "remove from one file, break the caller in the other" build-fail case.
public sealed class AdvancedCalculations
{
    private readonly Calculator calculator = new();

    public double Power(double value, int exponent)
    {
        double result = 1;
        for (int i = 0; i < exponent; i++)
        {
            result = calculator.Multiply(result, value);
        }

        return result;
    }

    public double SquareRoot(double value)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Cannot take the square root of a negative number.");
        }

        return Math.Sqrt(value);
    }
}
