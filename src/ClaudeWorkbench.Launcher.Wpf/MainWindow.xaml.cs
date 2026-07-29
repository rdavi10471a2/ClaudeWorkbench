using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using MessageBox = System.Windows.MessageBox;

namespace ClaudeWorkbench.Launcher.Wpf;

// The ClaudeWorkbench control panel: one row per workspace. Start launches the host + sidecar +
// a browser window, all inside one Job Object; Stop (or closing the launcher) kills that whole
// set together. Same behaviour as the retired WinForms MainForm — only the UI is WPF now, so
// DPI scaling and theming come for free. The engine (InstanceController, LauncherState, …) is
// shared source, unchanged.
public partial class MainWindow : Window
{
    private readonly LauncherState state = LauncherState.Load();
    private readonly Dictionary<string, InstanceController> controllers = new(StringComparer.Ordinal);
    private readonly ObservableCollection<WorkspaceRow> rows = new();
    private readonly DispatcherTimer pollTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    public MainWindow()
    {
        InitializeComponent();

        grid.ItemsSource = rows;
        AttachAuthMenu(claudeButton, AuthLauncher.Claude);
        AttachAuthMenu(gitHubButton, AuthLauncher.GitHub);
        RebuildRows();

        pollTimer.Tick += (_, _) => PollAndRefresh();
        pollTimer.Start();

        Loaded += OnLoadedClampToScreen;
        Closing += OnWindowClosing;
    }

