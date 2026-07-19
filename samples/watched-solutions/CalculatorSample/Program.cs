using CalculatorSample;

Calculator calculator = new();
AdvancedCalculations advanced = new();

Console.WriteLine($"2 + 3 = {calculator.Add(2, 3)}");
Console.WriteLine($"10 - 4 = {calculator.Subtract(10, 4)}");
Console.WriteLine($"6 * 7 = {calculator.Multiply(6, 7)}");
Console.WriteLine($"20 / 5 = {calculator.Divide(20, 5)}");
Console.WriteLine($"2 ^ 8 = {advanced.Power(2, 8)}");
Console.WriteLine($"sqrt(144) = {advanced.SquareRoot(144)}");
