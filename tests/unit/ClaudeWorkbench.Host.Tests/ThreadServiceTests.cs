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

    private (ThreadService service, FakeCurrentSession session, string projectsRoot, string sessionsDir) NewService()
    {
        string dir = Path.Combine(Path.GetTempPath(), "cwb-threadsvc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        tempDirs.Add(dir);
        string sessionsDir = Path.Combine(dir, "sessions");
        FakeWorkspace workspace = new(Path.Combine(dir, "threads.sqlite"), @"C:\watched\Solution", sessionsDir);
        string projectsRoot = Path.Combine(dir, "projects");
        Directory.CreateDirectory(projectsRoot);
        ClaudeTranscriptStore transcripts = new(projectsRoot);
        FakeCurrentSession session = new();
        return (new ThreadService(workspace, transcripts, session), session, projectsRoot, sessionsDir);
    }

    private static string WritePrimaryTranscript(string projectsRoot, string sessionId, string content = "{}")
    {
        string projectDir = Path.Combine(projectsRoot, "proj");
        Directory.CreateDirectory(projectDir);
        string path = Path.Combine(projectDir, sessionId + ".jsonl");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Ensure_thread_for_session_creates_then_touches_the_same_thread()
    {
        (ThreadService service, _, _, _) = NewService();

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
        (ThreadService service, _, _, _) = NewService();

        ThreadRecord stub = service.CreateStub("plan the refactor", "why");

        Assert.Equal(ThreadStatus.Planned, stub.Status);
        Assert.True(stub.IsStub);
        Assert.Equal("plan the refactor", stub.Name);
        Assert.Equal("why", stub.Description);
    }

    [Fact]
    public void Active_thread_is_the_one_matching_the_live_session()
    {
        (ThreadService service, FakeCurrentSession session, _, _) = NewService();
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
        (ThreadService service, FakeCurrentSession session, _, _) = NewService();
        ThreadRecord thread = service.EnsureThreadForSession("sess-1");
        session.CurrentSessionId = "sess-1";

        service.RecordAcceptedEdits(["rec-1", "rec-2"]);

        Assert.Equal(2, service.Get(thread.ThreadId)!.AcceptedEditRefs.Count);
    }

    [Fact]
    public void Record_accepted_edits_is_a_noop_with_no_live_session()
    {
        (ThreadService service, FakeCurrentSession session, _, _) = NewService();
        ThreadRecord thread = service.EnsureThreadForSession("sess-1");
        session.CurrentSessionId = null;

        service.RecordAcceptedEdits(["rec-1"]);

        Assert.Empty(service.Get(thread.ThreadId)!.AcceptedEditRefs);
    }

    [Fact]
    public void Get_resume_session_id_returns_the_session_for_a_real_thread_but_not_a_stub()
    {
        (ThreadService service, _, _, _) = NewService();
        ThreadRecord real = service.EnsureThreadForSession("sess-1");
        ThreadRecord stub = service.CreateStub("stub", null);

        Assert.Equal("sess-1", service.GetResumeSessionId(real.ThreadId));
        Assert.Null(service.GetResumeSessionId(stub.ThreadId));
        Assert.Null(service.GetResumeSessionId("unknown"));
    }

    [Fact]
    public void Mirror_transcript_copies_the_primary_into_the_runtime_sessions_dir()
    {
        (ThreadService service, _, string projectsRoot, string sessionsDir) = NewService();
        WritePrimaryTranscript(projectsRoot, "sess-1", "{\"turn\":1}");

        service.MirrorTranscript("sess-1");

        string mirror = Path.Combine(sessionsDir, "sess-1.jsonl");
        Assert.True(File.Exists(mirror));
        Assert.Equal("{\"turn\":1}", File.ReadAllText(mirror));
    }

    [Fact]
    public void Prepare_resume_restores_the_primary_from_the_mirror_and_returns_the_session()
    {
        (ThreadService service, _, string projectsRoot, string sessionsDir) = NewService();
        ThreadRecord thread = service.EnsureThreadForSession("sess-1"); // Cwd = C:\watched\Solution
        Directory.CreateDirectory(sessionsDir);
        File.WriteAllText(Path.Combine(sessionsDir, "sess-1.jsonl"), "SAVED"); // our mirror, no ~/.claude primary

        string? sid = service.PrepareResume(thread.ThreadId);

        Assert.Equal("sess-1", sid);
        // Restored under the encoded cwd (C:\watched\Solution -> C--watched-Solution).
        string restored = Path.Combine(projectsRoot, "C--watched-Solution", "sess-1.jsonl");
        Assert.True(File.Exists(restored));
        Assert.Equal("SAVED", File.ReadAllText(restored));
    }

    [Fact]
    public void Prepare_resume_returns_null_for_a_stub()
    {
        (ThreadService service, _, _, _) = NewService();
        ThreadRecord stub = service.CreateStub("stub", null);
        Assert.Null(service.PrepareResume(stub.ThreadId));
    }

    [Fact]
    public void Delete_thread_removes_the_row_the_primary_and_the_mirror()
    {
        (ThreadService service, _, string projectsRoot, string sessionsDir) = NewService();
        ThreadRecord thread = service.EnsureThreadForSession("sess-1");
        string transcript = WritePrimaryTranscript(projectsRoot, "sess-1");
        service.MirrorTranscript("sess-1");
        string mirror = Path.Combine(sessionsDir, "sess-1.jsonl");
        Assert.True(File.Exists(mirror));

        Assert.True(service.DeleteThread(thread.ThreadId));
        Assert.Null(service.Get(thread.ThreadId));
        Assert.False(File.Exists(transcript));
        Assert.False(File.Exists(mirror));
        Assert.False(service.DeleteThread(thread.ThreadId));
    }

    private sealed class FakeWorkspace : IThreadWorkspace
    {
        public FakeWorkspace(string threadsDatabasePath, string cwd, string sessionsDirectory)
        {
            ThreadsDatabasePath = threadsDatabasePath;
            Cwd = cwd;
            SessionsDirectory = sessionsDirectory;
        }

        public string ThreadsDatabasePath { get; }

        public string Cwd { get; }

        public string SessionsDirectory { get; }
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
