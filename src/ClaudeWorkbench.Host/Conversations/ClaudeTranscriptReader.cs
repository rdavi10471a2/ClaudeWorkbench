using System.Globalization;
using System.Text.Json;
using ClaudeWorkbench.Host.Console;
using ClaudeWorkbench.Host.Services;

namespace ClaudeWorkbench.Host.Conversations;

// Reconstructs a renderable transcript window from an app-owned mirror JSONL file (the Claude Agent
// SDK / claude CLI on-disk format), so the chat pane can be repainted on resume to match the history
// the agent's context was just restored to. See docs/plans/transcript-rehydration-on-resume.md.
//
// SOURCE = the RUNTIME mirror only. Callers pass an absolute mirror path
// (IConversationWorkspace.SessionsDirectory + the thread's transcript file). This reader NEVER touches
// ~/.claude — that copy is outside the app and may be swept or compacted; the mirror is authoritative.
//
// "counted" interactions = a human message OR an agent text reply. Tool calls, tool results and images
// are rendered in place but do NOT count toward the window size. The mapping mirrors the live
// SidecarOperatorConsole.Transcript projection so restored and live entries render identically.
public static class ClaudeTranscriptReader
{
    // Read the last `counted` interactions from the mirror as renderable entries, oldest-first, with a
    // trailing Notice divider summarizing what was restored. Returns an empty list (never throws) when
    // the path is missing/unreadable or holds no conversational content.
    public static IReadOnlyList<TranscriptEntry> ReadWindow(string mirrorPath, int counted = 50)
    {
        if (counted <= 0 || string.IsNullOrWhiteSpace(mirrorPath) || !File.Exists(mirrorPath))
        {
            return [];
        }

        List<TranscriptEntry> entries;
        try
        {
            entries = Reconstruct(mirrorPath);
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }

        if (entries.Count == 0)
        {
            return [];
        }

        (int start, int availableCounted) = WindowStart(entries, counted);
        List<TranscriptEntry> window = entries.GetRange(start, entries.Count - start);
        int shownCounted = Math.Min(availableCounted, counted);
        bool wholeConversation = availableCounted <= counted;
        window.Add(NoticeFor(window, shownCounted, wholeConversation));
        return window;
    }

