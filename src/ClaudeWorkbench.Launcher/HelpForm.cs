namespace ClaudeWorkbench.Launcher;

// What the launcher is and how its lifecycle works.
public sealed class HelpForm : Form
{
    private const string HelpText =
        "ClaudeWorkbench Launcher\r\n" +
        "Run several ClaudeWorkbench instances side by side — one watched solution per window,\r\n" +
        "each fully isolated (its own port, its own runtime and index).\r\n" +
        "\r\n" +
        "Workspaces\r\n" +
        "  • Add workspace — pick a .sln/.slnx to watch. It gets a free port and an isolated runtime.\r\n" +
        "  • Start — launches that workspace's backend (host + sidecar) and opens a browser window.\r\n" +
        "  • Stop — shuts that instance down.\r\n" +
        "  • Remove — takes the workspace off the list.\r\n" +
        "  • Settings — host exe path, sidecar folder, and which browser to open.\r\n" +
        "\r\n" +
        "Lifecycle (kill one, kill all)\r\n" +
        "  • Closing the browser window stops that instance's backend on its own, within a few seconds.\r\n" +
        "  • Stop — or closing the Launcher — terminates that instance's host + sidecar + browser together.\r\n" +
        "  • A Launcher crash can't orphan a backend: everything is held in a Windows Job Object that\r\n" +
        "    dies with the Launcher.\r\n" +
        "\r\n" +
        "Browser choice (Settings)\r\n" +
        "  • Chrome / Edge — opens a clean app window (via --app) that closes as a unit.\r\n" +
        "  • Custom (Chromium) — use another Chromium-based browser: Brave, Vivaldi, Opera, a portable\r\n" +
        "    Chromium, or a Chrome/Edge in a non-standard location. Put its .exe in the \"Custom path\"\r\n" +
        "    box; it's launched with the same --app flags. Ignored unless \"Custom\" is selected.\r\n" +
        "    (Firefox is not supported here — it has no --app equivalent.)\r\n" +
        "  • Default browser — just opens a tab; it can't be force-closed from here (closing it still\r\n" +
        "    stops the backend).\r\n" +
        "\r\n" +
        "Notes\r\n" +
        "  • The first agent turn may wait a moment while a large solution builds its index.\r\n" +
        "  • Each instance's host output is captured to <instance>\\host.log for troubleshooting.";

    public HelpForm()
    {
        Text = "ClaudeWorkbench Launcher — Help";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(680, 460);
        MinimumSize = new Size(520, 360);

        TextBox body = new()
        {
            Multiline = true,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            Text = HelpText,
            Font = new Font("Segoe UI", 9.5f),
            BackColor = SystemColors.Window,
            Margin = new Padding(0),
        };
        body.Select(0, 0);

        Panel host = new() { Dock = DockStyle.Fill, Padding = new Padding(14), BackColor = SystemColors.Window };
        host.Controls.Add(body);

        FlowLayoutPanel buttons = new() { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 44, Padding = new Padding(8) };
        Button close = new() { Text = "Close", DialogResult = DialogResult.OK, Width = 88 };
        buttons.Controls.Add(close);

        Controls.Add(host);
        Controls.Add(buttons);
        AcceptButton = close;
        CancelButton = close;
    }
}
