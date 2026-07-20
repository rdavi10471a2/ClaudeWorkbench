using System.Text.RegularExpressions;
using ClaudeWorkbench.Host.Console;
using ClaudeWorkbench.Host.Console.Models;
using Microsoft.AspNetCore.Components;
using Radzen;

namespace ClaudeWorkbench.Host.Components.Dialogs;

// Faithful port of the Codex StagedReviewDialog: hosted in a resizable Radzen
// dialog, session-flow (advances through each staged file in the edit session,
// no queue panel), auto-closes when the session is fully resolved. Accept writes
// the staged bytes to watched source via IReviewWorkflow; the agent never does.
public partial class MergeReviewDialog : IAsyncDisposable
{
    [Inject]
    private IReviewWorkflow Review { get; set; } = default!;

    [Inject]
    private DialogService DialogService { get; set; } = default!;

    [Inject]
    private Services.SidecarClient Sidecar { get; set; } = default!;

    [Parameter]
    public string? SessionId { get; set; }

    [Parameter]
    public bool AutoCloseWhenComplete { get; set; } = true;

    private IReadOnlyList<ReviewQueueItem> pendingItems = [];
    private ReviewRecordModel? selectedModel;
    private string? selectedRecordId;
    private string? errorMessage;
    private bool actionBusy;
    private CancellationTokenSource? sessionMonitorCts;
    private Task? sessionMonitorTask;

    private bool UseSessionFlow => !string.IsNullOrWhiteSpace(SessionId);
    private string DialogTitle => UseSessionFlow ? "Merge Review" : "Merge Review Queue";
    private string DialogSubtitle => UseSessionFlow
        ? "Review this edit session in order. It advances through each staged file; close any time to stop reviewing (unresolved files stay pending)."
        : "Review staged candidates before they become source truth.";

    private bool CanAcceptNormally => selectedModel is not null
        && !selectedModel.IsDecided
        && (!selectedModel.PreMergeValidationIsError || selectedModel.PreMergeValidationForceApproved);

    // Set when an accept attempt came back refused-but-overridable. The GATE-2 build only
    // runs at accept time and leaves the record's GATE-1 status clean, so without this the
    // override the message tells you to use would never light up for a build failure.
    private string? overrideOfferedForRecordId;

    private bool CanForceAccept => selectedModel is not null
        && !selectedModel.IsDecided
        && !selectedModel.PreMergeValidationForceApproved
        && (selectedModel.PreMergeValidationIsError
            || string.Equals(overrideOfferedForRecordId, selectedRecordId, StringComparison.Ordinal));

    protected override void OnParametersSet()
    {
        LoadReview(preserveSelection: false);
        EnsureSessionMonitor();
    }

    private void RefreshReview()
    {
        LoadReview(preserveSelection: true);
    }

    private void CloseDialog()
    {
        DialogService.Close();
    }

    private void SelectRecord(string stagedRecordId)
    {
        try
        {
            selectedModel = Review.Load(stagedRecordId);
            selectedRecordId = stagedRecordId;
            errorMessage = null;
            overrideOfferedForRecordId = null; // a fresh record has not been refused yet
        }
        catch (Exception ex)
        {
            selectedModel = null;
            selectedRecordId = null;
            errorMessage = ex.Message;
        }
    }

    private Task AcceptSelectedAsync()
    {
        return AcceptSelectedCoreAsync(forceApproveValidation: false);
    }

    private Task AcceptSelectedWithOverrideAsync()
    {
        return AcceptSelectedCoreAsync(forceApproveValidation: true);
    }

