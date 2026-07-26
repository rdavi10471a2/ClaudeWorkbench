using ClaudeWorkbench.Host.Services;

namespace ClaudeWorkbench.Host.Conversations;

// The Host-side thread lifecycle over the per-workspace threads.sqlite. It owns persistence and
// disk reclamation, NOT transport: resuming a session is an HTTP call the caller makes via
// SidecarClient.ResumeAsync using GetResumeSessionId. "Active" is computed here (the thread whose
// SessionId equals the live one) and never stored.
//
// Autosave is automatic: the sidecar's session_started event (relayed via ICurrentSession's owner)
// calls EnsureConversationForSession, so a conversation persists without any explicit save.
public sealed class ConversationService
{
    private readonly IConversationWorkspace workspace;
    private readonly ClaudeTranscriptStore transcripts;
    private readonly ICurrentSession currentSession;

    public ConversationService(
        IConversationWorkspace workspace,
        ClaudeTranscriptStore transcripts,
        ICurrentSession currentSession)
    {
        this.workspace = workspace;
        this.transcripts = transcripts;
        this.currentSession = currentSession;
    }

    // A conversation is a real, persisted thread the moment it's started from New Thread — even before
    // its first turn. This is that not-yet-lived thread's id: it has no session id until the first turn
    // adopts it. It's the Active thread while there's no live session, so the chip and the Conversations
    // board show it (and it's selectable/renamable) straight away.
    private string? pendingConversationId;

    // Raised when a NEW thread is created (New Thread or first-turn autosave), not on touch/resume/
    // adopt, so the UI can toast its name and reflect it on the top bar.
    public event Action<ConversationRecord>? ConversationCreated;

    // Raised when an existing thread's metadata changes (rename/delete), so the top-bar chip can
    // re-read the Active thread's name without waiting for a session change.
    public event Action? ConversationsChanged;

    // The deterministic default name the next thread WOULD get right now — used to prefill the New
    // Thread popup so the field is never blank.
    public string PeekNextDefaultName() => Repository().NextDefaultName(DateTime.UtcNow);

    // Start a new conversation from New Thread: persist it immediately (no session id yet) so it's the
    // Active thread and shows everywhere at once. Its first turn adopts this row via EnsureConversationForSession.
    // A blank name takes the deterministic default. Announces (toast). Returns the created record.
    public ConversationRecord StartNamedConversation(string? name) => CreatePendingThread(name, announce: true);

    // Ensure there's a current conversation thread so it can be named/shown BEFORE its first turn (the
    // fresh-start case). Idempotent and non-accumulating: return the live/pending one if any; else REUSE
    // an existing unused default-named stub (so app restarts don't pile up empty "conversation-*" threads);
    // else create a fresh default. Silent — no toast. A user-named stub is never hijacked.
    public ConversationRecord EnsureCurrentConversation()
    {
        ConversationRecord? active = ActiveConversation();
        if (active is not null)
        {
            return active;
        }

        ConversationRecord? reusable = Repository().LatestUnadoptedDefault();
        if (reusable is not null)
        {
            pendingConversationId = reusable.ConversationId;
            return reusable;
        }

        return CreatePendingThread(null, announce: false);
    }

    private ConversationRecord CreatePendingThread(string? name, bool announce)
    {
        ConversationRepository repository = Repository();
        DateTime now = DateTime.UtcNow;
        string resolved = string.IsNullOrWhiteSpace(name) ? repository.NextDefaultName(now) : name.Trim();
        ConversationRecord created = new(
            Guid.NewGuid().ToString("N"),
            resolved,
            Description: null,
            UserNote: null,
            SessionId: null,
            Cwd: workspace.Cwd,
            Status: ConversationStatus.Archived,
            CreatedAtUtc: now,
            UpdatedAtUtc: now,
            AcceptedEditRefs: []);
        repository.Upsert(created);
        pendingConversationId = created.ConversationId;
        if (announce)
        {
            ConversationCreated?.Invoke(created);
        }

        return created;
    }

