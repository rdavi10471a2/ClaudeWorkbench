# 04 — Build fails at plan-complete, agent fixes and retries

**Tests:** the headline of the new workflow — `complete_edit_plan` runs the REAL build, so a
`.razor` that references an undefined member is caught with the ACTUAL compiler error (CS0103
in the generated component) BEFORE staging, and the agent is told to fix and re-run. The old
flat overlay could not see `.razor`-generated code, so this class of error slipped through to
the operator's Accept; now it does not.

## Prompt

In `Components/CustomerList.razor`, add a line to the markup that shows a `@Subtitle` value
under the heading, but do NOT add the `Subtitle` member yet. Call `complete_edit_plan`.

## Expect

- **First `complete_edit_plan`:** the build FAILS with the real compiler error (the generated
  component references `Subtitle`, which does not exist — CS0103) naming the file, and tells
  you to fix and call `complete_edit_plan` again.
- **The fix:** add `public string Subtitle { get; set; } = "…";` to the component's `@code`,
  re-submit, and call `complete_edit_plan` again. It now builds clean. Only then do you stage.
