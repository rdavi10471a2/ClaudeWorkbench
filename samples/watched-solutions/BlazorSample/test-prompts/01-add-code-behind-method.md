# 01 — Add a method (single file, surgical)

**Tests:** a one-file change to a Razor component's code-behind (`.razor.cs`) via a typed
symbol edit, a single-file session, and one Accept that writes immediately. Confirms the
per-edit path is syntax-only and the plan-complete build compiles the component cleanly.

## Prompt

Add a `Reset()` method to the `CustomerList` component's code-behind
(`Components/CustomerList.razor.cs`) that sets `LoadedCount` back to zero. Match the existing
expression-bodied style of `RecordLoad`. Don't change the `.razor` markup.
