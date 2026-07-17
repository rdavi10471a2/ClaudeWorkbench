namespace WinFormsSample.Forms;

partial class MainForm
{
    private System.ComponentModel.IContainer components = null;
    private System.Windows.Forms.Button loadButton;
    private System.Windows.Forms.Label statusLabel;

    protected override void Dispose(bool disposing)
    {
        if (disposing && (components != null))
        {
            components.Dispose();
        }
        base.Dispose(disposing);
    }

    #region Windows Form Designer generated code
    private void InitializeComponent()
    {
        this.loadButton = new System.Windows.Forms.Button();
        this.statusLabel = new System.Windows.Forms.Label();
        this.SuspendLayout();
        this.loadButton.Location = new System.Drawing.Point(12, 12);
        this.loadButton.Name = "loadButton";
        this.loadButton.Size = new System.Drawing.Size(120, 30);
        this.loadButton.Text = "Load";
        this.loadButton.Click += new System.EventHandler(this.OnLoadClicked);
        this.statusLabel.Location = new System.Drawing.Point(12, 50);
        this.statusLabel.Name = "statusLabel";
        this.statusLabel.Size = new System.Drawing.Size(200, 23);
        this.Controls.Add(this.loadButton);
        this.Controls.Add(this.statusLabel);
        this.Name = "MainForm";
        this.Text = "WinForms Sample";
        this.ResumeLayout(false);
    }
    #endregion
}
