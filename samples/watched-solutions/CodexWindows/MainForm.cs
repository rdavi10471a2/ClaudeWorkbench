using CodexWindows.Configuration;
using System.Windows.Forms;

namespace CodexWindows;

public partial class MainForm : Form
{
    private readonly AppConfig appConfig;

    public MainForm(AppConfig appConfig)
    {
        this.appConfig = appConfig;
        InitializeComponent();
        Text = appConfig.WindowTitle;
        statusLabel.Text = appConfig.InitiaWorkflowTestPassed3
            ? "Ready"
            : "Not ready";
    }
}
