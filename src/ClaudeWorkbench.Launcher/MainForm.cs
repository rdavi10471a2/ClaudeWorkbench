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
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(820, 420);
        MinimumSize = new Size(640, 320);

        BuildUi();
        RefreshGrid();

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
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "Workspace", FillWeight = 22 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Solution", HeaderText = "Solution", FillWeight = 46 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Port", HeaderText = "Port", FillWeight = 12 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Status", HeaderText = "Status", FillWeight = 20 });
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

        Controls.Add(grid);
        Controls.Add(toolbar);
    }

    private static Button Button(string text, EventHandler onClick)
    {
        Button button = new() { Text = text, AutoSize = true, Height = 30, Margin = new Padding(4) };
        button.Click += onClick;
        return button;
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
        RefreshGrid();
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
        RefreshGrid();
    }

    private async void StartSelected()
    {
        WorkspaceEntry? workspace = SelectedWorkspace();
        if (workspace is null)
        {
            return;
        }

        InstanceController controller = ControllerFor(workspace);
        await controller.StartAsync(PortsInUse(controller));
        if (controller.Status == InstanceStatus.Error)
        {
            MessageBox.Show(this, controller.LastError ?? "Failed to start.", "Start failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        RefreshGrid();
    }

    private void StopSelected()
    {
        WorkspaceEntry? workspace = SelectedWorkspace();
        if (workspace is not null && controllers.TryGetValue(workspace.Id, out InstanceController? controller))
        {
            controller.Stop();
            RefreshGrid();
        }
    }

    private void OnSettings()
    {
        using SettingsForm settings = new(state);
        settings.ShowDialog(this);
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

        RefreshGrid();
    }

    private void RefreshGrid()
    {
        grid.Rows.Clear();
        foreach (WorkspaceEntry workspace in state.Workspaces)
        {
            controllers.TryGetValue(workspace.Id, out InstanceController? controller);
            InstanceStatus status = controller?.Status ?? InstanceStatus.Stopped;
            string port = status is InstanceStatus.Running or InstanceStatus.Starting && controller is not null
                ? controller.HostPort.ToString()
                : "-";

            int index = grid.Rows.Add(workspace.Name, workspace.SolutionPath, port, StatusText(status));
            grid.Rows[index].Tag = workspace.Id;
            grid.Rows[index].Cells[3].Style.ForeColor = status switch
            {
                InstanceStatus.Running => Color.ForestGreen,
                InstanceStatus.Starting => Color.DarkGoldenrod,
                InstanceStatus.Error => Color.Firebrick,
                _ => Color.Gray,
            };
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
