using ClaudeWorkbench.Host.Services;
using ClaudeWorkbench.Host.Threads;

namespace ClaudeWorkbench.Host.Tests;

// The thread lifecycle service over a throwaway threads.sqlite + a throwaway ~/.claude projects
// root, with a fake "current session". Proves autosave (create-then-touch), stub creation, the
// COMPUTED Active thread, provenance linking to the live thread, resume-id lookup, and hard delete
// removing both the row and the transcript.
public sealed class ThreadServiceTests : IDisposable
{
    private readonly List<string> tempDirs = new();

    private (ThreadService service, FakeCurrentSession session, string projectsRoot) NewService()
    {
        string dir = Path.Combine(Path.GetTempPath(), "cwb-threadsvc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        tempDirs.Add(dir);
        FakeWorkspace workspace = new(Path.Combine(dir, "threads.sqlite"), @"C:\watched\Solution");
        string projectsRoot = Path.Combine(dir, "projects");
        Directory.CreateDirectory(projectsRoot);
        ClaudeTranscriptStore transcripts = new(projectsRoot);
        FakeCurrentSession session = new();
        return (new ThreadService(workspace, transcripts, session), session, projectsRoot);
    }

    [Fact]
    public void Ensure_thread_for_session_creates_then_touches_the_same_thread()
    {
        (ThreadService service, _, _) = NewService();

        ThreadRecord created = service.EnsureThreadForSession("sess-1");
        ThreadRecord again = service.EnsureThreadForSession("sess-1");

        Assert.Equal(created.ThreadId, again.ThreadId);
        Assert.Single(service.List());
        Assert.Equal(ThreadStatus.Archived, created.Status);
        Assert.StartsWith("discussion-", created.Name);
        Assert.Equal(@"C:\watched\Solution", created.Cwd);
    }

    [Fact]
    public void Create_stub_is_planned_with_no_session()
    {
        (ThreadService service, _, _) = NewService();

        ThreadRecord stub = service.CreateStub("plan the refactor", "why");

        Assert.Equal(ThreadStatus.Planned, stub.Status);
        Assert.True(stub.IsStub);
        Assert.Equal("plan the refactor", stub.Name);
        Assert.Equal("why", stub.Description);
    }

    [Fact]
    public void Active_thread_is_the_one_matching_the_live_session()
    {
        (ThreadService service, FakeCurrentSession session, _) = NewService();
        ThreadRecord a = service.EnsureThreadForSession("sess-a");
        service.EnsureThreadForSession("sess-b");

        session.CurrentSessionId = null;
        Assert.Null(service.ActiveThread());

        session.CurrentSessionId = "sess-a";
        Assert.Equal(a.ThreadId, service.ActiveThread()!.ThreadId);
    }

    [Fact]
    public void Record_accepted_edits_links_them_to_the_live_thread()
    {
        (ThreadService service, FakeCurrentSession session, _) = NewService();
        ThreadRecord thread = service.EnsureThreadForSession("sess-1");
        session.CurrentSessionId = "sess-1";

        service.RecordAcceptedEdits(["rec-1", "rec-2"]);

        Assert.Equal(2, service.Get(thread.ThreadId)!.AcceptedEditRefs.Count);
    }

    [Fact]
    public void Record_accepted_edits_is_a_noop_with_no_live_session()
    {
        (ThreadService service, FakeCurrentSession session, _) = NewService();
        ThreadRecord thread = service.EnsureThreadForSession("sess-1");
        session.CurrentSessionId = null;

        service.RecordAcceptedEdits(["rec-1"]);

        Assert.Empty(service.Get(thread.ThreadId)!.AcceptedEditRefs);
    }

    [Fact]
    public void Get_resume_session_id_returns_the_session_for_a_real_thread_but_not_a_stub()
    {
        (ThreadService service, _, _) = NewService();
        ThreadRecord real = service.EnsureThreadForSession("sess-1");
        ThreadRecord stub = service.CreateStub("stub", null);

        Assert.Equal("sess-1", service.GetResumeSessionId(real.ThreadId));
        Assert.Null(service.GetResumeSessionId(stub.ThreadId));
        Assert.Null(service.GetResumeSessionId("unknown"));
    }

    [Fact]
    public void Delete_thread_removes_the_row_and_the_transcript()
    {
        (ThreadService service, _, string projectsRoot) = NewService();
        ThreadRecord thread = service.EnsureThreadForSession("sess-1");
        string projectDir = Path.Combine(projectsRoot, "proj");
        Directory.CreateDirectory(projectDir);
        string transcript = Path.Combine(projectDir, "sess-1.jsonl");
        File.WriteAllText(transcript, "{}");

        Assert.True(service.DeleteThread(thread.ThreadId));
        Assert.Null(service.Get(thread.ThreadId));
        Assert.False(File.Exists(transcript));
        Assert.False(service.DeleteThread(thread.ThreadId));
    }

    private sealed class FakeWorkspace : IThreadWorkspace
    {
        public FakeWorkspace(string threadsDatabasePath, string cwd)
        {
            ThreadsDatabasePath = threadsDatabasePath;
            Cwd = cwd;
        }

        public string ThreadsDatabasePath { get; }

        public string Cwd { get; }
    }

    private sealed class FakeCurrentSession : ICurrentSession
    {
        public string? CurrentSessionId { get; set; }
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (string dir in tempDirs)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