    private async Task AcceptSelectedCoreAsync(bool forceApproveValidation)
    {
        if (string.IsNullOrWhiteSpace(selectedRecordId) || actionBusy)
        {
            return;
        }

        actionBusy = true;
        try
        {
            await InvokeAsync(StateHasChanged);
            string recordId = selectedRecordId;
            ReviewActionResult result = await Task.Run(() => Review.Accept(recordId, forceApproveValidation));
            errorMessage = result.Message;
            if (result.OverrideAvailable)
            {
                overrideOfferedForRecordId = recordId;
            }

            // The accept has already written watched source by here. Telling the agent
            // how the build went is best-effort transport: if it fails it must not abort
            // the advance to the next staged file, but it must not vanish either — the
            // operator has to know the agent was never told the build failed.
            if (!string.IsNullOrWhiteSpace(result.AgentSummary)
                && !await Sidecar.PostReviewOutcomeAsync(result.AgentSummary))
            {
                errorMessage = $"{result.Message} The agent could NOT be notified of the outcome (sidecar unreachable): {result.AgentSummary}";
            }

            await LoadNextStateAsync();
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
        }
        finally
        {
            actionBusy = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task RejectSelectedAsync()
    {
        if (string.IsNullOrWhiteSpace(selectedRecordId) || actionBusy)
        {
            return;
        }

        actionBusy = true;
        try
        {
            await InvokeAsync(StateHasChanged);
            string recordId = selectedRecordId;
            ReviewActionResult result = await Task.Run(() => Review.Reject(recordId));
            errorMessage = result.Message;

            // Same contract as accept: telling the agent is best-effort transport, but a
            // failure must be visible. A rejection the agent never hears about leaves it
            // waiting on a review that already happened.
            if (!string.IsNullOrWhiteSpace(result.AgentSummary)
                && !await Sidecar.PostReviewOutcomeAsync(result.AgentSummary))
            {
                errorMessage = $"{result.Message} The agent could NOT be notified of the rejection (sidecar unreachable): {result.AgentSummary}";
            }

            await LoadNextStateAsync();
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
        }
        finally
        {
            actionBusy = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task LoadNextStateAsync()
    {
        LoadReview(preserveSelection: false);
        bool sessionComplete = UseSessionFlow && (selectedModel?.IsSessionComplete ?? false);
        await InvokeAsync(StateHasChanged);
        if (sessionComplete && AutoCloseWhenComplete)
        {
            DialogService.Close();
        }
    }

    private void EnsureSessionMonitor()
    {
        if (!UseSessionFlow)
        {
            StopSessionMonitor();
            return;
        }

        if (sessionMonitorTask is not null)
        {
            return;
        }

        sessionMonitorCts = new CancellationTokenSource();
        sessionMonitorTask = MonitorSessionAsync(sessionMonitorCts.Token);
    }

    private void StopSessionMonitor()
    {
        CancellationTokenSource? cts = sessionMonitorCts;
        sessionMonitorCts = null;
        sessionMonitorTask = null;
        cts?.Cancel();
        cts?.Dispose();
    }

    private async Task MonitorSessionAsync(CancellationToken cancellationToken)
    {
        try
        {
            using (PeriodicTimer timer = new(TimeSpan.FromSeconds(1)))
            {
                while (await timer.WaitForNextTickAsync(cancellationToken))
                {
                    await InvokeAsync(() =>
                    {
                        if (actionBusy)
                        {
                            return;
                        }

                        LoadReview(preserveSelection: true);
                        StateHasChanged();
                        if (UseSessionFlow && AutoCloseWhenComplete && (selectedModel?.IsSessionComplete ?? false))
                        {
                            DialogService.Close();
                        }
                    });
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void LoadReview(bool preserveSelection)
    {
        try
        {
            pendingItems = Review.ListPending();
            errorMessage = null;

            if (UseSessionFlow)
            {
                selectedModel = Review.LoadNextForSession(SessionId!);
                selectedRecordId = selectedModel.IsSessionComplete ? null : selectedModel.StagedRecordId;
                return;
            }

            string? recordIdToLoad = preserveSelection
                ? pendingItems.FirstOrDefault(item => item.StagedRecordId == selectedRecordId)?.StagedRecordId
                : null;
            recordIdToLoad ??= pendingItems.FirstOrDefault()?.StagedRecordId;

            if (string.IsNullOrWhiteSpace(recordIdToLoad))
            {
                selectedModel = null;
                selectedRecordId = null;
                return;
            }

            selectedModel = Review.Load(recordIdToLoad);
            selectedRecordId = recordIdToLoad;
        }
        catch (Exception ex)
        {
            pendingItems = [];
            selectedModel = null;
            selectedRecordId = null;
            errorMessage = ex.Message;
        }
    }

    private static string NormalizeLabel(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "pending" : value;
    }

    // --- diagnostic presentation -------------------------------------------------
    // The engine stores raw MSBuild lines, which point at the throwaway validation
    // overlay copy and carry the project path as a suffix:
    //   C:\...\validation\20260719T210222937-399f91f\Calculator.cs(25,16): error CS0103: … [C:\…\Sample.csproj]
    // Neither part helps the operator (the overlay file is deleted after the build),
    // so the banner shows the solution-relative file only. Purely presentational —
    // the untouched original stays on the <li> title attribute, and anything that does
    // not match the expected shape is rendered unchanged.
    private static readonly Regex DiagnosticLinePattern = new(
        @"^(?<path>.+?)\((?<span>\d+(?:,\d+){0,3})\)(?<rest>:\s.*)$",
        RegexOptions.CultureInvariant);

    // validation\<timestampTguid>\ — the per-run overlay folder the build compiles in.
    private static readonly Regex ValidationOverlayFolderPattern = new(
        @"^\d{8}T\d+-[0-9a-fA-F]+$",
        RegexOptions.CultureInvariant);

    private static string FormatDiagnostic(string diagnostic)
    {
        if (string.IsNullOrWhiteSpace(diagnostic))
        {
            return diagnostic;
        }

        try
        {
            string text = StripProjectSuffix(diagnostic.Trim());
            Match match = DiagnosticLinePattern.Match(text);
            if (!match.Success)
            {
                return diagnostic;
            }

            string path = ShortenDiagnosticPath(match.Groups["path"].Value);
            if (string.IsNullOrWhiteSpace(path))
            {
                return diagnostic;
            }

            return $"{path}({match.Groups["span"].Value}){match.Groups["rest"].Value}";
        }
        catch (Exception)
        {
            // Presentation only: an unexpected shape must never break the review banner.
            return diagnostic;
        }
    }

    private static string StripProjectSuffix(string text)
    {
        if (!text.EndsWith(']'))
        {
            return text;
        }

        int open = text.LastIndexOf('[');
        if (open <= 0)
        {
            return text;
        }

        string inside = text[(open + 1)..^1];
        bool looksLikeProjectPath = inside.EndsWith("proj", StringComparison.OrdinalIgnoreCase)
            || inside.Contains('\\')
            || inside.Contains('/');
        return looksLikeProjectPath ? text[..open].TrimEnd() : text;
    }

    private static string ShortenDiagnosticPath(string path)
    {
        string trimmed = path.Trim();
        string[] segments = trimmed.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length <= 1)
        {
            return trimmed;
        }

        // Inside the throwaway overlay: everything after validation\<run-folder>\ is
        // already the path relative to the watched solution root.
        for (int i = segments.Length - 2; i >= 1; i--)
        {
            if (string.Equals(segments[i - 1], "validation", StringComparison.OrdinalIgnoreCase)
                && ValidationOverlayFolderPattern.IsMatch(segments[i]))
            {
                return string.Join('/', segments[(i + 1)..]);
            }
        }

        // Otherwise a real watched copy: <runtime>\watched-solutions\<solution>\<relative>.
        for (int i = 0; i < segments.Length - 2; i++)
        {
            if (string.Equals(segments[i], "watched-solutions", StringComparison.OrdinalIgnoreCase))
            {
                return string.Join('/', segments[(i + 2)..]);
            }
        }

        return segments[^1];
    }

    public ValueTask DisposeAsync()
    {
        StopSessionMonitor();
        return ValueTask.CompletedTask;
    }
}
