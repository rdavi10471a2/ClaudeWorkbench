# CodexBlazor

This is a committed Blazor watched-solution baseline for Codex CLI workflow tests.

It exists so Codex can test the safe edit loop against Blazor-shaped code:

- `.razor` markup as diffable text;
- `.razor.cs` code-behind as indexed C#;
- CSS/text assets as non-semantic protected edits;
- clear Razor-boundary behavior without using a production app as the fixture.

Copy this folder to runtime before human-in-the-loop tests. Do not mutate the
committed baseline as the normal test path.

