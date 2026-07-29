using System.IO;
using System.Windows;

namespace ClaudeWorkbench.Launcher.Wpf;

// Where the host exe / sidecar dir / instances dir / browser choice live. Auto-guessed on first
// run; editable here. Same persistence contract as the WinForms SettingsForm.
public partial class SettingsWindow : Window
{
    private readonly LauncherState state;

    public SettingsWindow(LauncherState state)
    {
        InitializeComponent();
        this.state = state;

        hostExeBox.Text = state.HostExePath;
        sidecarBox.Text = state.SidecarDirectory;
        instancesBox.Text = state.EffectiveInstancesRoot;
        customBrowserBox.Text = state.CustomBrowserPath;

        browserBox.ItemsSource = new[] { "Chrome", "Edge", "Custom (Chromium)", "Default browser" };
        browserBox.SelectedIndex = (int)state.Browser;
        SyncCustomEnabled();
    }

    // The custom path only applies to the "Custom (Chromium)" choice — gray it out otherwise.
    private void SyncCustomEnabled() => customBrowserBox.IsEnabled = browserBox.SelectedIndex == (int)BrowserKind.Custom;

    private void OnBrowserChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => SyncCustomEnabled();

    private void OnBrowseHostExe(object sender, RoutedEventArgs e) => BrowseFile(hostExeBox, "Executables|*.exe");

    private void OnBrowseCustom(object sender, RoutedEventArgs e) => BrowseFile(customBrowserBox, "Executables|*.exe");

    private void OnBrowseSidecar(object sender, RoutedEventArgs e) => BrowseFolder(sidecarBox);

    private void OnBrowseInstances(object sender, RoutedEventArgs e) => BrowseFolder(instancesBox);

    private void BrowseFile(System.Windows.Controls.TextBox target, string filter)
    {
        Microsoft.Win32.OpenFileDialog dialog = new() { Filter = filter };
        if (File.Exists(target.Text))
        {
            dialog.FileName = target.Text;
        }

        if (dialog.ShowDialog(this) == true)
        {
            target.Text = dialog.FileName;
        }
    }

    private void BrowseFolder(System.Windows.Controls.TextBox target)
    {
        Microsoft.Win32.OpenFolderDialog dialog = new();
        if (Directory.Exists(target.Text))
        {
            dialog.InitialDirectory = target.Text;
        }

        if (dialog.ShowDialog(this) == true)
        {
            target.Text = dialog.FolderName;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        state.HostExePath = hostExeBox.Text.Trim();
        state.SidecarDirectory = sidecarBox.Text.Trim();
        state.CustomBrowserPath = customBrowserBox.Text.Trim();
        state.Browser = (BrowserKind)browserBox.SelectedIndex;

        // The host exe locates the workbench, so a new one re-anchors everything else.
        state.Reanchor();

        // The box shows the computed default. Only store it if the user actually typed something
        // else — otherwise it would freeze and stop following the workbench.
        string instances = instancesBox.Text.Trim();
        state.InstancesRoot = string.Equals(
            Path.TrimEndingDirectorySeparator(instances),
            Path.TrimEndingDirectorySeparator(state.DefaultInstancesRoot),
            StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : instances;

        state.Save();
        DialogResult = true;
        Close();
    }
}
