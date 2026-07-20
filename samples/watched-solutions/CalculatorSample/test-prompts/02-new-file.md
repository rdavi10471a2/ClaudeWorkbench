# 02 — New file (single new file into the namespace)

**Tests:** the `new_file` → `submit_file` path — a brand-new watched file declared in the
edit session, compiled in the overlay, and written on Accept.

## Prompt

Add a new file `Statistics.cs` in the `CalculatorSample` namespace with a
`public sealed class Statistics` that exposes:

- `double Mean(IReadOnlyList<double> values)` — the arithmetic mean.
- `double Variance(IReadOnlyList<double> values)` — the population variance.

Both should throw `ArgumentException` when the list is null or empty. Don't change any
existing file.
