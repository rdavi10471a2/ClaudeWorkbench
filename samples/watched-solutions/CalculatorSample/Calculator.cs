namespace CalculatorSample;

// Basic four-function calculator. The flow-smoke fixture drives the agent to add,
// remove, and move methods across this file and AdvancedCalculations.cs.
public sealed class Calculator
{
    public double Add(double a, double b) => a + b;

    public double Subtract(double a, double b) => a - b;

    public double Multiply(double a, double b) => a * b;

    public double Divide(double a, double b)
    {
        if (b == 0)
        {
            throw new DivideByZeroException("Cannot divide by zero.");
        }

        return a / b;
    }
}
