# CodexWindows

This is a committed WinForms watched-solution baseline for Codex CLI workflow tests.

It exists so Codex can test the safe edit loop against Windows-style code:

- ordinary C# files;
- WinForms partial designer files;
- event/control-adjacent source-map noise;
- text replacement and new-file review through the CLI workflow.

Copy this folder to runtime before human-in-the-loop tests. Do not mutate the
committed baseline as the normal test path.

