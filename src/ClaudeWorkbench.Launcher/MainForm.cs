using System.Diagnostics;
using System.IO;

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
        // ApplyForm first: AutoScaleMode.Font + surface colours are the baseline every child
        // control scales and themes against, so it must run before any control is created.
        UiTheme.ApplyForm(this);

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

        BuildUi();
        RebuildRows();

        pollTimer.Tick += (_, _) => PollAndRefresh();
        pollTimer.Start();
        FormClosing += OnFormClosing;
    }

    // Sizing lives here, NOT the constructor: under AutoScaleMode.Font + PerMonitorV2 the handle's
    // DPI and the auto-scale baseline are only settled once the form loads, so a size set in the
    // ctor gets rescaled unpredictably (this is why the window opened far narrower than its
    // MinimumSize). By OnLoad, DeviceDpi is real — scale the logical design sizes to this monitor
    // and CLAMP to its working area so the window always opens fully on-screen, never larger than
    // the display. (Same discipline as setting SplitContainer.SplitterDistance in Load.)
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        float scale = DeviceDpi / 96f;
        Rectangle work = Screen.FromControl(this).WorkingArea;

        // Floor of 1024x768 (logical), scaled to this monitor. MinimumSize must never exceed the
        // working area, or the window cannot fit and clips.
        Size min = new(
            Math.Min((int)(1024 * scale), work.Width),
            Math.Min((int)(768 * scale), work.Height));
        MinimumSize = min;

        int width = Math.Clamp((int)(1200 * scale), min.Width, (int)(work.Width * 0.95));
        int height = Math.Clamp((int)(820 * scale), min.Height, (int)(work.Height * 0.95));
        Size = new Size(width, height);
        Location = new Point(work.Left + (work.Width - width) / 2, work.Top + (work.Height - height) / 2);
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
        UiTheme.StyleGrid(grid);
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "Workspace", FillWeight = 20 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Solution", HeaderText = "Solution", FillWeight = 54 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Port", HeaderText = "Port", FillWeight = 10 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Status", HeaderText = "Status", FillWeight = 16 });
        grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) StartSelected(); };

        // A top command bar. A single WRAPPING flow panel: buttons sit on one row when the window
        // is wide and wrap onto further rows when it is narrow, so nothing is ever clipped at any
        // width or DPI (the panel is AutoSize, so it grows taller as rows wrap). Order runs from
        // primary workspace/lifecycle actions to machine-wide concerns (auth, reset, settings, help).
        FlowLayoutPanel commandBar = new()
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = UiTheme.Surface,
            Padding = new Padding(8, 8, 8, 8),
        };
        commandBar.Controls.Add(Button("Add workspace", (_, _) => OnAdd()));
        commandBar.Controls.Add(Button("New blank solution", (_, _) => OnNewBlankSolution()));
        commandBar.Controls.Add(Button("Start", (_, _) => StartSelected(), primary: true));
        commandBar.Controls.Add(Button("Stop", (_, _) => StopSelected()));
        commandBar.Controls.Add(Button("Remove", (_, _) => OnRemove()));
        // Auth is a machine-wide concern (the CLIs cache their login under the user profile), not a
        // per-workspace one, so these sit on the launcher bar rather than in any instance. Each opens
        // a terminal on the CLI's own interactive login — see AuthLauncher for why a terminal.
        commandBar.Controls.Add(AuthButton("Claude sign-in", AuthLauncher.Claude));
        commandBar.Controls.Add(AuthButton("GitHub sign-in", AuthLauncher.GitHub));
        // Test convenience: restore the shared samples\ workspaces to the pristine copy publish
        // lays down in samples-golden\, discarding whatever the fixture runs edited.
        commandBar.Controls.Add(Button("Reset samples", (_, _) => OnResetSamples()));
        commandBar.Controls.Add(Button("Settings", (_, _) => OnSettings()));
        commandBar.Controls.Add(Button("Help", (_, _) => OnHelp()));

        // Hairline under the command bar to separate it from the list.
        Panel separator = new() { Dock = DockStyle.Top, Height = 1, BackColor = UiTheme.Border };

        // Inset the grid so it reads as a card on the app background rather than edge-to-edge chrome.
        Panel gridHost = new() { Dock = DockStyle.Fill, Padding = new Padding(12), BackColor = UiTheme.AppBackground };
        gridHost.Controls.Add(grid);

        // Add the fill control first (lowest z-order => laid out last, takes the remaining space),
        // then the top edges. Result, top to bottom: commandBar, separator, grid.
        Controls.Add(gridHost);
        Controls.Add(separator);
        Controls.Add(commandBar);
    }

    private static Button Button(string text, EventHandler onClick, bool primary = false)
    {
        Button button = UiTheme.MakeButton(text, primary);
        button.Click += onClick;
        return button;
    }

    // Restore the GLOBAL samples\ tree from the pristine samples-golden\ mirror publish writes,
    // discarding any edits the fixture runs made (added files included). The install root is two
    // folders up from the host exe (<root>\host\ClaudeWorkbench.Host.exe).
    private void OnResetSamples()
    {
        string? hostExe = state.HostExePath;
        if (string.IsNullOrWhiteSpace(hostExe)
            || Path.GetDirectoryName(hostExe) is not string hostDir
            || Path.GetDirectoryName(hostDir) is not string root)
        {
            MessageBox.Show(this, "Can't locate the install root from the host exe path (set it in Settings).",
                "Reset samples", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        string golden = Path.Combine(root, "samples-golden");
        string samples = Path.Combine(root, "samples");
        if (!Directory.Exists(golden))
        {
            MessageBox.Show(this, $"No golden backup found at:\n{golden}\n\nRun scripts\\publish-live.ps1 to create it.",
                "Reset samples", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (MessageBox.Show(this,
                "Restore ALL sample workspaces to their pristine first-publish state?\n\n"
                + "This discards every change under samples\\ (including files the agent added). "
                + "Stop any running sample instances first, or the copy will fail on locked files.",
                "Reset samples", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            return;
        }

        try
        {
            RestoreFromGolden(golden, samples);
            MessageBox.Show(this, "Samples restored from the golden backup.",
                "Reset samples", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"Reset failed (is a sample instance still running and holding files?):\n\n{exception.Message}",
                "Reset samples", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // Make each sample under samples\ match samples-golden\ exactly: wipe the SOURCE then re-copy,
    // so agent-added files are removed rather than left behind by an overwrite-only copy.
    //
    // Deliberately SKIPS bin\ and obj\. The host indexes/builds the watched sample IN PLACE, so
    // build output accumulates here and can be held open by a lingering Roslyn BuildHost or a
    // compiler/MSBuild server. A full recursive delete then collides with that lock, half-deletes
    // the tree, and BLANKS the source. bin/obj are derived (rebuilt on next build) and the golden
    // carries none, so leaving them is correct as well as safe. This mirrors the smoke's
    // resetFixture, which skips build output and has never hit this failure. See the file-locking
    // diagnosis. (Top-level bin/obj only; the sample fixtures are single-project.)
    private static void RestoreFromGolden(string golden, string samples)
    {
        Directory.CreateDirectory(samples);
        foreach (string goldenSample in Directory.GetDirectories(golden))
        {
            string target = Path.Combine(samples, Path.GetFileName(goldenSample));
            if (Directory.Exists(target))
            {
                WipeExceptBuildOutput(target);
            }

            CopyTree(goldenSample, target);
        }
    }

    // Delete every top-level entry under root except bin\ and obj\ (see RestoreFromGolden).
    private static void WipeExceptBuildOutput(string root)
    {
        foreach (string entry in Directory.GetFileSystemEntries(root))
        {
            string name = Path.GetFileName(entry);
            if (name is "bin" or "obj")
            {
                continue;
            }

            if (Directory.Exists(entry))
            {
                Directory.Delete(entry, recursive: true);
            }
            else
            {
                File.Delete(entry);
            }
        }
    }

    private static void CopyTree(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (string file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
        }

        foreach (string dir in Directory.GetDirectories(source))
        {
            CopyTree(dir, Path.Combine(dest, Path.GetFileName(dir)));
        }
    }

    // An auth button drops a small menu: sign in, or check status. Both first confirm the CLI is
    // installed, so a missing tool produces a clear dialog instead of a console that flashes
    // "command not found" and vanishes.
    private Button AuthButton(string text, AuthLauncher.Provider provider)
    {
        Button button = UiTheme.MakeButton(text);

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

    // Greenfield bootstrap: write an empty .slnx into a chosen folder (solution name = folder name),
    // register it as a workspace, and let the operator Start it. Projects are then added from the
    // Blazor Source tab. The Launcher stays dumb — it only creates the blank solution file.
    private void OnNewBlankSolution()
    {
        using FolderBrowserDialog dialog = new()
        {
            Description = "Pick (or create) an empty folder for the new solution",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        string folder = dialog.SelectedPath;
        string name = new DirectoryInfo(folder).Name;
        string slnxPath = Path.Combine(folder, name + ".slnx");

        if (File.Exists(slnxPath))
        {
            MessageBox.Show(
                this,
                "A solution already exists here:\r\n" + slnxPath + "\r\n\r\nUse \"Add workspace\" to watch it instead.",
                "New blank solution",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        try
        {
            File.WriteAllText(slnxPath, "<Solution>\r\n</Solution>\r\n");
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                "Couldn't create the solution:\r\n" + ex.Message,
                "New blank solution",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        WorkspaceEntry entry = new()
        {
            SolutionPath = slnxPath,
            Name = name,
        };
        state.Workspaces.Add(entry);
        state.Save();
        RebuildRows();

        MessageBox.Show(
            this,
            "Created a blank solution:\r\n" + slnxPath + "\r\n\r\nSelect it and click Start to launch it — then add projects from the Source tab.",
            "New blank solution",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
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
                    InstanceStatus.Running => UiTheme.StatusRunning,
                    InstanceStatus.Starting => UiTheme.StatusStarting,
                    InstanceStatus.Error => UiTheme.StatusError,
                    _ => UiTheme.StatusStopped,
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
