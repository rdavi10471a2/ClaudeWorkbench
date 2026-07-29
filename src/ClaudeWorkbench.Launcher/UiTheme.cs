namespace ClaudeWorkbench.Launcher;

// One place for the launcher's modern light look, shared by every form so they read as one app.
//
// DPI: forms opt into AutoScaleMode.Font (see ApplyForm) and all sizes below are LOGICAL 96-DPI
// values. Combined with PerMonitorV2 (app.manifest + csproj) WinForms scales them for the actual
// monitor and rescales on a DPI change, so nothing here multiplies by a DPI factor by hand.
internal static class UiTheme
{
    // A flat, neutral light palette with a single blue accent for the primary action.
    internal static readonly Color AppBackground = Color.FromArgb(246, 247, 249);
    internal static readonly Color Surface = Color.FromArgb(255, 255, 255);
    internal static readonly Color Border = Color.FromArgb(223, 226, 231);
    internal static readonly Color TextPrimary = Color.FromArgb(27, 28, 30);
    internal static readonly Color TextSecondary = Color.FromArgb(96, 101, 110);

    internal static readonly Color Accent = Color.FromArgb(45, 108, 223);
    internal static readonly Color AccentHover = Color.FromArgb(37, 95, 200);
    internal static readonly Color SecondaryHover = Color.FromArgb(240, 242, 245);
    internal static readonly Color SelectionBackground = Color.FromArgb(232, 240, 254);
    internal static readonly Color HeaderBackground = Color.FromArgb(249, 250, 251);
    internal static readonly Color RowAlternate = Color.FromArgb(251, 252, 253);

    // Status colours, tuned to sit on white / the light selection tint and stay legible.
    internal static readonly Color StatusRunning = Color.FromArgb(22, 140, 80);
    internal static readonly Color StatusStarting = Color.FromArgb(176, 120, 20);
    internal static readonly Color StatusError = Color.FromArgb(197, 48, 48);
    internal static readonly Color StatusStopped = Color.FromArgb(128, 132, 140);

    // Prepare a form: modern surface colour and font-based DPI scaling. Call FIRST in a ctor,
    // before controls are added, so children inherit the scaling baseline.
    internal static void ApplyForm(Form form)
    {
        form.AutoScaleMode = AutoScaleMode.Font;
        form.BackColor = AppBackground;
        form.ForeColor = TextPrimary;
    }

    // A flat button. primary => filled accent (the one call-to-action per surface); otherwise a
    // white button with a hairline border. AutoSize keeps the width to the text at any DPI/font.
    internal static Button MakeButton(string text, bool primary = false)
    {
        Button button = new()
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlatStyle = FlatStyle.Flat,
            MinimumSize = new Size(0, 32),
            Padding = new Padding(14, 6, 14, 6),
            Margin = new Padding(3, 0, 3, 0),
            UseVisualStyleBackColor = false,
            Cursor = Cursors.Hand,
            TabStop = true,
        };

        button.FlatAppearance.BorderSize = primary ? 0 : 1;
        button.FlatAppearance.BorderColor = Border;
        button.BackColor = primary ? Accent : Surface;
        button.ForeColor = primary ? Color.White : TextPrimary;
        button.FlatAppearance.MouseOverBackColor = primary ? AccentHover : SecondaryHover;
        button.FlatAppearance.MouseDownBackColor = primary ? AccentHover : SecondaryHover;
        return button;
    }

    // Turn a DataGridView into a clean, borderless list: horizontal separators only, a light
    // header, a soft selection tint (so per-cell status colours still show through), roomy rows.
    internal static void StyleGrid(DataGridView grid)
    {
        grid.EnableHeadersVisualStyles = false;
        grid.BackgroundColor = Surface;
        grid.BorderStyle = BorderStyle.None;
        grid.GridColor = Border;
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        grid.ColumnHeadersHeight = 38;
        grid.RowTemplate.Height = 34;

        grid.ColumnHeadersDefaultCellStyle.BackColor = HeaderBackground;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = TextSecondary;
        grid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI Semibold", 9f);
        grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(8, 0, 8, 0);
        grid.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;

        grid.DefaultCellStyle.BackColor = Surface;
        grid.DefaultCellStyle.ForeColor = TextPrimary;
        grid.DefaultCellStyle.Padding = new Padding(8, 0, 8, 0);
        grid.DefaultCellStyle.SelectionBackColor = SelectionBackground;
        grid.DefaultCellStyle.SelectionForeColor = TextPrimary;
        grid.AlternatingRowsDefaultCellStyle.BackColor = RowAlternate;
        grid.AlternatingRowsDefaultCellStyle.SelectionBackColor = SelectionBackground;
        grid.AlternatingRowsDefaultCellStyle.SelectionForeColor = TextPrimary;
    }
}
