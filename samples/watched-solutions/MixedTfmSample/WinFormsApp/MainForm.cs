using Shared;

namespace WinFormsApp;

public sealed class MainForm : Form
{
    public MainForm()
    {
        Text = "MixedTfmSample - WinForms (net9.0)";
        Controls.Add(new Label
        {
            AutoSize = true,
            Text = SharedGreeter.Greet("WinForms (net9.0)"),
        });
    }
}
