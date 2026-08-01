# Archive upload extraction

Status: **BUILT (2026-07-30).** Operator convenience; low-traffic ("may be good to have").

## Why

Claude can't ingest a raw archive: the Read tool can't parse a binary `.zip`, and even the files inside
face hard per-file ceilings (≈10 MB per image, ≈25k tokens per text file via Read). So "attach a zip and
let Claude read it" can't work as a raw-file handoff. Instead the host **extracts** the archive and hands
the agent the **folder**, which it can explore lazily.

## Flow

1. Operator drops an archive on the composer paste zone → `POST /uploads/paste` lands it in `uploads/`
   (allowlist gate in `LocalFileEndpoints`).
2. If the saved file is a supported archive, `ArchiveExtractor.Extract` unpacks it into a **new unique
   folder** under `uploads/` (`foo/`, then `foo (2)/`… — never overwrites), and the raw archive is
   deleted (redundant once extracted).
3. The endpoint returns the **folder** path (`kind:"folder"`, `fileCount`) instead of a file. It becomes
   a normal pending attachment.
4. On send, `AssistantTab.ComposePrompt` detects a directory attachment and emits a **folder handoff**:
   the readable folder path + a capped manifest (≤200 relative paths, "+N more"). The agent uses
   Glob/Grep/Read inside it — `uploads/` is already in the sidecar's `additionalDirectories` and
   `Read/Grep/Glob` are granted when `allowNativeReads` is on (the default).

**Folder, not files** — so we never attach hundreds of files (dodging the image/attachment count limits)
and the agent pulls only what it needs into context.

## Supported types (in-box, no external dependency)

`.zip` (`System.IO.Compression`), `.tar` / `.tar.gz` / `.tgz` (`System.Formats.Tar` + `GZipStream`), and a
bare `.gz` single file. `.7z`/`.rar` are **not** accepted — they'd need a third-party library (SharpCompress),
so accepting them would be an accepted-but-unextractable dead-end. If ever wanted, add SharpCompress and a
branch in `ArchiveExtractor.Extract`.

## Guards (always on)

- **Zip-slip**: every entry path is resolved and must stay under the target folder; `..`/absolute/drive-
  relative entries are refused (`SafeDestination`).
- **Zip-bomb**: total uncompressed bytes (500 MB) and entry count (5,000) are capped; a bare `.gz` is
  capped mid-stream since its size isn't known up front. On any trip the partial folder is deleted and the
  upload returns an error.

## Size context (why the upload cap wasn't raised)

The `/uploads/paste` cap stays at 25 MB. Claude's real ingest ceilings are all smaller — 10 MB/image,
≈25k tokens/text via Read, 32 MB whole-request — so raising the upload cap would just admit files Claude
rejects. A zip only needs to *land* (it never goes to the API), so 25 MB (under Kestrel's ~30 MB default
body limit) is a fine outer bound.

## Files

- `src/ClaudeWorkbench.Host/Services/ArchiveExtractor.cs` (new) — extraction + guards; unit-tested.
- `src/ClaudeWorkbench.Host/Services/LocalFileEndpoints.cs` — allowlist (`.zip/.tar/.gz/.tgz`), extract-on-upload.
- `src/ClaudeWorkbench.Host/Components/Pages/Tabs/AssistantTab.razor.cs` — folder handoff + manifest in `ComposePrompt`.
- `tests/unit/ClaudeWorkbench.Host.Tests/ArchiveExtractorTests.cs` (new) — happy path, slip, bomb, tar.gz, collision, unsupported.

## Not done / future

- No UI distinction for a folder chip vs a file chip (shows the folder name). Fine for now.
- No `.7z`/`.rar` (needs SharpCompress).
- Manifest is generated at send time from disk; a huge tree is truncated to 200 with a "+N more" note.
