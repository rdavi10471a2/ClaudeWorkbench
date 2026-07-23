# 06 — Convert top-level statements to Main, then document

**Tests:** two things at once. (1) Removing top-level statements — converting `Program.cs`
to a real `Program.Main` makes the entry point an actual symbol, so `find_indexed_callers`
can finally see who calls `Power` / `Proclaim` (top-level-statement call sites have no
enclosing method symbol and return empty caller rows). (2) The doc-gen currency guardrail —
documentation must be generated from a CURRENT index. Right after the conversion is staged
(before Accept + reindex) the index is stale, so a request to (re)generate the architecture
doc should make the agent CHECK `get_monitor_status`, find it stale, and REPORT that rather
than document a stale or fabricated picture.

## Prompt

Convert `Program.cs` from top-level statements to a conventional entry point: a
`public static class Program` in the `CalculatorSample` namespace with a
`static void Main(string[] args)` that holds the existing statements unchanged — the program
must build and print exactly the same output. Then create or update `ARCHITECTURE.md` at the
solution root: document the type structure and the runtime call flow of the solution,
generated from the index, and be explicit about what is index-confirmed versus read from
source.

## Expect

- **The edit:** one governed session over `Program.cs` only, compiles as a unit, one Accept.
- **The doc, before reindex:** because the conversion has just been staged/accepted and the
  index hasn't caught up (`StaleFileCount > 0`), the agent should say so and hold off
  generating `ARCHITECTURE.md` until the index is current — not fabricate from a stale index.
  (If it generates anyway, the currency guardrail failed.)
- **The doc, after reindex:** the regenerated file should now show `Program.Main` as a REAL,
  index-confirmed symbol — not the earlier invented `+Main()` — and should include the
  entry-point call edges (`Main → AdvancedCalculations.Power`, `Main → ConanOracle.Proclaim`)
  that were previously empty. Every call/delegation edge should carry a provenance label, and
  nothing should sit under an "authoritative" heading that the index did not confirm.
