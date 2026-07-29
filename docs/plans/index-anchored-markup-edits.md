# Plan — index-anchored markup edits (possible expansion)

**Status:** proposed · not scheduled · **Date:** 2026-07-29 · **Built from:** the ADR-0007 read path (index parses the build's generated `.g.cs`, mapped back to `.razor` via `#line`) and a governed-agent debate on whether find-in-files is the ceiling for markup mutation.

## Framing — this is polish, not a wound

Find-in-files is **not** the problem people assume it is here, because the index already hands the agent the exact `.razor` location to act on. When the agent needs to touch `@SharedGreeter.Greet(...)`, it does not scan the tree blind — the index resolves it to `GreetingCard.razor:3` col 23 first, and the text operation is aimed at a single known line. That is a *targeted* text edit, not a search-and-hope. So this document is an optional upgrade, not a gap that is costing us.

There is a real reason to keep it in view anyway: Roslyn genuinely cannot edit markup (it only sees the generated C#), so the C# symbol mutators (`add_method`, `submit_symbol`, …) stop at the `.cs`/`@code` boundary. For markup itself, some form of span replacement is the write mechanism. The only open question is **where the span comes from** — and the index already produces a better source than grep for the semantic subset.

## The three tiers

| Tier | Mechanism | Covers | Cost / risk |
|---|---|---|---|
| 1 | find-in-files | anything textual | floor; already index-anchored in practice |
| **2** | **index reference → `.razor` char span → `replace_span_in_file`** | **semantic markup: component usages, bound params, event handlers, `@expr`, `@code`** | **low — rides what we already store; this doc** |
| 3 | `RazorSyntaxTree.Parse` of the `.razor` source | pure **static** markup structure (rename an element, add a class to a plain `<div>`, rewrap) | high — internal, unstable `...Language.Syntax` node types; maintenance risk |

Key point the debate missed: the first rung above find-in-files is **not** tier 3. It is tier 2 — the `#line` map we are *already* generating. Tier 3 is only needed for static markup that never produced a semantic node, and that is the rung carrying the API risk.

## Why tier 2 is cheap — the hard halves already exist

- **Locating** is done. Razor references are stored in `.razor` coordinates (`symbol_references.file_path` = the `.razor`, `line`/`column` are `#line`-mapped source positions, not generated positions). `GetMappedLineSpan` produced them at index time.
- **Writing + safety** are done. `replace_span_in_file` is the write primitive; `complete_edit_plan`'s pre-merge build gates correctness, so a malformed markup edit cannot merge.

The only new code is the connector: **index reference → `.razor` char offset → span → existing span-replace + staging pipeline.**

## The one real subtlety — the span *end*

`symbol_references` stores a *start* (`line`, `column`) plus a `snippet`, not an end position. Two ways to close it:

- **Cheap / now:** at edit time, read the `.razor` and scan the token at the anchor. Clean for identifier-level edits (rename a component usage, retarget a handler). ~1 day for a working prototype.
- **Robust / general:** capture the end at extraction — the mapped span is a `LinePositionSpan` that already carries `Start` **and** `End`, so it is a one-field schema add (`end_line`/`end_column` on `symbol_references`) plus a reindex. After that, *any* anchored construct edits exactly. ~2–3 days total.

Neither touches the risky part; both reuse the existing write + validation path.

## Scope boundary (so it is not oversold)

Tier 2 only reaches markup that produced a semantic node — bindings, expressions, component/handler usages. **Pure static markup has no anchor in the index**, by design: the generated `.g.cs` emits it as a single `AddMarkupContent("<div…>")` literal blob with no per-element `#line`. Static-structure edits therefore fall through to tier 3 or find-in-files, and no amount of index work changes that — it is a property of how the Razor SDK generates code, not a limitation we introduced.

So: if the mutation targets are mostly component/attribute-binding/handler edits, tier 2 covers them. If they are mostly static-HTML restructuring, tier 2 does not help and tier 3 (with its risk) would be required.

## Difficulty summary

- **Prototype (identifier/usage-level, no schema change):** ~1 day.
- **Robust/general (exact end spans, schema add + reindex):** ~2–3 days.
- **Tier 3 (static-markup structure via `RazorSyntaxTree`):** larger and risky; explicitly *not* this plan.

## Non-goals / why not now

- Find-in-files with an index anchor is a known, adequate method for today's edit mix; nothing is blocked.
- Tier 3's unstable API is not worth taking on speculatively.
- If and when a workload shows up that is heavy on component/binding refactors across many `.razor` files, tier 2 is the first thing to build, and it is small.
