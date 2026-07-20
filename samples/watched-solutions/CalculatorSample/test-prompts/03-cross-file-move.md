# 03 — Cross-file move (declare all files up front)

**Tests:** moving code OUT of one file into a new one and fixing the caller — the
"break the caller in the other file" case. The agent must declare all three files in one
edit session before moving anything, and the whole set must compile as a unit.

## Prompt

Move the `Power` method out of `AdvancedCalculations` into a new file `Exponentiation.cs`
with a `public sealed class Exponentiation` in the `CalculatorSample` namespace. Keep its
behaviour exactly (repeated multiplication via `Calculator.Multiply`). Then update
`AdvancedCalculations` so it no longer defines `Power`, and update `Program.cs` so the
`2 ^ 8` line calls the new `Exponentiation` class. Everything must still compile and print
the same output.
