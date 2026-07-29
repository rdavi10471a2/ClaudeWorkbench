using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace ClaudeWorkbench.Launcher.Wpf;

// One grid row: a workspace and its live instance state. The poll timer calls Refresh(); the
// bound properties raise PropertyChanged so the DataGrid repaints only what actually changed
// (no more hand-poking cells, the WinForms way).
public sealed class WorkspaceRow : INotifyPropertyChanged
{
    // Frozen brushes are shareable across threads and cheap to reuse each poll.
    private static readonly Brush RunningBrush = Frozen(22, 140, 80);
    private static readonly Brush StartingBrush = Frozen(176, 120, 20);
    private static readonly Brush ErrorBrush = Frozen(197, 48, 48);
    private static readonly Brush StoppedBrush = Frozen(128, 132, 140);

    public WorkspaceRow(WorkspaceEntry entry, InstanceController controller)
    {
        Entry = entry;
        Controller = controller;
    }

    public WorkspaceEntry Entry { get; }

    public InstanceController Controller { get; }

    public string Name => Entry.Name;

    public string SolutionPath => Entry.SolutionPath;

    private string port = "-";
    public string Port { get => port; private set => Set(ref port, value); }

    private string statusText = "stopped";
    public string StatusText { get => statusText; private set => Set(ref statusText, value); }

    private Brush statusBrush = StoppedBrush;
    public Brush StatusBrush { get => statusBrush; private set => Set(ref statusBrush, value); }

    // Pull the controller's current state into the bound properties.
    public void Refresh()
    {
        InstanceStatus status = Controller.Status;
        Port = status is InstanceStatus.Running or InstanceStatus.Starting
            ? Controller.HostPort.ToString()
            : "-";
        StatusText = status switch
        {
            InstanceStatus.Running => "running",
            InstanceStatus.Starting => "starting…",
            InstanceStatus.Error => "error",
            _ => "stopped",
        };
        StatusBrush = status switch
        {
            InstanceStatus.Running => RunningBrush,
            InstanceStatus.Starting => StartingBrush,
            InstanceStatus.Error => ErrorBrush,
            _ => StoppedBrush,
        };
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        SolidColorBrush brush = new(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (!Equals(field, value))
        {
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
