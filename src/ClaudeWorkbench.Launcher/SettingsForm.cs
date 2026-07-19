namespace ClaudeWorkbench.Launcher;

// Where the host exe / sidecar dir / browser choice live. Auto-guessed on first run;
// editable here.
public sealed class SettingsForm : Form
{
    private readonly LauncherState state;
    private readonly TextBox hostExeBox = new() { Width = 380 };
    private readonly TextBox sidecarBox = new() { Width = 380 };
    private readonly ComboBox browserBox = new() { Width = 200, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox customBrowserBox = new() { Width = 380 };

    public SettingsForm(LauncherState state)
    {
        this.state = state;
        Text = "Launcher Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(560, 260);

        TableLayoutPanel layout = new()
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 3,
            RowCount = 5,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));

        hostExeBox.Text = state.HostExePath;
        sidecarBox.Text = state.SidecarDirectory;
        customBrowserBox.Text = state.CustomBrowserPath;
        browserBox.Items.AddRange(new object[] { "Chrome", "Edge", "Custom (Chromium)", "Default browser" });
        browserBox.SelectedIndex = (int)state.Browser;

        layout.Controls.Add(new Label { Text = "Host exe", Anchor = AnchorStyles.Left, AutoSize = true }, 0, 0);
        layout.Controls.Add(hostExeBox, 1, 0);
        layout.Controls.Add(BrowseButton(hostExeBox, pickFolder: false, "Executables|*.exe"), 2, 0);

        layout.Controls.Add(new Label { Text = "Sidecar dir", Anchor = AnchorStyles.Left, AutoSize = true }, 0, 1);
        layout.Controls.Add(sidecarBox, 1, 1);
        layout.Controls.Add(BrowseButton(sidecarBox, pickFolder: true, null), 2, 1);

        layout.Controls.Add(new Label { Text = "Browser", Anchor = AnchorStyles.Left, AutoSize = true }, 0, 2);
        layout.Controls.Add(browserBox, 1, 2);

        layout.Controls.Add(new Label { Text = "Custom path", Anchor = AnchorStyles.Left, AutoSize = true }, 0, 3);
        layout.Controls.Add(customBrowserBox, 1, 3);
        layout.Controls.Add(BrowseButton(customBrowserBox, pickFolder: false, "Executables|*.exe"), 2, 3);

        FlowLayoutPanel buttons = new() { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
        Button ok = new() { Text = "Save", DialogResult = DialogResult.OK, Width = 80 };
        Button cancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 80 };
        ok.Click += (_, _) => Persist();
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        layout.Controls.Add(buttons, 1, 4);

        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    private Button BrowseButton(TextBox target, bool pickFolder, string? filter)
    {
        Button button = new() { Text = "Browse", Width = 80 };
        button.Click += (_, _) =>
        {
            if (pickFolder)
            {
                using FolderBrowserDialog dialog = new();
                if (Directory.Exists(target.Text))
                {
                    dialog.SelectedPath = target.Text;
                }

                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    target.Text = dialog.SelectedPath;
                }
            }
            else
            {
                using OpenFileDialog dialog = new() { Filter = filter ?? "All files|*.*" };
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    target.Text = dialog.FileName;
                }
            }
        };
        return button;
    }

    private void Persist()
    {
        state.HostExePath = hostExeBox.Text.Trim();
        state.SidecarDirectory = sidecarBox.Text.Trim();
        state.CustomBrowserPath = customBrowserBox.Text.Trim();
        state.Browser = (BrowserKind)browserBox.SelectedIndex;
        state.Save();
    }
}
