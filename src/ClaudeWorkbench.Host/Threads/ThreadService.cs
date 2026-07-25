using ClaudeWorkbench.Host.Services;

namespace ClaudeWorkbench.Host.Threads;

// The Host-side thread lifecycle over the per-workspace threads.sqlite. It owns persistence and
// disk reclamation, NOT transport: resuming a session is an HTTP call the caller makes via
// SidecarClient.ResumeAsync using GetResumeSessionId. "Active" is computed here (the thread whose
// SessionId equals the live one) and never stored.
//
// Autosave is automatic: the sidecar's session_started event (relayed via ICurrentSession's owner)
// calls EnsureThreadForSession, so a conversation persists without any explicit save.
public sealed class ThreadService
{
    private readonly IThreadWorkspace workspace;
    private readonly ClaudeTranscriptStore transcripts;
    private readonly ICurrentSession currentSession;

    public ThreadService(
        IThreadWorkspace workspace,
        ClaudeTranscriptStore transcripts,
        ICurrentSession currentSession)
    {
        this.workspace = workspace;
        this.transcripts = transcripts;
        this.currentSession = currentSession;
    }

    // Called when the sidecar reports a live/resumed session id. If a thread already owns this
    // session (either autosaved earlier or a resumed one), touch it; otherwise persist a new
    // thread with the deterministic default name. Idempotent.
    public ThreadRecord EnsureThreadForSession(string sessionId)
    {
        ThreadRepository repository = Repository();
        ThreadRecord? existing = repository.FindBySessionId(sessionId);
        if (existing is not null)
        {
            repository.Upsert(existing with { UpdatedAtUtc = DateTime.UtcNow });
            return existing;
        }

        DateTime now = DateTime.UtcNow;
        ThreadRecord created = new(
            Guid.NewGuid().ToString("N"),
            repository.NextDefaultName(now),
            Description: null,
            UserNote: null,
            SessionId: sessionId,
            Cwd: workspace.Cwd,
            Status: ThreadStatus.Archived,
            Kind: ThreadKind.Discussion,
            CreatedAtUtc: now,
            UpdatedAtUtc: now,
            AcceptedEditRefs: []);
        repository.Upsert(created);
        return created;
    }

    // A named pre-conversation stub (Planned) with no session yet — the one useful board affordance.
    public ThreadRecord CreateStub(string? name, string? description)
    {
        ThreadRepository repository = Repository();
        DateTime now = DateTime.UtcNow;
        ThreadRecord stub = new(
            Guid.NewGuid().ToString("N"),
            string.IsNullOrWhiteSpace(name) ? repository.NextDefaultName(now) : name.Trim(),
            Description: description,
            UserNote: null,
            SessionId: null,
            Cwd: workspace.Cwd,
            Status: ThreadStatus.Planned,
            Kind: ThreadKind.Discussion,
            CreatedAtUtc: now,
            UpdatedAtUtc: now,
            AcceptedEditRefs: []);
        repository.Upsert(stub);
        return stub;
    }

    public IReadOnlyList<ThreadRecord> List() => Repository().List();

    public ThreadRecord? Get(string threadId) => Repository().Get(threadId);

    // The Active thread = the one whose session is the current live one (single-operator => one).
    public ThreadRecord? ActiveThread()
    {
        string? live = currentSession.CurrentSessionId;
        return string.IsNullOrWhiteSpace(live) ? null : Repository().FindBySessionId(live);
    }

    public void Rename(string threadId, string name) => Repository().Rename(threadId, name);

    public void SetDescription(string threadId, string? description) =>
        Repository().SetDescription(threadId, description);

    public void SetUserNote(string threadId, string? userNote) =>
        Repository().SetUserNote(threadId, userNote);

    public void SetStatus(string threadId, string status) => Repository().SetStatus(threadId, status);

    public void SetKind(string threadId, string kind) => Repository().SetKind(threadId, kind);

    // Provenance: link the accepted staged-edit records to whichever thread is currently live.
    public void RecordAcceptedEdits(IEnumerable<string> stagedRecordIds)
    {
        string? live = currentSession.CurrentSessionId;
        if (string.IsNullOrWhiteSpace(live))
        {
            return;
        }

        ThreadRepository repository = Repository();
        ThreadRecord? thread = repository.FindBySessionId(live);
        if (thread is not null)
        {
            repository.AddEditRefs(thread.ThreadId, stagedRecordIds);
        }
    }

    // Copy the current ~/.claude transcript for a session into the app-owned runtime mirror
    // (runtime\<workspace>\sessions). Called after each turn so the mirror stays current. Best-effort.
    public void MirrorTranscript(string sessionId)
    {
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            transcripts.MirrorTo(sessionId, workspace.SessionsDirectory);
        }
    }

    // The session id to hand SidecarClient.ResumeAsync when reopening a thread. Null for a stub
    // (nothing to resume yet) or an unknown thread.
    public string? GetResumeSessionId(string threadId)
    {
        ThreadRecord? thread = Repository().Get(threadId);
        return string.IsNullOrWhiteSpace(thread?.SessionId) ? null : thread!.SessionId;
    }

    // Prepare a thread for resume: RESTORE the SDK's primary transcript from our app-owned mirror so
    // resume reads exactly what we saved (Claude may have swept or compacted its own copy). Returns
    // the session id to pass to SidecarClient.ResumeAsync, or null for a stub/unknown thread. No HTTP
    // here — the caller performs the resume so this service stays transport-free.
    public string? PrepareResume(string threadId)
    {
        ThreadRecord? thread = Repository().Get(threadId);
        if (thread is null || string.IsNullOrWhiteSpace(thread.SessionId))
        {
            return null;
        }

        string mirror = Path.Combine(workspace.SessionsDirectory, thread.SessionId + ".jsonl");
        transcripts.RestoreFromMirror(thread.SessionId, thread.Cwd, mirror);
        return thread.SessionId;
    }

    // Hard delete on demand: remove the DB row (+ provenance, cascade) AND the ~/.claude transcript
    // JSONL to reclaim disk. Not watched source => no governance gate. Returns false if unknown.
    public bool DeleteThread(string threadId)
    {
        ThreadRepository repository = Repository();
        ThreadRecord? thread = repository.Get(threadId);
        if (thread is null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(thread.SessionId))
        {
            // Remove BOTH copies: the ~/.claude primary and the app-owned runtime mirror.
            transcripts.DeleteTranscripts(thread.SessionId);
            DeleteMirror(thread.SessionId);
        }

        return repository.Delete(threadId);
    }

    // Remove the app-owned runtime mirror for a session (best-effort; the ~/.claude primary is
    // handled by ClaudeTranscriptStore.DeleteTranscripts).
    private void DeleteMirror(string sessionId)
    {
        try
        {
            string mirror = Path.Combine(workspace.SessionsDirectory, sessionId + ".jsonl");
            if (File.Exists(mirror))
            {
                File.Delete(mirror);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // A fresh repository bound to the CURRENT workspace each call, so the service follows an
    // operator workspace switch. EnsureCreated is idempotent and cheap.
    private ThreadRepository Repository() =>
        new(new ThreadDatabase(workspace.ThreadsDatabasePath));
}
