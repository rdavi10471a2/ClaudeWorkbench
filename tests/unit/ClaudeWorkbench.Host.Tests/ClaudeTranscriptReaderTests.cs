using System.Text;
using System.Text.Json;
using ClaudeWorkbench.Host.Console;
using ClaudeWorkbench.Host.Conversations;

namespace ClaudeWorkbench.Host.Tests;

// ClaudeTranscriptReader reconstructs a renderable window from a mirror JSONL (SDK/CLI format). These
// tests build tiny fixture files and assert the counting rule (human/assistant count; tool calls don't),
// the exclusions (sidechains, meta, synthetic user strings), uuid dedupe, and the trailing Notice.
public sealed class ClaudeTranscriptReaderTests : IDisposable
{
    private readonly List<string> tempFiles = new();

    private string WriteJsonl(IEnumerable<object> lines)
    {
        string path = Path.Combine(Path.GetTempPath(), "cwb-reader-" + Guid.NewGuid().ToString("N") + ".jsonl");
        StringBuilder builder = new();
        foreach (object line in lines)
        {
            builder.Append(JsonSerializer.Serialize(line)).Append('\n');
        }

        File.WriteAllText(path, builder.ToString());
        tempFiles.Add(path);
        return path;
    }

    private static object UserText(string text, string? uuid = null) => new
    {
        type = "user",
        uuid = uuid ?? Guid.NewGuid().ToString("N"),
        timestamp = "2026-07-30T12:00:00.000Z",
        message = new { role = "user", content = text },
    };

    private static object AssistantText(string text, string? uuid = null) => new
    {
        type = "assistant",
        uuid = uuid ?? Guid.NewGuid().ToString("N"),
        timestamp = "2026-07-30T12:00:01.000Z",
        message = new { role = "assistant", content = new object[] { new { type = "text", text } } },
    };

    private static object AssistantToolUse(string name) => new
    {
        type = "assistant",
        uuid = Guid.NewGuid().ToString("N"),
        message = new { role = "assistant", content = new object[] { new { type = "tool_use", name, input = new { } } } },
    };

    private static object UserToolResult() => new
    {
        type = "user",
        uuid = Guid.NewGuid().ToString("N"),
        message = new { role = "user", content = new object[] { new { type = "tool_result", tool_use_id = "t1", is_error = false } } },
    };

    [Fact]
    public void Counts_only_human_and_assistant_but_renders_tool_calls()
    {
        string path = WriteJsonl(new[]
        {
            UserText("hello"),
            AssistantToolUse("Bash"),
            UserToolResult(),
            AssistantText("world"),
        });

        IReadOnlyList<TranscriptEntry> window = ClaudeTranscriptReader.ReadWindow(path, counted: 50);

        // User + Assistant + ToolCall rendered; tool_result dropped; plus the trailing Notice.
        Assert.Equal(TranscriptKind.User, window[0].Kind);
        Assert.Equal(TranscriptKind.ToolCall, window[1].Kind);
        Assert.Equal(TranscriptKind.Assistant, window[2].Kind);
        Assert.Equal(TranscriptKind.Notice, window[^1].Kind);
        Assert.DoesNotContain(window, e => e.Text == "(result)");
    }

    [Fact]
    public void Trims_to_last_N_counted_but_keeps_tool_calls_in_the_window()
    {
        List<object> lines = new();
        for (int i = 0; i < 60; i++)
        {
            lines.Add(UserText($"msg {i}"));
            lines.Add(AssistantToolUse("Read"));
            lines.Add(AssistantText($"reply {i}"));
        }

        string path = WriteJsonl(lines);
        IReadOnlyList<TranscriptEntry> window = ClaudeTranscriptReader.ReadWindow(path, counted: 50);

        int counted = window.Count(e => e.Kind is TranscriptKind.User or TranscriptKind.Assistant);
        Assert.Equal(50, counted);
        // Tool calls inside the window are retained (not counted, but rendered).
        Assert.Contains(window, e => e.Kind == TranscriptKind.ToolCall);
        // The last human message must be present; the very first ones must be trimmed away.
        Assert.Contains(window, e => e.Text == "msg 59");
        Assert.DoesNotContain(window, e => e.Text == "msg 0");
    }

    [Fact]
    public void Excludes_sidechains_meta_and_synthetic_user_strings()
    {
        string path = WriteJsonl(new object[]
        {
            UserText("<system-reminder>bg context</system-reminder>"),
            UserText("<task-notification>done</task-notification>"),
            new { type = "assistant", uuid = "s1", isSidechain = true, message = new { role = "assistant", content = new object[] { new { type = "text", text = "subagent" } } } },
            new { type = "assistant", uuid = "m1", isMeta = true, message = new { role = "assistant", content = new object[] { new { type = "text", text = "meta" } } } },
            UserText("real question"),
            AssistantText("real answer"),
        });

        IReadOnlyList<TranscriptEntry> window = ClaudeTranscriptReader.ReadWindow(path, counted: 50);

        Assert.DoesNotContain(window, e => e.Text.Contains("system-reminder"));
        Assert.DoesNotContain(window, e => e.Text.Contains("task-notification"));
        Assert.DoesNotContain(window, e => e.Text == "subagent");
        Assert.DoesNotContain(window, e => e.Text == "meta");
        Assert.Contains(window, e => e.Text == "real question");
        Assert.Contains(window, e => e.Text == "real answer");
        int counted = window.Count(e => e.Kind is TranscriptKind.User or TranscriptKind.Assistant);
        Assert.Equal(2, counted);
    }

    [Fact]
    public void Dedupes_by_uuid()
    {
        string path = WriteJsonl(new[]
        {
            UserText("once", uuid: "dup"),
            UserText("once", uuid: "dup"),
            AssistantText("ok"),
        });

        IReadOnlyList<TranscriptEntry> window = ClaudeTranscriptReader.ReadWindow(path, counted: 50);

        Assert.Equal(1, window.Count(e => e.Text == "once"));
    }

    [Fact]
    public void Missing_or_empty_file_returns_empty()
    {
        Assert.Empty(ClaudeTranscriptReader.ReadWindow(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N") + ".jsonl")));
        string empty = WriteJsonl(Array.Empty<object>());
        Assert.Empty(ClaudeTranscriptReader.ReadWindow(empty));
    }

    [Fact]
    public void Notice_summarizes_the_restored_window()
    {
        string path = WriteJsonl(new[]
        {
            UserText("q"),
            AssistantText("a"),
        });

        IReadOnlyList<TranscriptEntry> window = ClaudeTranscriptReader.ReadWindow(path, counted: 50);
        TranscriptEntry notice = window[^1];

        Assert.Equal(TranscriptKind.Notice, notice.Kind);
        Assert.Contains("1 from Claude", notice.Text);
        Assert.Contains("1 from you", notice.Text);
    }

    public void Dispose()
    {
        foreach (string file in tempFiles)
        {
            try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
