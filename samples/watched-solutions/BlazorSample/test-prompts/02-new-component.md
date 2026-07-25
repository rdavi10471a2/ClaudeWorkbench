# 02 — New Razor component (new file, exercises the Razor generator)

**Tests:** the `new_file` → `submit_file` path for a brand-new `.razor` component, and that
the plan-complete build runs the REAL Razor source generator — a `.razor` compiles to C# via
the generator, which the old in-memory overlay could not do. A member referenced in markup
must resolve through the generated code.

## Prompt

Add a new Razor component `Components/CustomerCard.razor` in the `BlazorSample.Components`
namespace. It takes a `[Parameter] public Customer Customer { get; set; }` (from
`BlazorSample.Model`) and renders the customer's `Name` in an `<h4>` and `Id` in a `<span>`.
Add a `[Parameter] public bool Highlight { get; set; }` that, when true, wraps the card in a
`<div class="highlight">`. Don't change any existing file.
