using ClaudeWorkbench.Host.Services;
using ClaudeWorkbench.Host.Conversations;

namespace ClaudeWorkbench.Host.Tests;

// The thread lifecycle service over a throwaway threads.sqlite + a throwaway ~/.claude projects
// root, with a fake "current session". Proves autosave (create-then-touch), stub creation, the
// COMPUTED Active thread, provenance linking to the live thread, resume-id lookup, and hard delete
// removing both the row and the transcript.
public sealed class ConversationServiceTests : IDisposable
{
    private readonly List<string> tempDirs = new();

    private (ConversationService service, FakeCurrentSession session, string projectsRoot, string sessionsDir) NewService()
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
        return (new ConversationService(workspace, transcripts, session), session, projectsRoot, sessionsDir);
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
        (ConversationService service, _, _, _) = NewService();

        ConversationRecord created = service.EnsureConversationForSession("sess-1");
        ConversationRecord again = service.EnsureConversationForSession("sess-1");

        Assert.Equal(created.ConversationId, again.ConversationId);
        Assert.Single(service.List());
        Assert.Equal(ConversationStatus.Archived, created.Status);
        Assert.StartsWith("conversation-", created.Name);
        Assert.Equal(@"C:\watched\Solution", created.Cwd);
    }

    [Fact]
    public void Start_named_thread_persists_immediately_with_no_session()
    {
        (ConversationService service, _, _, _) = NewService();

        ConversationRecord started = service.StartNamedConversation("my refactor");

        Assert.Equal("my refactor", started.Name);
        Assert.Null(started.SessionId);
        Assert.True(started.IsStub);
        // It's persisted right away (visible in the board before any turn).
        Assert.Single(service.List());
        Assert.Equal("my refactor", service.Get(started.ConversationId)!.Name);
    }

    [Fact]
    public void Start_named_thread_with_blank_takes_the_default_name()
    {
        (ConversationService service, _, _, _) = NewService();

        ConversationRecord started = service.StartNamedConversation("   ");

        Assert.StartsWith("conversation-", started.Name);
    }

    [Fact]
    public void Pending_thread_is_active_until_a_live_session_binds_it()
    {
        // No live session (the fake defaults CurrentSessionId to null).
        (ConversationService service, _, _, _) = NewService();

        ConversationRecord started = service.StartNamedConversation("in progress");

        // No live session yet -> the just-started thread is the Active one.
        Assert.Equal(started.ConversationId, service.ActiveConversation()!.ConversationId);
    }

    [Fact]
    public void First_turn_adopts_the_pending_thread_instead_of_creating_a_new_one()
    {
        (ConversationService service, _, _, _) = NewService();
        ConversationRecord started = service.StartNamedConversation("adopt me");

        ConversationRecord adopted = service.EnsureConversationForSession("sess-live");

        // Same row, now bound to the session — not a duplicate.
        Assert.Equal(started.ConversationId, adopted.ConversationId);
        Assert.Equal("adopt me", adopted.Name);
        Assert.Equal("sess-live", adopted.SessionId);
        Assert.Single(service.List());

        // The pending pointer is consumed: the next session creates a fresh thread.
        ConversationRecord next = service.EnsureConversationForSession("sess-2");
        Assert.NotEqual(started.ConversationId, next.ConversationId);
        Assert.Equal(2, service.List().Count);
    }

    [Fact]
    public void Ensure_current_thread_creates_a_nameable_default_when_none_exists()
    {
        // Fresh start: there must be a current conversation to name before the first turn.
        (ConversationService service, _, _, _) = NewService();

        ConversationRecord current = service.EnsureCurrentConversation();

        Assert.True(current.IsStub);
        Assert.StartsWith("conversation-", current.Name);
        Assert.Equal(current.ConversationId, service.ActiveConversation()!.ConversationId);
        Assert.Single(service.List());

        // Idempotent: it doesn't spawn a second one.
        ConversationRecord again = service.EnsureCurrentConversation();
        Assert.Equal(current.ConversationId, again.ConversationId);
        Assert.Single(service.List());
    }

    [Fact]
    public void Ensure_current_thread_reuses_an_unused_default_stub_rather_than_accumulating()
    {
        (ConversationService service, _, _, _) = NewService();
        ConversationRecord a = service.StartNamedConversation(null); // default stub A (pending)
        ConversationRecord b = service.StartNamedConversation(null); // default stub B (pending); A now unadopted
        service.DeleteConversation(b.ConversationId);                // clears pending; A remains unadopted

        ConversationRecord current = service.EnsureCurrentConversation();

        // Reused A instead of creating a third empty default.
        Assert.Equal(a.ConversationId, current.ConversationId);
        Assert.Single(service.List());
    }

    [Fact]
    public void Discarding_the_pending_thread_clears_it_from_active()
    {
        // No live session (the fake defaults CurrentSessionId to null).
        (ConversationService service, _, _, _) = NewService();
        ConversationRecord started = service.StartNamedConversation("throwaway");

        Assert.True(service.DeleteConversation(started.ConversationId));

        Assert.Null(service.ActiveConversation());
        Assert.Empty(service.List());
        // A later turn does NOT resurrect it — it creates a fresh thread.
        ConversationRecord next = service.EnsureConversationForSession("sess-live");
        Assert.NotEqual(started.ConversationId, next.ConversationId);
    }

    [Fact]
    public void Thread_created_event_fires_only_when_a_new_thread_is_created()
    {
        (ConversationService service, _, _, _) = NewService();
        int count = 0;
        ConversationRecord? last = null;
        service.ConversationCreated += thread => { count++; last = thread; };

        service.EnsureConversationForSession("sess-1"); // creates -> fires
        service.EnsureConversationForSession("sess-1"); // touch -> no fire

        Assert.Equal(1, count);
        Assert.Equal("sess-1", last!.SessionId);
    }

    [Fact]
    public void Active_thread_is_the_one_matching_the_live_session()
    {
        (ConversationService service, FakeCurrentSession session, _, _) = NewService();
        ConversationRecord a = service.EnsureConversationForSession("sess-a");
        service.EnsureConversationForSession("sess-b");

        session.CurrentSessionId = null;
        Assert.Null(service.ActiveConversation());

        session.CurrentSessionId = "sess-a";
        Assert.Equal(a.ConversationId, service.ActiveConversation()!.ConversationId);
    }

    [Fact]
    public void Record_accepted_edits_links_them_to_the_live_thread()
    {
        (ConversationService service, FakeCurrentSession session, _, _) = NewService();
        ConversationRecord thread = service.EnsureConversationForSession("sess-1");
        session.CurrentSessionId = "sess-1";

        service.RecordAcceptedEdits(["rec-1", "rec-2"]);

        Assert.Equal(2, service.Get(thread.ConversationId)!.AcceptedEditRefs.Count);
    }

    [Fact]
    public void Record_accepted_edits_is_a_noop_with_no_live_session()
    {
        (ConversationService service, FakeCurrentSession session, _, _) = NewService();
        ConversationRecord thread = service.EnsureConversationForSession("sess-1");
        session.CurrentSessionId = null;

        service.RecordAcceptedEdits(["rec-1"]);

        Assert.Empty(service.Get(thread.ConversationId)!.AcceptedEditRefs);
    }

    [Fact]
    public void Get_resume_session_id_returns_the_session_for_a_real_thread()
    {
        (ConversationService service, _, _, _) = NewService();
        ConversationRecord real = service.EnsureConversationForSession("sess-1");

        Assert.Equal("sess-1", service.GetResumeSessionId(real.ConversationId));
        Assert.Null(service.GetResumeSessionId("unknown"));
    }

    [Fact]
    public void Mirror_transcript_copies_the_primary_into_a_readable_named_file_recorded_on_the_thread()
    {
        (ConversationService service, _, string projectsRoot, string sessionsDir) = NewService();
        ConversationRecord thread = service.EnsureConversationForSession("sess-1"); // gives it a (default) name
        WritePrimaryTranscript(projectsRoot, "sess-1", "{\"turn\":1}");

        service.MirrorTranscript("sess-1");

        // The mirror filename is derived from the name (no GUID) and recorded on the thread row.
        string? file = service.Get(thread.ConversationId)!.TranscriptFile;
        Assert.NotNull(file);
        Assert.EndsWith(".jsonl", file);
        Assert.DoesNotContain("sess-1", file); // the session GUID is NOT in the filename
        string mirror = Path.Combine(sessionsDir, file!);
        Assert.True(File.Exists(mirror));
        Assert.Equal("{\"turn\":1}", File.ReadAllText(mirror));
    }

    [Fact]
    public void Prepare_resume_restores_the_primary_from_the_mirror_and_returns_the_session()
    {
        (ConversationService service, _, string projectsRoot, _) = NewService();
        ConversationRecord thread = service.EnsureConversationForSession("sess-1"); // Cwd = C:\watched\Solution
        // Mirror a primary so the thread records its readable transcript filename, then remove the
        // primary (as if Claude swept its own copy) — resume must restore from our mirror.
        string primary = WritePrimaryTranscript(projectsRoot, "sess-1", "SAVED");
        service.MirrorTranscript("sess-1");
        File.Delete(primary);

        string? sid = service.PrepareResume(thread.ConversationId);

        Assert.Equal("sess-1", sid);
        // Restored under the encoded cwd (C:\watched\Solution -> C--watched-Solution).
        string restored = Path.Combine(projectsRoot, "C--watched-Solution", "sess-1.jsonl");
        Assert.True(File.Exists(restored));
        Assert.Equal("SAVED", File.ReadAllText(restored));
    }

    [Fact]
    public void Prepare_resume_returns_null_for_an_unknown_thread()
    {
        (ConversationService service, _, _, _) = NewService();
        Assert.Null(service.PrepareResume("unknown"));
    }

    [Fact]
    public void Delete_thread_removes_the_row_the_primary_and_the_mirror()
    {
        (ConversationService service, _, string projectsRoot, string sessionsDir) = NewService();
        ConversationRecord thread = service.EnsureConversationForSession("sess-1");
        string transcript = WritePrimaryTranscript(projectsRoot, "sess-1");
        service.MirrorTranscript("sess-1");
        string mirror = Path.Combine(sessionsDir, service.Get(thread.ConversationId)!.TranscriptFile!);
        Assert.True(File.Exists(mirror));

        Assert.True(service.DeleteConversation(thread.ConversationId));
        Assert.Null(service.Get(thread.ConversationId));
        Assert.False(File.Exists(transcript));
        Assert.False(File.Exists(mirror));
        Assert.False(service.DeleteConversation(thread.ConversationId));
    }

    private sealed class FakeWorkspace : IConversationWorkspace
    {
        public FakeWorkspace(string threadsDatabasePath, string cwd, string sessionsDirectory)
        {
            ConversationsDatabasePath = threadsDatabasePath;
            Cwd = cwd;
            SessionsDirectory = sessionsDirectory;
        }

        public string ConversationsDatabasePath { get; }

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
