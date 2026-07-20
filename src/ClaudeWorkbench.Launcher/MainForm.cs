using System.Diagnostics;

namespace ClaudeWorkbench.Launcher;

// The ClaudeWorkbench control panel: one row per workspace. Start launches the host +
// sidecar + a browser window, all inside one Job Object; Stop (or closing the launcher)
// kills that whole set together. Closing the browser window stops the backend from the
// other side (the host's CWB_EXIT_WITH_BROWSER).
public sealed class MainForm : Form
{
    private readonly LauncherState state = LauncherState.Load();
    private readonly Dictionary<string, InstanceController> controllers = new(StringComparer.Ordinal);
    private readonly DataGridView grid = new();
    private readonly System.Windows.Forms.Timer pollTimer = new() { Interval = 2000 };

    public MainForm()
    {
        Text = "ClaudeWorkbench Launcher";

        // ApplicationIcon puts the mark on the .exe (Explorer, taskbar, the desktop shortcut), but
        // Form.Icon does not inherit it — left alone the window keeps the default WinForms icon.
        // Reading it back off our own executable avoids embedding the same .ico a second time.
        try
        {
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        }
        catch
        {
            // Cosmetic only; a missing window icon must never stop the launcher opening.
        }

        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1060, 480);
        MinimumSize = new Size(760, 340);

        BuildUi();
        RebuildRows();