    // Called when the sidecar reports a live/resumed session id. Resolve which thread owns it:
    //   1. a thread already bound to this session -> touch it (idempotent turns/resume);
    //   2. the pending New-Thread row waiting for its first turn -> ADOPT it (assign the session id);
    //   3. otherwise (e.g. the very first conversation, never through New Thread) -> create a new one.
    public ConversationRecord EnsureConversationForSession(string sessionId)
    {
        ConversationRepository repository = Repository();
        ConversationRecord? existing = repository.FindBySessionId(sessionId);
        if (existing is not null)
        {
            repository.Upsert(existing with { UpdatedAtUtc = DateTime.UtcNow });
            return existing;
        }

        // Adopt the pending New-Thread row, if one is waiting for its first turn.
        if (pendingConversationId is not null)
        {
            ConversationRecord? pending = repository.Get(pendingConversationId);
            pendingConversationId = null;
            if (pending is not null && string.IsNullOrWhiteSpace(pending.SessionId))
            {
                repository.AssignSession(pending.ConversationId, sessionId);
                return repository.Get(pending.ConversationId)!;
            }
        }

        DateTime now = DateTime.UtcNow;
        ConversationRecord created = new(
            Guid.NewGuid().ToString("N"),
            repository.NextDefaultName(now),
            Description: null,
            UserNote: null,
            SessionId: sessionId,
            Cwd: workspace.Cwd,
            Status: ConversationStatus.Archived,
            CreatedAtUtc: now,
            UpdatedAtUtc: now,
            AcceptedEditRefs: []);
        repository.Upsert(created);
        ConversationCreated?.Invoke(created);
        return created;
    }

    public IReadOnlyList<ConversationRecord> List() => Repository().List();

    public ConversationRecord? Get(string conversationId) => Repository().Get(conversationId);

    // The Active thread = the conversation you're in now. Normally the one bound to the live session;
    // between the New Thread click and its first turn there's no live session yet, so it's the pending
    // (just-started, not-yet-lived) thread. Single-operator => at most one.
    public ConversationRecord? ActiveConversation()
    {
        string? live = currentSession.CurrentSessionId;
        if (!string.IsNullOrWhiteSpace(live))
        {
            ConversationRecord? bound = Repository().FindBySessionId(live);
            if (bound is not null)
            {
                return bound;
            }
        }

        return pendingConversationId is null ? null : Repository().Get(pendingConversationId);
    }

    public void Rename(string conversationId, string name)
    {
        Repository().Rename(conversationId, name);
        ConversationsChanged?.Invoke();
    }

    public void SetDescription(string conversationId, string? description) =>
        Repository().SetDescription(conversationId, description);

    public void SetUserNote(string conversationId, string? userNote) =>
        Repository().SetUserNote(conversationId, userNote);

    public void SetStatus(string conversationId, string status) => Repository().SetStatus(conversationId, status);

    // Provenance: link the accepted staged-edit records to whichever thread is currently live.
    public void RecordAcceptedEdits(IEnumerable<string> stagedRecordIds)
    {
        string? live = currentSession.CurrentSessionId;
        if (string.IsNullOrWhiteSpace(live))
        {
            return;
        }

        ConversationRepository repository = Repository();
        ConversationRecord? thread = repository.FindBySessionId(live);
        if (thread is not null)
        {
            repository.AddEditRefs(thread.ConversationId, stagedRecordIds);
        }
    }

    // Copy the current ~/.claude transcript for a session into the app-owned runtime mirror
    // (runtime\<workspace>\sessions). The filename is a plain dated sequence (YYYY-MM-DD-N.jsonl) — NOT
    // the conversation name and NO GUID: it's never user-facing, the DB maps it back via transcript_file.
    // Assigned once (stable regardless of renames). Called after each turn to refresh the mirror. Best-effort.
    public void MirrorTranscript(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        ConversationRepository repository = Repository();
        ConversationRecord? conversation = repository.FindBySessionId(sessionId);
        if (conversation is null)
        {
            return;
        }

        string file = TranscriptFileName(conversation);
        string? written = transcripts.MirrorTo(sessionId, file, workspace.SessionsDirectory);
        if (written is not null && !string.Equals(conversation.TranscriptFile, file, StringComparison.Ordinal))
        {
            repository.SetTranscriptFile(conversation.ConversationId, file);
        }
    }

