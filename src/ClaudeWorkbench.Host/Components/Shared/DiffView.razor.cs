using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using Microsoft.AspNetCore.Components;

namespace ClaudeWorkbench.Host.Components.Shared;

// Shared side-by-side diff renderer (DiffPlex SideBySideDiffBuilder). Used by both the
// merge-review dialog and the Git panel so there is ONE diff view in the app. Give it
// OldText/NewText; it computes and renders the line-by-line diff with number gutters.
public partial class DiffView
{
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

    protected override void OnParametersSet()
    {
        model = new SideBySideDiffBuilder(new Differ())
            .BuildDiffModel(OldText ?? string.Empty, NewText ?? string.Empty);
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
}