        pollTimer.Tick += (_, _) => PollAndRefresh();
        pollTimer.Start();
        FormClosing += OnFormClosing;
    }

    private void BuildUi()
    {
        grid.Dock = DockStyle.Fill;
        grid.ReadOnly = true;
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.AllowUserToResizeRows = false;
        grid.MultiSelect = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.RowHeadersVisible = false;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        grid.ShowCellToolTips = true; // full solution path on hover when truncated
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "Workspace", FillWeight = 20 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Solution", HeaderText = "Solution", FillWeight = 54 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Port", HeaderText = "Port", FillWeight = 10 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Status", HeaderText = "Status", FillWeight = 16 });
        grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) StartSelected(); };

        FlowLayoutPanel toolbar = new()
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.LeftToRight,
            Height = 44,
            Padding = new Padding(6),
        };
        toolbar.Controls.Add(Button("Add workspace", (_, _) => OnAdd()));
        toolbar.Controls.Add(Button("Start", (_, _) => StartSelected()));
        toolbar.Controls.Add(Button("Stop", (_, _) => StopSelected()));
        toolbar.Controls.Add(Button("Remove", (_, _) => OnRemove()));
        toolbar.Controls.Add(Button("Settings", (_, _) => OnSettings()));
        toolbar.Controls.Add(Button("Help", (_, _) => OnHelp()));

        // Auth is a machine-wide concern (the CLIs cache their login under the user profile), not a
        // per-workspace one, so these sit on the launcher toolbar rather than in any instance. Each
        // opens a terminal on the CLI's own interactive login — see AuthLauncher for why a terminal.
        toolbar.Controls.Add(AuthButton("Claude sign-in", AuthLauncher.Claude));
        toolbar.Controls.Add(AuthButton("GitHub sign-in", AuthLauncher.GitHub));

        Controls.Add(grid);
        Controls.Add(toolbar);
    }

    private static Button Button(string text, EventHandler onClick)
    {
        Button button = new() { Text = text, AutoSize = true, Height = 30, Margin = new Padding(4) };
        button.Click += onClick;
        return button;
    }

    // An auth button drops a small menu: sign in, or check status. Both first confirm the CLI is
    // installed, so a missing tool produces a clear dialog instead of a console that flashes
    // "command not found" and vanishes.
    private Button AuthButton(string text, AuthLauncher.Provider provider)
    {
        Button button = new() { Text = text, AutoSize = true, Height = 30, Margin = new Padding(4) };

        ContextMenuStrip menu = new();
        menu.Items.Add($"Sign in to {provider.DisplayName}…", null, (_, _) => RunAuth(provider, AuthLauncher.LaunchLogin));
        menu.Items.Add($"Check {provider.DisplayName} status", null, (_, _) => RunAuth(provider, AuthLauncher.LaunchStatus));
        menu.Items.Add(new ToolStripSeparator());
        // Sign out clears the CLI's cached credential (~/.claude for Claude). This is the only way
        // to force a genuinely fresh login: `login` on an already-authenticated CLI can short-circuit.
        menu.Items.Add($"Sign out of {provider.DisplayName}", null, (_, _) => RunAuth(provider, AuthLauncher.LaunchLogout));

        // Show the menu directly under the button, so the click that opens it reads as the button's.
        button.Click += (_, _) => menu.Show(button, new Point(0, button.Height));
        return button;
    }

    private void RunAuth(AuthLauncher.Provider provider, Action<AuthLauncher.Provider> launch)
    {
        if (AuthLauncher.ResolveExecutable(provider) is null)
        {
            MessageBox.Show(
                this,
                provider.InstallHint,
                $"{provider.DisplayName} CLI not found",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        try
        {
            launch(provider);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                $"Could not open a terminal for {provider.DisplayName} sign-in.\r\n\r\n{exception.Message}",
                "Sign-in failed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private InstanceController ControllerFor(WorkspaceEntry workspace)
    {
        if (!controllers.TryGetValue(workspace.Id, out InstanceController? controller))
        {
            controller = new InstanceController(workspace, state);
            controllers[workspace.Id] = controller;
        }

        return controller;
    }

    private WorkspaceEntry? SelectedWorkspace()
    {
        if (grid.CurrentRow?.Tag is string id)
        {
            return state.Workspaces.FirstOrDefault(w => w.Id == id);
        }

        return null;
    }

    private void OnAdd()
    {
        using OpenFileDialog dialog = new()
        {
            Title = "Pick a solution to watch",
            Filter = "Solutions|*.sln;*.slnx|All files|*.*",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        WorkspaceEntry entry = new()
        {
            SolutionPath = dialog.FileName,
            Name = Path.GetFileNameWithoutExtension(dialog.FileName),
        };
        state.Workspaces.Add(entry);
        state.Save();
        RebuildRows();
    }

    private void OnRemove()
    {
        WorkspaceEntry? workspace = SelectedWorkspace();
        if (workspace is null)
        {
            return;
        }

        if (controllers.TryGetValue(workspace.Id, out InstanceController? controller))
        {
            controller.Stop();
            controller.Dispose();
            controllers.Remove(workspace.Id);
        }

        state.Workspaces.Remove(workspace);
        state.Save();
        RebuildRows();
    }

    private async void StartSelected()
    {
        WorkspaceEntry? workspace = SelectedWorkspace();
        if (workspace is null)
        {
            return;
        }

        InstanceController controller = ControllerFor(workspace);
        UpdateStatuses(); // reflect "starting…" immediately
        await controller.StartAsync(PortsInUse(controller));
        if (controller.Status == InstanceStatus.Error)
        {
            MessageBox.Show(this, controller.LastError ?? "Failed to start.", "Start failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        UpdateStatuses();
    }

    private void StopSelected()
    {
        WorkspaceEntry? workspace = SelectedWorkspace();
        if (workspace is not null && controllers.TryGetValue(workspace.Id, out InstanceController? controller))
        {
            controller.Stop();
            UpdateStatuses();
        }
    }

    private void OnSettings()
    {
        using SettingsForm settings = new(state);
        settings.ShowDialog(this);
    }

    private void OnHelp()
    {
        using HelpForm help = new();
        help.ShowDialog(this);
    }

    private IEnumerable<int> PortsInUse(InstanceController? except)
    {
        foreach (InstanceController controller in controllers.Values)
        {
            if (ReferenceEquals(controller, except) || controller.Status == InstanceStatus.Stopped)
            {
                continue;
            }

            yield return controller.HostPort;
            yield return controller.SidecarPort;
        }
    }

    private void PollAndRefresh()
    {
        foreach (InstanceController controller in controllers.Values)
        {
            controller.Poll();
        }

        UpdateStatuses();
    }

    // Structural: rebuild rows when the workspace set changes (add/remove). Clears the
    // selection, which is fine for an explicit add/remove.
    private void RebuildRows()
    {
        grid.Rows.Clear();
        foreach (WorkspaceEntry workspace in state.Workspaces)
        {
            int index = grid.Rows.Add(workspace.Name, workspace.SolutionPath, "-", StatusText(InstanceStatus.Stopped));
            grid.Rows[index].Tag = workspace.Id;
            grid.Rows[index].Cells[1].ToolTipText = workspace.SolutionPath;
        }

        UpdateStatuses();
    }

    // In-place: update only the port/status cells on each poll, so selection and scroll
    // are preserved (no more jerking) and cells only repaint when a value actually changes.
    private void UpdateStatuses()
    {
        foreach (DataGridViewRow row in grid.Rows)
        {
            if (row.Tag is not string id)
            {
                continue;
            }

            controllers.TryGetValue(id, out InstanceController? controller);
            InstanceStatus status = controller?.Status ?? InstanceStatus.Stopped;
            string port = status is InstanceStatus.Running or InstanceStatus.Starting && controller is not null
                ? controller.HostPort.ToString()
                : "-";
            string statusText = StatusText(status);

            if (!Equals(row.Cells[2].Value, port))
            {
                row.Cells[2].Value = port;
            }

            if (!Equals(row.Cells[3].Value, statusText))
            {
                row.Cells[3].Value = statusText;
                row.Cells[3].Style.ForeColor = status switch
                {
                    InstanceStatus.Running => Color.ForestGreen,
                    InstanceStatus.Starting => Color.DarkGoldenrod,
                    InstanceStatus.Error => Color.Firebrick,
                    _ => Color.Gray,
                };
            }
        }
    }

    private static string StatusText(InstanceStatus status) => status switch
    {
        InstanceStatus.Running => "running",
        InstanceStatus.Starting => "starting…",
        InstanceStatus.Error => "error",
        _ => "stopped",
    };

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        pollTimer.Stop();
        // Terminate every instance's Job Object — host + sidecar + browser all die.
        foreach (InstanceController controller in controllers.Values)
        {
            controller.Dispose();
        }
    }
}
