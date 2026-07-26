using ClaudeWorkbench.Host.Conversations;

namespace ClaudeWorkbench.Host.Tests;

// Hermetic tests for the per-workspace thread index (threads.sqlite). Each test uses a throwaway
// temp database, so nothing touches a real workspace. These prove the persistence contract the
// thread lifecycle is built on: round-trip, the deterministic default name, status/kind moves,
// stub -> session assignment, provenance links, and hard delete.
public sealed class ConversationRepositoryTests : IDisposable
{
    private readonly List<string> tempDirs = new();

    private ConversationRepository NewRepository()
    {
        string dir = Path.Combine(Path.GetTempPath(), "cwb-threads-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        tempDirs.Add(dir);
        return new ConversationRepository(new ConversationDatabase(Path.Combine(dir, "threads.sqlite")));
    }

    private static ConversationRecord NewThread(
        string conversationId,
        string name,
        string? sessionId = null,
        string status = ConversationStatus.Archived)
    {
        DateTime now = DateTime.UtcNow;
        return new ConversationRecord(
            conversationId,
            name,
            "a description",
            "a note",
            sessionId,
            @"C:\watched\Solution",
            status,
            now,
            now,
            []);
    }

    [Fact]
    public void Upsert_then_get_round_trips_all_fields()
    {
        ConversationRepository repo = NewRepository();
        ConversationRecord thread = NewThread("t1", "conversation-2026-07-25-1", sessionId: "sess-abc");

        repo.Upsert(thread);
        ConversationRecord? loaded = repo.Get("t1");

        Assert.NotNull(loaded);
        Assert.Equal("t1", loaded!.ConversationId);
        Assert.Equal("conversation-2026-07-25-1", loaded.Name);
        Assert.Equal("a description", loaded.Description);
        Assert.Equal("a note", loaded.UserNote);
        Assert.Equal("sess-abc", loaded.SessionId);
        Assert.Equal(@"C:\watched\Solution", loaded.Cwd);
        Assert.Equal(ConversationStatus.Archived, loaded.Status);
        Assert.False(loaded.IsStub);
        // Timestamps round-trip to the second (round-trip "o" format).
        Assert.Equal(thread.CreatedAtUtc, loaded.CreatedAtUtc, TimeSpan.FromMilliseconds(2));
    }

    [Fact]
    public void Get_returns_null_for_unknown_thread()
    {
        ConversationRepository repo = NewRepository();
        Assert.Null(repo.Get("nope"));
    }

    [Fact]
    public void Upsert_is_idempotent_update_on_same_id()
    {
        ConversationRepository repo = NewRepository();
        ConversationRecord thread = NewThread("t1", "first");
        repo.Upsert(thread);
        repo.Upsert(thread with { Name = "second" });

        Assert.Single(repo.List());
        Assert.Equal("second", repo.Get("t1")!.Name);
    }

    [Fact]
    public void Stub_has_no_session_and_reads_as_stub()
    {
        ConversationRepository repo = NewRepository();
        repo.Upsert(NewThread("stub", "unstarted one", sessionId: null));

        ConversationRecord? loaded = repo.Get("stub");
        Assert.NotNull(loaded);
        Assert.True(loaded!.IsStub);
        Assert.Null(loaded.SessionId);
        Assert.Equal(ConversationStatus.Archived, loaded.Status);
    }

    [Fact]
    public void Assign_session_moves_a_stub_to_having_a_session()
    {
        ConversationRepository repo = NewRepository();
        repo.Upsert(NewThread("stub", "unstarted one", sessionId: null));

        repo.AssignSession("stub", "sess-live");

        Assert.Equal("sess-live", repo.Get("stub")!.SessionId);
        Assert.False(repo.Get("stub")!.IsStub);
    }

    [Fact]
    public void Rename_description_and_note_updates_persist()
    {
        ConversationRepository repo = NewRepository();
        repo.Upsert(NewThread("t1", "orig"));

        repo.Rename("t1", "renamed");
        repo.SetDescription("t1", "new desc");
        repo.SetUserNote("t1", null);

        ConversationRecord loaded = repo.Get("t1")!;
        Assert.Equal("renamed", loaded.Name);
        Assert.Equal("new desc", loaded.Description);
        Assert.Null(loaded.UserNote);
    }

    [Fact]
    public void Invalid_status_is_rejected()
    {
        ConversationRepository repo = NewRepository();
        repo.Upsert(NewThread("t1", "orig"));
        Assert.Throws<ArgumentException>(() => repo.SetStatus("t1", "active"));
    }

    [Fact]
    public void List_returns_newest_updated_first()
    {
        ConversationRepository repo = NewRepository();
        DateTime baseTime = DateTime.UtcNow.AddMinutes(-10);
        repo.Upsert(NewThread("old", "old") with { UpdatedAtUtc = baseTime });
        repo.Upsert(NewThread("new", "new") with { UpdatedAtUtc = baseTime.AddMinutes(5) });

        IReadOnlyList<ConversationRecord> list = repo.List();
        Assert.Equal(2, list.Count);
        Assert.Equal("new", list[0].ConversationId);
        Assert.Equal("old", list[1].ConversationId);
    }

    [Fact]
    public void Next_default_name_uses_the_day_and_increments_by_thread()
    {
        ConversationRepository repo = NewRepository();
        DateTime day = new(2026, 7, 25, 9, 0, 0, DateTimeKind.Utc);

        string first = repo.NextDefaultName(day);
        Assert.Equal("conversation-2026-07-25-1", first);

        // Once a thread exists for that day, the next default increments.
        repo.Upsert(NewThread("t1", first) with { CreatedAtUtc = day });
        Assert.Equal("conversation-2026-07-25-2", repo.NextDefaultName(day));

        // A different day restarts the count.
        Assert.Equal("conversation-2026-07-26-1", repo.NextDefaultName(day.AddDays(1)));
    }

    [Theory]
    [InlineData("conversation-2026-07-25-1", true)]
    [InlineData("conversation-2026-12-01-42", true)]
    [InlineData("conversation-2026-7-5-1", false)]      // unpadded month/day is not the generated form
    [InlineData("add Dapper + repository", false)]
    [InlineData("conversation", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Is_default_name_matches_only_the_generated_default_form(string? name, bool expected)
    {
        // New Thread relies on this to decide whether the conversation being left is still "unnamed"
        // (a machine default the operator never touched) and therefore offered for naming-or-discard.
        Assert.Equal(expected, ConversationRepository.IsDefaultName(name));
    }

    [Fact]
    public void Next_default_name_uses_max_suffix_plus_one_not_a_reusable_count()
    {
        ConversationRepository repo = NewRepository();
        DateTime day = new(2026, 7, 25, 9, 0, 0, DateTimeKind.Utc);

        // Two defaults exist for the day; delete the FIRST so a plain count (1 -> +1 = 2) would collide
        // with the surviving "-2". Max-suffix+1 must jump to "-3".
        repo.Upsert(NewThread("t1", "conversation-2026-07-25-1") with { CreatedAtUtc = day });
        repo.Upsert(NewThread("t2", "conversation-2026-07-25-2") with { CreatedAtUtc = day });
        repo.Delete("t1");

        Assert.Equal("conversation-2026-07-25-3", repo.NextDefaultName(day));
    }

    [Fact]
    public void Latest_unadopted_default_finds_the_reusable_stub_and_skips_others()
    {
        ConversationRepository repo = NewRepository();
        // A named stub (no session) — intentional, must NOT be reused.
        repo.Upsert(NewThread("named", "My plan", sessionId: null));
        // An adopted default (has a session) — not reusable.
        repo.Upsert(NewThread("adopted", "conversation-2026-07-25-1", sessionId: "sess-x"));
        // A default-named stub with no session — the reusable one.
        repo.Upsert(NewThread("stub", "conversation-2026-07-25-2", sessionId: null));

        ConversationRecord? reusable = repo.LatestUnadoptedDefault();

        Assert.NotNull(reusable);
        Assert.Equal("stub", reusable!.ConversationId);
    }

    [Fact]
    public void Latest_unadopted_default_is_null_when_none_qualify()
    {
        ConversationRepository repo = NewRepository();
        repo.Upsert(NewThread("named", "My plan", sessionId: null));
        repo.Upsert(NewThread("adopted", "conversation-2026-07-25-1", sessionId: "sess-x"));

        Assert.Null(repo.LatestUnadoptedDefault());
    }

    [Fact]
    public void Edit_refs_are_recorded_deduped_and_loaded_with_the_thread()
    {
        ConversationRepository repo = NewRepository();
        repo.Upsert(NewThread("t1", "orig"));

        repo.AddEditRefs("t1", ["rec-1", "rec-2"]);
        repo.AddEditRefs("t1", ["rec-2", "rec-3"]); // rec-2 is a duplicate

        IReadOnlyList<string> refs = repo.Get("t1")!.AcceptedEditRefs;
        Assert.Equal(3, refs.Count);
        Assert.Contains("rec-1", refs);
        Assert.Contains("rec-2", refs);
        Assert.Contains("rec-3", refs);
    }

    [Fact]
    public void Delete_removes_the_thread_and_cascades_its_edit_refs()
    {
        ConversationRepository repo = NewRepository();
        repo.Upsert(NewThread("t1", "orig"));
        repo.AddEditRefs("t1", ["rec-1"]);

        Assert.True(repo.Delete("t1"));
        Assert.Null(repo.Get("t1"));
        Assert.False(repo.Delete("t1")); // second delete is a no-op

        // Re-creating the id must not resurrect the cascaded provenance rows.
        repo.Upsert(NewThread("t1", "reborn"));
        Assert.Empty(repo.Get("t1")!.AcceptedEditRefs);
    }

    [Fact]
    public void Find_by_session_id_returns_the_matching_thread()
    {
        ConversationRepository repo = NewRepository();
        repo.Upsert(NewThread("t1", "one", sessionId: "sess-1"));
        repo.Upsert(NewThread("t2", "two", sessionId: "sess-2"));

        Assert.Equal("t2", repo.FindBySessionId("sess-2")!.ConversationId);
        Assert.Null(repo.FindBySessionId("sess-missing"));
        Assert.Null(repo.FindBySessionId(""));
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