    // WPF measures in device-independent units, so this is not DPI math — it just guarantees the
    // window never opens larger than the current work area (so it's fully on-screen and draggable),
    // while keeping a 1024x768 floor where the display allows it.
    private void OnLoadedClampToScreen(object? sender, RoutedEventArgs e)
    {
        Rect work = SystemParameters.WorkArea;
        MinWidth = Math.Min(1024, work.Width);
        MinHeight = Math.Min(768, work.Height);
        Width = Math.Clamp(1200, MinWidth, work.Width * 0.98);
        Height = Math.Clamp(820, MinHeight, work.Height * 0.98);
        Left = work.Left + (work.Width - Width) / 2;
        Top = work.Top + (work.Height - Height) / 2;
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

    private WorkspaceRow? SelectedRow() => grid.SelectedItem as WorkspaceRow;

    private WorkspaceEntry? SelectedWorkspace() => SelectedRow()?.Entry;

    // Rebuild rows when the workspace set changes (add/remove). Controllers persist in the dict so
    // a running instance keeps its controller across a rebuild.
    private void RebuildRows()
    {
        rows.Clear();
        foreach (WorkspaceEntry workspace in state.Workspaces)
        {
            WorkspaceRow row = new(workspace, ControllerFor(workspace));
            row.Refresh();
            rows.Add(row);
        }
    }

    private void PollAndRefresh()
    {
        foreach (WorkspaceRow row in rows)
        {
            row.Controller.Poll();
            row.Refresh();
        }
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

    private void OnGridDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (SelectedRow() is not null)
        {
            StartSelected();
        }
    }

    private void OnStart(object sender, RoutedEventArgs e) => StartSelected();

    private async void StartSelected()
    {
        WorkspaceEntry? workspace = SelectedWorkspace();
        if (workspace is null)
        {
            return;
        }

        InstanceController controller = ControllerFor(workspace);
        PollAndRefresh(); // reflect "starting…" immediately
        await controller.StartAsync(PortsInUse(controller));
        if (controller.Status == InstanceStatus.Error)
        {
            MessageBox.Show(this, controller.LastError ?? "Failed to start.", "Start failed",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        PollAndRefresh();
    }

    private void OnStop(object sender, RoutedEventArgs e)
    {
        WorkspaceEntry? workspace = SelectedWorkspace();
        if (workspace is not null && controllers.TryGetValue(workspace.Id, out InstanceController? controller))
        {
            controller.Stop();
            PollAndRefresh();
        }
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        Microsoft.Win32.OpenFileDialog dialog = new()
        {
            Title = "Pick a solution to watch",
            Filter = "Solutions|*.sln;*.slnx|All files|*.*",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        state.Workspaces.Add(new WorkspaceEntry
        {
            SolutionPath = dialog.FileName,
            Name = Path.GetFileNameWithoutExtension(dialog.FileName),
        });
        state.Save();
        RebuildRows();
    }

    // Greenfield bootstrap: write an empty .slnx into a chosen folder (solution name = folder name),
    // register it as a workspace, and let the operator Start it.
    private void OnNewBlankSolution(object sender, RoutedEventArgs e)
    {
        Microsoft.Win32.OpenFolderDialog dialog = new()
        {
            Title = "Pick (or create) an empty folder for the new solution",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        string folder = dialog.FolderName;
        string name = new DirectoryInfo(folder).Name;
        string slnxPath = Path.Combine(folder, name + ".slnx");

        if (File.Exists(slnxPath))
        {
            MessageBox.Show(this,
                "A solution already exists here:\r\n" + slnxPath + "\r\n\r\nUse \"Add workspace\" to watch it instead.",
                "New blank solution", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            File.WriteAllText(slnxPath, "<Solution>\r\n</Solution>\r\n");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Couldn't create the solution:\r\n" + ex.Message,
                "New blank solution", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        state.Workspaces.Add(new WorkspaceEntry { SolutionPath = slnxPath, Name = name });
        state.Save();
        RebuildRows();

        MessageBox.Show(this,
            "Created a blank solution:\r\n" + slnxPath + "\r\n\r\nSelect it and click Start to launch it — then add projects from the Source tab.",
            "New blank solution", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnRemove(object sender, RoutedEventArgs e)
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

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        SettingsWindow settings = new(state) { Owner = this };
        settings.ShowDialog();
    }

    private void OnHelp(object sender, RoutedEventArgs e)
    {
        HelpWindow help = new() { Owner = this };
        help.ShowDialog();
    }

    // ---- Auth (machine-wide; opens a terminal on the CLI's own login flow) ---------------------

    private void AttachAuthMenu(System.Windows.Controls.Button button, AuthLauncher.Provider provider)
    {
        System.Windows.Controls.ContextMenu menu = new();
        menu.Items.Add(MenuItem($"Sign in to {provider.DisplayName}…", () => RunAuth(provider, AuthLauncher.LaunchLogin)));
        menu.Items.Add(MenuItem($"Check {provider.DisplayName} status", () => RunAuth(provider, AuthLauncher.LaunchStatus)));
        menu.Items.Add(new System.Windows.Controls.Separator());
        // Sign out clears the CLI's cached credential — the only way to force a genuinely fresh login.
        menu.Items.Add(MenuItem($"Sign out of {provider.DisplayName}", () => RunAuth(provider, AuthLauncher.LaunchLogout)));

        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        button.ContextMenu = menu;
    }

    private static System.Windows.Controls.MenuItem MenuItem(string header, Action onClick)
    {
        System.Windows.Controls.MenuItem item = new() { Header = header };
        item.Click += (_, _) => onClick();
        return item;
    }

    private void OnClaudeAuth(object sender, RoutedEventArgs e) => OpenMenu(claudeButton);

    private void OnGitHubAuth(object sender, RoutedEventArgs e) => OpenMenu(gitHubButton);

    private static void OpenMenu(System.Windows.Controls.Button button)
    {
        if (button.ContextMenu is { } menu)
        {
            menu.IsOpen = true;
        }
    }

    private void RunAuth(AuthLauncher.Provider provider, Action<AuthLauncher.Provider> launch)
    {
        if (AuthLauncher.ResolveExecutable(provider) is null)
        {
            MessageBox.Show(this, provider.InstallHint, $"{provider.DisplayName} CLI not found",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            launch(provider);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this,
                $"Could not open a terminal for {provider.DisplayName} sign-in.\r\n\r\n{exception.Message}",
                "Sign-in failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---- Reset samples (restore the global samples\ tree from samples-golden\) -----------------

    private void OnResetSamples(object sender, RoutedEventArgs e)
    {
        string? hostExe = state.HostExePath;
        if (string.IsNullOrWhiteSpace(hostExe)
            || Path.GetDirectoryName(hostExe) is not string hostDir
            || Path.GetDirectoryName(hostDir) is not string root)
        {
            MessageBox.Show(this, "Can't locate the install root from the host exe path (set it in Settings).",
                "Reset samples", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string golden = Path.Combine(root, "samples-golden");
        string samples = Path.Combine(root, "samples");
        if (!Directory.Exists(golden))
        {
            MessageBox.Show(this, $"No golden backup found at:\n{golden}\n\nRun scripts\\publish-live.ps1 to create it.",
                "Reset samples", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (MessageBox.Show(this,
                "Restore ALL sample workspaces to their pristine first-publish state?\n\n"
                + "This discards every change under samples\\ (including files the agent added). "
                + "Stop any running sample instances first, or the copy will fail on locked files.",
                "Reset samples", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            RestoreFromGolden(golden, samples);
            MessageBox.Show(this, "Samples restored from the golden backup.",
                "Reset samples", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"Reset failed (is a sample instance still running and holding files?):\n\n{exception.Message}",
                "Reset samples", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // Make each sample under samples\ match samples-golden\ exactly, SKIPPING bin\ and obj\ (they
    // can be locked by a lingering Roslyn/MSBuild build host, and a full recursive delete would
    // then half-delete and blank the source). See the WinForms original for the full rationale.
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

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        pollTimer.Stop();
        // Terminate every instance's Job Object — host + sidecar + browser all die.
        foreach (InstanceController controller in controllers.Values)
        {
            controller.Dispose();
        }
    }
}
