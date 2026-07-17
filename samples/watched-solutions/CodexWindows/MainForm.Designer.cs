using System.Drawing;
using System.Windows.Forms;

namespace CodexWindows;

partial class MainForm
{
    private System.ComponentModel.IContainer components = null!;
    private Label statusLabel = null!;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            components?.Dispose();
        }

        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();
        statusLabel = new Label();
        SuspendLayout();
        statusLabel.AutoSize = true;
        statusLabel.Location = new Point(24, 24);
        statusLabel.Name = "statusLabel";
        statusLabel.Size = new Size(39, 15);
        statusLabel.TabIndex = 0;
        statusLabel.Text = "Ready";
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(360, 120);
        Controls.Add(statusLabel);
        Name = "MainForm";
        Text = "Codex Windows Sample";
        ResumeLayout(false);
        PerformLayout();
    }
}
