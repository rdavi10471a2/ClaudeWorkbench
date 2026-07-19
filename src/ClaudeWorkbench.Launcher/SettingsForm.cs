namespace ClaudeWorkbench.Launcher;

// Where the host exe / sidecar dir / instances dir / browser choice live. Auto-guessed on
// first run; editable here.
public sealed class SettingsForm : Form
{
    private static readonly AnchorStyles Stretch = AnchorStyles.Left | AnchorStyles.Right;

    private readonly LauncherState state;
    private readonly TextBox hostExeBox = new() { Anchor = Stretch };
    private readonly TextBox sidecarBox = new() { Anchor = Stretch };
    private readonly TextBox instancesBox = new() { Anchor = Stretch };
    private readonly ComboBox browserBox = new() { Width = 220, DropDownStyle = ComboBoxStyle.DropDownList, Anchor = AnchorStyles.Left };
    private readonly TextBox customBrowserBox = new() { Anchor = Stretch };

    public SettingsForm(LauncherState state)
    {
        this.state = state;
        Text = "Launcher Settings";
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(620, 314);
        MinimumSize = new Size(520, 334);

        TableLayoutPanel layout = new()
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 3,
            RowCount = 6,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        // Give the input rows fixed height and let the last row (buttons) take the slack.
        for (int i = 0; i < 5; i++)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        }

        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        hostExeBox.Text = state.HostExePath;
        sidecarBox.Text = state.SidecarDirectory;
        instancesBox.Text = state.EffectiveInstancesRoot;
        customBrowserBox.Text = state.CustomBrowserPath;
        browserBox.Items.AddRange(new object[] { "Chrome", "Edge", "Custom (Chromium)", "Default browser" });
        browserBox.SelectedIndex = (int)state.Browser;
        // The custom path only applies to the "Custom (Chromium)" choice — gray it out otherwise.
        void SyncCustomEnabled() => customBrowserBox.Enabled = browserBox.SelectedIndex == (int)BrowserKind.Custom;
        browserBox.SelectedIndexChanged += (_, _) => SyncCustomEnabled();
        SyncCustomEnabled();

        layout.Controls.Add(new Label { Text = "Host exe", Anchor = AnchorStyles.Left, AutoSize = true }, 0, 0);
        layout.Controls.Add(hostExeBox, 1, 0);
        layout.Controls.Add(BrowseButton(hostExeBox, pickFolder: false, "Executables|*.exe"), 2, 0);

        layout.Controls.Add(new Label { Text = "Sidecar dir", Anchor = AnchorStyles.Left, AutoSize = true }, 0, 1);
        layout.Controls.Add(sidecarBox, 1, 1);
        layout.Controls.Add(BrowseButton(sidecarBox, pickFolder: true, null), 2, 1);

        layout.Controls.Add(new Label { Text = "Instances dir", Anchor = AnchorStyles.Left, AutoSize = true }, 0, 2);
        layout.Controls.Add(instancesBox, 1, 2);
        layout.Controls.Add(BrowseButton(instancesBox, pickFolder: true, null), 2, 2);

        layout.Controls.Add(new Label { Text = "Browser", Anchor = AnchorStyles.Left, AutoSize = true }, 0, 3);
        layout.Controls.Add(browserBox, 1, 3);

        layout.Controls.Add(new Label { Text = "Custom path", Anchor = AnchorStyles.Left, AutoSize = true }, 0, 4);
        layout.Controls.Add(customBrowserBox, 1, 4);
        layout.Controls.Add(BrowseButton(customBrowserBox, pickFolder: false, "Executables|*.exe"), 2, 4);

        FlowLayoutPanel buttons = new()
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
        };
        Button ok = new() { Text = "Save", DialogResult = DialogResult.OK, Width = 80 };
        Button cancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 80 };
        ok.Click += (_, _) => Persist();
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        layout.Controls.Add(buttons, 1, 5);
        layout.SetColumnSpan(buttons, 2);

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

        // The host exe locates the workbench, so a new one re-anchors everything else.
        state.Reanchor();

        // The box shows the computed default. Only store it if the user actually typed
        // something else — otherwise it would freeze, and stop following the workbench.
        string instances = instancesBox.Text.Trim();
        state.InstancesRoot = string.Equals(
            Path.TrimEndingDirectorySeparator(instances),
            Path.TrimEndingDirectorySeparator(state.DefaultInstancesRoot),
            StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : instances;

        state.Save();
    }
}
