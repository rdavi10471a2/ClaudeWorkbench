using System;

namespace LegacyFrameworkSample
{
    // Old-format (non-SDK-style) csproj targeting .NET Framework 4.7.2 with packages.config.
    // Fixture for the question "does the workbench index legacy project formats?".
    public class LegacyCalculator
    {
        public double Add(double a, double b)
        {
            return a + b;
        }

        public double Divide(double a, double b)
        {
            if (b == 0)
            {
                throw new DivideByZeroException("Cannot divide by zero.");
            }

            return a / b;
        }
    }
}
