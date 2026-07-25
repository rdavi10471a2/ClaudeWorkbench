# 03 — Cross-file: model + component + code-behind (declare all files up front)

**Tests:** a change that spans a plain `.cs` model, a `.razor` component's markup, and its
`.razor.cs` code-behind, all declared in ONE edit session. The plan-complete build must
compile the whole set as a unit — the Razor generator turns the markup into C# that
references the new model member across files, which only a real build resolves.

## Prompt

Add an `Email` property (`string`, defaulting to `""`) to the `Customer` model
(`Model/Customer.cs`). Then surface it: in `Components/CustomerList.razor`, show the loaded
customer's email under the filter input; and in the `CustomerList` code-behind
(`Components/CustomerList.razor.cs`), add a `bool HasEmail` computed property. Everything must
still compile as one session.
