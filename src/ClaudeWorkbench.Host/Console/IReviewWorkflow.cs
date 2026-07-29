using ClaudeWorkbench.Host.Console.Models;

namespace ClaudeWorkbench.Host.Console;

// Operator-facing review of staged edits. The agent stages candidates through the
// governed MCP; this surface lets the operator review the diff and accept/reject.
// Accept is the only place watched source is written, and it is an operator action
// (the Blazor accept handler), never the agent.
public interface IReviewWorkflow
{
    IReadOnlyList<ReviewQueueItem> ListPending();

    ReviewRecordModel Load(string stagedRecordId);

    ReviewRecordModel LoadNextForSession(string sessionId);

    // rebuildIndex is honored ONLY on the terminal accept (the one that writes the session).
    // Default true = current behavior. False defers the (expensive) post-accept index rebuild:
    // the files still write, but the index goes stale until the next reindex — for tight
    // single-file/markup loops where the cross-file graph isn't needed yet.
    //
    // buildAfterAccept, when true, runs a real in-place `dotnet build` after the terminal accept so the
    // watched tree's own bin holds a runnable artifact. The configuration is not the caller's to choose —
    // it is always Debug (IndexRidesBuild.IndexBuildConfiguration); Debug/Release lives on the Source tab,
    // which owns build/run of the app and never feeds the index. False = no build (the default; the
    // programmatic/agent accept path never builds). Best-effort: the source is already written, so a build
    // failure is reported, not rolled back. Sequenced after the reindex (they run serially — no MSBuild
    // handle contention). runAfterAccept launches the built executable after a successful build (requires
    // buildAfterAccept — you can't run what wasn't built). Off by default. Operator-only.
    ReviewActionResult Accept(
        string stagedRecordId,
        bool forceApproveValidation,
        bool rebuildIndex = true,
        bool buildAfterAccept = false,
        bool runAfterAccept = false);

    ReviewActionResult Reject(string stagedRecordId);
}
