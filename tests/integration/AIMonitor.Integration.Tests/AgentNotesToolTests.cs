using AIMonitor.Core;
using AIMonitor.Logging;
using AIMonitor.McpServer;
using Microsoft.Extensions.Hosting;

namespace AIMonitor.Integration.Tests;

// The write_note family is the agent's ungoverned scratchpad. These tests prove the two
// properties that matter: (1) notes round-trip as ordinary files under the per-workspace
// runtime, OUTSIDE watched source, and (2) every path is confined to the notes folder by the
// tool itself — no absolute path and no '..' escape can reach source or any other runtime
// folder. They run in-process against AIMonitorTools directly (no build, no server), so they
// are fast and can assert the thrown containment error precisely.
public sealed class AgentNotesToolTests
{
    [Fact]
    public void Write_then_read_round_trips_and_lands_under_agent_notes_outside_watched_source()
    {
        McpSurfaceFixture fixture = McpSurfaceFixture.CreateSingleProject();
        AIMonitorTools tools = CreateTools(fixture, out MonitorSettings settings);

        AgentNoteWriteResult write = tools.WriteNote("plan.md", "step one");
        AgentNoteReadResult read = tools.ReadNote("plan.md");

        Assert.True(read.Exists);
        Assert.Equal("step one", read.Content);
        Assert.Equal("plan.md", write.RelativePath);

        // The note lives under runtime\<workspace>\agent-notes and NOT inside watched source.
        string notesRoot = Path.Combine(
            MonitorWorkspacePaths.GetWatchedSolutionWorkspaceRoot(settings),
            "agent-notes");
        Assert.StartsWith(Path.GetFullPath(notesRoot), write.FullPath, StringComparison.OrdinalIgnoreCase);
        Assert.False(
            Path.GetFullPath(write.FullPath).StartsWith(Path.GetFullPath(settings.WatchedProjectFolder), StringComparison.OrdinalIgnoreCase),
            "A note must never resolve inside watched source.");
        Assert.True(File.Exists(write.FullPath));
    }

    [Fact]
    public void Write_with_append_concatenates_instead_of_overwriting()
    {
        McpSurfaceFixture fixture = McpSurfaceFixture.CreateSingleProject();
        AIMonitorTools tools = CreateTools(fixture, out _);

        tools.WriteNote("log.md", "first\n");
        tools.WriteNote("log.md", "second\n", append: true);

        Assert.Equal("first\nsecond\n", tools.ReadNote("log.md").Content);
    }

    [Fact]
    public void Write_creates_nested_folders_and_list_returns_notes()
    {
        McpSurfaceFixture fixture = McpSurfaceFixture.CreateSingleProject();
        AIMonitorTools tools = CreateTools(fixture, out _);

        tools.WriteNote("research/findings.md", "a");
        tools.WriteNote("todo.md", "b");

        IReadOnlyList<AgentNoteInfo> notes = tools.ListNotes();
        string[] relativePaths = notes.Select(note => note.RelativePath.Replace('\\', '/')).ToArray();
        Assert.Contains("research/findings.md", relativePaths);
        Assert.Contains("todo.md", relativePaths);
    }

    [Fact]
    public void Delete_removes_the_note_file()
    {
        McpSurfaceFixture fixture = McpSurfaceFixture.CreateSingleProject();
        AIMonitorTools tools = CreateTools(fixture, out _);
        AgentNoteWriteResult write = tools.WriteNote("scratch.md", "temp");
        Assert.True(File.Exists(write.FullPath));

        AgentNoteDeleteResult delete = tools.DeleteNote("scratch.md");

        Assert.True(delete.Deleted);
        Assert.False(File.Exists(write.FullPath));
        Assert.False(tools.ReadNote("scratch.md").Exists);
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("../../escape.txt")]
    [InlineData("sub/../../escape.txt")]
    [InlineData("notes/../../../outside.txt")]
    public void Relative_escape_attempts_are_rejected(string relativePath)
    {
        McpSurfaceFixture fixture = McpSurfaceFixture.CreateSingleProject();
        AIMonitorTools tools = CreateTools(fixture, out _);

        Assert.Throws<InvalidOperationException>(() => tools.WriteNote(relativePath, "nope"));
    }

    [Fact]
    public void Absolute_paths_are_rejected()
    {
        McpSurfaceFixture fixture = McpSurfaceFixture.CreateSingleProject();
        AIMonitorTools tools = CreateTools(fixture, out MonitorSettings settings);

        string absoluteTarget = Path.Combine(settings.WatchedProjectFolder, "Program.cs");
        Assert.Throws<InvalidOperationException>(() => tools.WriteNote(absoluteTarget, "nope"));
        Assert.Throws<InvalidOperationException>(() => tools.WriteNote(Path.Combine(Path.GetTempPath(), "x.txt"), "nope"));
    }

    [Fact]
    public void Empty_path_is_rejected()
    {
        McpSurfaceFixture fixture = McpSurfaceFixture.CreateSingleProject();
        AIMonitorTools tools = CreateTools(fixture, out _);

        Assert.Throws<ArgumentException>(() => tools.WriteNote("   ", "nope"));
    }

    private static AIMonitorTools CreateTools(McpSurfaceFixture fixture, out MonitorSettings settings)
    {
        settings = MonitorSettings.Create(
            fixture.RepositoryRoot,
            fixture.WatchedProjectPath,
            fixture.RuntimeRoot);
        WorkspaceManager workspace = new(fixture.RepositoryRoot, fixture.RuntimeRoot, settings);
        NullMonitorLogger logger = new();
        return new AIMonitorTools(
            workspace,
            new AIMonitorMcpRuntimeState(logger),
            new StubApplicationLifetime(),
            logger);
    }

    private sealed class StubApplicationLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }
    }

    private sealed class NullMonitorLogger : IMonitorLogger
    {
        public void Write(
            MonitorLogLevel level,
            string source,
            string eventName,
            string message,
            IReadOnlyDictionary<string, string>? properties = null)
        {
        }
    }
}