    // Parse the mirror into linear entries (oldest-first), deduped by uuid (auto-compaction can repeat
    // uuids), skipping subagent sidechains, meta lines, and synthetic (non-human) user strings.
    private static List<TranscriptEntry> Reconstruct(string mirrorPath)
    {
        HashSet<string> seenUuids = new(StringComparer.Ordinal);
        List<TranscriptEntry> entries = [];

        foreach (string line in File.ReadLines(mirrorPath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonElement root;
            try
            {
                using JsonDocument document = JsonDocument.Parse(line);
                root = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                continue;
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? type = GetString(root, "type");
            if (type is not ("user" or "assistant"))
            {
                continue;
            }

            if (GetBool(root, "isSidechain") || GetBool(root, "isMeta"))
            {
                continue;
            }

            string? uuid = GetString(root, "uuid");
            if (uuid is not null && !seenUuids.Add(uuid))
            {
                continue;
            }

            string time = FormatTime(GetString(root, "timestamp"));
            AppendEntries(entries, type, root, time);
        }

        return entries;
    }

    private static void AppendEntries(List<TranscriptEntry> entries, string type, JsonElement root, string time)
    {
        if (!root.TryGetProperty("message", out JsonElement message)
            || message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("content", out JsonElement content))
        {
            return;
        }

        if (type == "assistant")
        {
            AppendAssistant(entries, content, time);
        }
        else
        {
            AppendUser(entries, content, time);
        }
    }

    private static void AppendAssistant(List<TranscriptEntry> entries, JsonElement content, string time)
    {
        // Assistant content is normally an array of text/tool_use/thinking blocks; a bare string is
        // uncommon but handled. Thinking and other block types are skipped (the live view shows only
        // final text + tool calls).
        if (content.ValueKind == JsonValueKind.String)
        {
            string text = content.GetString() ?? string.Empty;
            if (text.Trim().Length > 0)
            {
                entries.Add(new TranscriptEntry(TranscriptKind.Assistant, text, time));
            }

            return;
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement block in content.EnumerateArray())
        {
            string? blockType = GetString(block, "type");
            if (blockType == "text")
            {
                string text = GetString(block, "text") ?? string.Empty;
                if (text.Trim().Length > 0)
                {
                    entries.Add(new TranscriptEntry(TranscriptKind.Assistant, text, time));
                }
            }
            else if (blockType == "tool_use")
            {
                entries.Add(ToolOrImageEntry(block, time));
            }
        }
    }

    private static void AppendUser(List<TranscriptEntry> entries, JsonElement content, string time)
    {
        // A string under the user role is either the operator's typed message or a synthetic injection
        // (task notifications, system reminders, slash-command wrappers, interrupt markers). Only the
        // former is a real "interaction".
        if (content.ValueKind == JsonValueKind.String)
        {
            string text = content.GetString() ?? string.Empty;
            if (text.Trim().Length == 0 || IsSynthetic(text))
            {
                return;
            }

            entries.Add(new TranscriptEntry(TranscriptKind.User, text, time));
            return;
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement block in content.EnumerateArray())
        {
            string? blockType = GetString(block, "type");
            if (blockType == "text")
            {
                // Rare: a user message carrying explicit text blocks.
                string text = GetString(block, "text") ?? string.Empty;
                if (text.Trim().Length > 0 && !IsSynthetic(text))
                {
                    entries.Add(new TranscriptEntry(TranscriptKind.User, text, time));
                }
            }
            // tool_result blocks (tool output injected under the user role) and inline base64 image
            // blocks (no local path to serve) are intentionally not emitted: the live view shows tool
            // OUTPUT only via the preceding ToolCall line, and a base64 image can't go through
            // /local-file. Both are non-counting either way.
        }
    }

    // A tool_use block -> a ToolCall line, or an inline Image when the agent read an image file that
    // still exists on disk (mirrors SidecarOperatorConsole.ToolOrImageEntry so /local-file can serve it).
    private static TranscriptEntry ToolOrImageEntry(JsonElement toolUse, string time)
    {
        string name = GetString(toolUse, "name") ?? string.Empty;
        JsonElement? input = toolUse.TryGetProperty("input", out JsonElement value) ? value : null;

        if (string.Equals(name, "Read", StringComparison.OrdinalIgnoreCase))
        {
            string? path = FilePathOf(input);
            if (path is not null && ImageExtensions.Contains(Path.GetExtension(path)) && FileExists(path))
            {
                return new TranscriptEntry(TranscriptKind.Image, path, time);
            }
        }

        return new TranscriptEntry(TranscriptKind.ToolCall, ApprovalFormatter.ShortLabel(name, input), time);
    }

    // Walk backward counting only User/Assistant entries; the window starts at the entry that makes the
    // count reach `counted` (or at 0 when the conversation has fewer). Returns (startIndex, totalCounted).
    private static (int Start, int AvailableCounted) WindowStart(IReadOnlyList<TranscriptEntry> entries, int counted)
    {
        int total = 0;
        int hitTarget = -1;
        for (int i = entries.Count - 1; i >= 0; i--)
        {
            if (IsCounted(entries[i].Kind))
            {
                total++;
                if (total == counted && hitTarget < 0)
                {
                    hitTarget = i;
                }
            }
        }

        return (hitTarget < 0 ? 0 : hitTarget, total);
    }

    private static bool IsCounted(TranscriptKind kind) => kind is TranscriptKind.User or TranscriptKind.Assistant;

    private static TranscriptEntry NoticeFor(IReadOnlyList<TranscriptEntry> window, int shownCounted, bool wholeConversation)
    {
        int fromClaude = window.Count(entry => entry.Kind == TranscriptKind.Assistant);
        int fromYou = window.Count(entry => entry.Kind == TranscriptKind.User);
        string scope = wholeConversation
            ? $"Restored this conversation from history — {shownCounted} interaction{Plural(shownCounted)}"
            : $"Restored the last {shownCounted} interactions from history";
        string text = $"{scope} ({fromClaude} from Claude, {fromYou} from you); tool calls included. New messages continue below.";
        return new TranscriptEntry(TranscriptKind.Notice, text, string.Empty);
    }

    private static string Plural(int n) => n == 1 ? string.Empty : "s";

    // Synthetic (non-human) user strings the SDK/host inject into the transcript. Match on the leading
    // tag/marker after trimming leading whitespace.
    private static bool IsSynthetic(string text)
    {
        string trimmed = text.TrimStart();
        foreach (string marker in SyntheticMarkers)
        {
            if (trimmed.StartsWith(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static readonly string[] SyntheticMarkers =
    [
        "<task-notification",
        "<system-reminder",
        "<local-command-stdout",
        "<local-command-caveat",
        "<command-name",
        "<command-message",
        "<command-args",
        "[Request interrupted",
    ];

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg", ".bmp", ".ico", ".avif",
    };

    private static bool FileExists(string path)
    {
        try { return File.Exists(path); }
        catch (Exception) { return false; }
    }

    private static string? FilePathOf(JsonElement? input)
    {
        if (input is not JsonElement element || element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (string key in new[] { "file_path", "path", "sourceFilePath", "filePath" })
        {
            if (element.TryGetProperty(key, out JsonElement value)
                && value.ValueKind == JsonValueKind.String)
            {
                string? path = value.GetString();
                if (!string.IsNullOrWhiteSpace(path))
                {
                    return path;
                }
            }
        }

        return null;
    }

    private static string FormatTime(string? isoTimestamp)
    {
        if (string.IsNullOrWhiteSpace(isoTimestamp))
        {
            return string.Empty;
        }

        return DateTimeOffset.TryParse(isoTimestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset when)
            ? when.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : string.Empty;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

    private static bool GetBool(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.True;
}
