using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace ClaudeWorkbench.Host.Components.Shared;

// Shared side-by-side diff renderer (DiffPlex SideBySideDiffBuilder). Used by both the
// merge-review dialog and the Git panel so there is ONE diff view in the app. Give it
// OldText/NewText; it computes and renders the line-by-line diff with number gutters.
//
// The body owns the shared VERTICAL scrollbar natively (both panes scroll together). Horizontal is
// a single dedicated bottom bar wired up in JS (attachDiffHScroll): its track is sized to the widest
// pane and scrolling it drives BOTH panes' scrollLeft, so a long line extends right inside a fixed
// 50/50 pane and both panes share one bar instead of every line carrying its own. See the .css.
public partial class DiffView : IAsyncDisposable
{
    [Inject]
    private IJSRuntime JS { get; set; } = default!;

    [Parameter]
    public string OldText { get; set; } = string.Empty;

    [Parameter]
    public string NewText { get; set; } = string.Empty;

    [Parameter]
    public string OldLabel { get; set; } = "Before";

    [Parameter]
    public string NewLabel { get; set; } = "After";

    // When true the New (proposed) column renders on the LEFT — the merge-review layout.
    // Default false = the conventional old-left / new-right (git / GitHub) layout.
    [Parameter]
    public bool NewOnLeft { get; set; }

    private SideBySideDiffModel? model;
    private ElementReference leftPane;
    private ElementReference rightPane;
    private ElementReference hBar;
    private ElementReference hBarInner;
    private IJSObjectReference? scrollModule;

    protected override void OnParametersSet()
    {
        model = new SideBySideDiffBuilder(new Differ())
            .BuildDiffModel(OldText ?? string.Empty, NewText ?? string.Empty);
    }

    // The two panes only exist when there is a diff to show. Attach the scroll-sync once they are
    // rendered; attachDiffScrollSync is idempotent (element-keyed), so re-renders are harmless.
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (LineCount == 0)
        {
            return;
        }

        scrollModule ??= await JS.InvokeAsync<IJSObjectReference>("import", "/js/sourceResize.js");
        await scrollModule.InvokeVoidAsync("attachDiffHScroll", leftPane, rightPane, hBar, hBarInner);
    }

    private int LineCount => Math.Max(model?.OldText.Lines.Count ?? 0, model?.NewText.Lines.Count ?? 0);

    private string LeftLabel => NewOnLeft ? NewLabel : OldLabel;

    private string RightLabel => NewOnLeft ? OldLabel : NewLabel;

    private DiffPiece? LeftLine(int index) => NewOnLeft ? NewLine(index) : OldLine(index);

    private DiffPiece? RightLine(int index) => NewOnLeft ? OldLine(index) : NewLine(index);

    private DiffPiece? OldLine(int index)
        => model is not null && index < model.OldText.Lines.Count ? model.OldText.Lines[index] : null;

    private DiffPiece? NewLine(int index)
        => model is not null && index < model.NewText.Lines.Count ? model.NewText.Lines[index] : null;

    private static string Num(DiffPiece? piece) => piece?.Position?.ToString() ?? string.Empty;

    private static string Text(DiffPiece? piece) => piece?.Text ?? string.Empty;

    private static string LineClass(DiffPiece? piece) => piece is null
        ? "imaginary"
        : piece.Type switch
        {
            ChangeType.Inserted => "inserted",
            ChangeType.Deleted => "deleted",
            ChangeType.Modified => "modified",
            ChangeType.Imaginary => "imaginary",
            _ => "unchanged"
        };

    public async ValueTask DisposeAsync()
    {
        if (scrollModule is not null)
        {
            try
            {
                await scrollModule.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
            }
        }
    }
}