    // The conversation's mirror filename: kept once assigned (a dated name doesn't change on rename),
    // otherwise a fresh "YYYY-MM-DD-N.jsonl" using the conversation's creation date, N chosen to avoid
    // collisions in the sessions folder.
    private string TranscriptFileName(ConversationRecord conversation)
    {
        if (!string.IsNullOrWhiteSpace(conversation.TranscriptFile))
        {
            return conversation.TranscriptFile;
        }

        string date = conversation.CreatedAtUtc.ToUniversalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        string dir = workspace.SessionsDirectory;
        int suffix = 1;
        string candidate = $"{date}-{suffix}.jsonl";
        while (File.Exists(Path.Combine(dir, candidate)))
        {
            suffix++;
            candidate = $"{date}-{suffix}.jsonl";
        }

        return candidate;
    }

    // The session id to hand SidecarClient.ResumeAsync when reopening a thread. Null for a stub
    // (nothing to resume yet) or an unknown thread.
    public string? GetResumeSessionId(string conversationId)
    {
        ConversationRecord? thread = Repository().Get(conversationId);
        return string.IsNullOrWhiteSpace(thread?.SessionId) ? null : thread!.SessionId;
    }

    // Prepare a thread for resume: RESTORE the SDK's primary transcript from our app-owned mirror so
    // resume reads exactly what we saved (Claude may have swept or compacted its own copy). Returns
    // the session id to pass to SidecarClient.ResumeAsync, or null for a stub/unknown thread. No HTTP
    // here — the caller performs the resume so this service stays transport-free.
    public string? PrepareResume(string conversationId)
    {
        ConversationRecord? thread = Repository().Get(conversationId);
        if (thread is null || string.IsNullOrWhiteSpace(thread.SessionId))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(thread.TranscriptFile))
        {
            string mirror = Path.Combine(workspace.SessionsDirectory, thread.TranscriptFile);
            transcripts.RestoreFromMirror(thread.SessionId, thread.Cwd, mirror);
        }

        return thread.SessionId;
    }

    // Hard delete on demand (also the "opt out of saving" discard when leaving an unnamed thread):
    // reclaim the app-owned RUNTIME footprint for this run FIRST — the threads.sqlite row (+ provenance
    // cascade) and the sessions/<id>.jsonl mirror — because that is the disk we own and must always
    // leave clean. The ~/.claude primary is deleted last and best-effort: it lives outside our runtime
    // and may be locked or swept by Claude, so it can never block the runtime cleanup. Not watched
    // source => no governance gate. Returns false if the thread is unknown.
    public bool DeleteConversation(string conversationId)
    {
        ConversationRepository repository = Repository();
        ConversationRecord? thread = repository.Get(conversationId);
        if (thread is null)
        {
            return false;
        }

        // 1. Our runtime mirror (guarded, always runs).
        if (!string.IsNullOrWhiteSpace(thread.TranscriptFile))
        {
            DeleteMirrorFile(thread.TranscriptFile!);
        }

        // 2. Our runtime index row (+ cascade). This is the authoritative "gone from our runtime".
        bool removed = repository.Delete(conversationId);

        // If we just discarded the pending New-Thread row, drop the pointer so it isn't resurrected
        // as Active or adopted by the next turn.
        if (string.Equals(conversationId, pendingConversationId, StringComparison.Ordinal))
        {
            pendingConversationId = null;
        }

        // 3. The ~/.claude primary — outside our control, best-effort only.
        if (!string.IsNullOrWhiteSpace(thread.SessionId))
        {
            transcripts.DeleteTranscripts(thread.SessionId!);
        }

        ConversationsChanged?.Invoke();
        return removed;
    }

    // Remove the app-owned runtime mirror file (best-effort; the ~/.claude primary is handled by
    // ClaudeTranscriptStore.DeleteTranscripts).
    private void DeleteMirrorFile(string transcriptFile)
    {
        try
        {
            string mirror = Path.Combine(workspace.SessionsDirectory, transcriptFile);
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
    private ConversationRepository Repository() =>
        new(new ConversationDatabase(workspace.ConversationsDatabasePath));
}
